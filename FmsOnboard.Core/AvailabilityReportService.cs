using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FmsOnboard.Core;

public static class AvailabilityReportService
{
    public static string DeviceLabel(string dtype) => dtype switch
    {
        "R" => "Radio",
        "D" => "Display",
        "P" => "PLE",
        "G" => "GPS",
        _ => "Unknown"
    };

    private static double? AvailabilityPct(double uptime, double observed) =>
        observed > 0.0 ? (uptime / observed * 100.0) : null;

    private static double CoveragePct(double observed, double expected) =>
        expected <= 0.0 ? 0.0 : Math.Clamp(observed / expected * 100.0, 0.0, 100.0);

    private sealed class Agg
    {
        public long TargetCount;
        public long DevicesWithData;
        public double UptimeSeconds;
        public double DowntimeSeconds;
        public double ObservedSeconds;
    }

    public static Dictionary<string, object?> BuildReport(AppConfig cfg, DateOnly reportDate)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, cfg.Timezone);
        if (reportDate > DateOnly.FromDateTime(nowLocal))
            throw new InvalidOperationException("Tanggal laporan tidak boleh berada di masa depan.");

        var dateIso = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var machineMap = MachineMapService.LoadMachineMapForReport(cfg);
        var excludeOids = DatabaseService.GetBdDays(cfg.DbFile, dateIso, dateIso).Select(x => x.Oid).ToHashSet();

        bool satMode = GpsMetricService.SatelliteMode(cfg);
        var types = Constants.DeviceTypes
            .Where(t => machineMap.Values.Any(m => m.GetIp(t) is not null) || (t == "G" && satMode && machineMap.Count > 0))
            .ToList();
        var overallTypes = Constants.OverallDeviceTypes
            .Where(t => machineMap.Values.Any(m => m.GetIp(t) is not null))
            .ToList();

        double periodSeconds = DatabaseService.CountedSecondsInDay(reportDate, nowUtc, cfg.Timezone, cfg.BreakStart, cfg.BreakEnd);
        var availMap = DatabaseService.GetDailyAvailabilityMap(cfg.DbFile, dateIso);
        var statusMap = DatabaseService.GetLatestStatusMap(cfg.DbFile);
        var latestScan = DatabaseService.GetLatestScan(cfg.DbFile);

        var aggregate = overallTypes.ToDictionary(t => t, _ => new Agg());
        var gpsAgg = new Agg();

        var sortedMachines = machineMap
            .OrderBy(kv => kv.Value.Name, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key)
            .ToList();

        var unitRows = new List<Dictionary<string, object?>>(sortedMachines.Count);

        foreach (var (oid, machine) in sortedMachines)
        {
            bool excluded = excludeOids.Contains(oid);
            var devicesObj = new Dictionary<string, Dictionary<string, object?>>();
            double unitUp = 0, unitDown = 0, unitObs = 0;
            long configuredCount = 0;

            foreach (var dtype in types)
            {
                string? ip = (dtype == "G" && satMode) ? Constants.GpsAvailabilityKey : machine.GetIp(dtype);
                double up = 0, dn = 0, ob = 0;

                if (ip is not null)
                {
                    var key = (oid, dtype, ip);
                    if (availMap.TryGetValue(key, out var a))
                    {
                        up = a.UptimeSeconds;
                        dn = a.DowntimeSeconds;
                        ob = a.ObservedSeconds;
                    }
                    statusMap.TryGetValue(key, out var st);

                    string? lastChk = null;
                    if (st is not null)
                    {
                        var dtUtc = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(st.LastCheckedAt)).UtcDateTime;
                        lastChk = TimeZoneInfo.ConvertTimeFromUtc(dtUtc, cfg.Timezone).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                    }

                    devicesObj[dtype] = new Dictionary<string, object?>
                    {
                        ["device_type"] = dtype,
                        ["ip_address"] = ip,
                        ["status"] = st?.Status ?? "N/A",
                        ["is_available"] = st?.IsAvailable,
                        ["packet_loss_pct"] = st?.PacketLossPct,
                        ["packets_sent"] = st?.PacketsSent,
                        ["packets_received"] = st?.PacketsReceived,
                        ["avg_rtt_ms"] = st?.AvgRttMs,
                        ["last_checked_at"] = lastChk,
                        ["uptime_seconds"] = up,
                        ["downtime_seconds"] = dn,
                        ["observed_seconds"] = ob,
                        ["availability_pct"] = AvailabilityPct(up, ob),
                        ["coverage_pct"] = CoveragePct(ob, periodSeconds)
                    };
                }
                else
                {
                    devicesObj[dtype] = new Dictionary<string, object?>
                    {
                        ["device_type"] = dtype,
                        ["ip_address"] = null,
                        ["status"] = "N/A",
                        ["is_available"] = null,
                        ["packet_loss_pct"] = null,
                        ["packets_sent"] = null,
                        ["packets_received"] = null,
                        ["avg_rtt_ms"] = null,
                        ["last_checked_at"] = null,
                        ["uptime_seconds"] = 0.0,
                        ["downtime_seconds"] = 0.0,
                        ["observed_seconds"] = 0.0,
                        ["availability_pct"] = null,
                        ["coverage_pct"] = 0.0
                    };
                }

                if (dtype == "G" && ip is not null && !excluded)
                {
                    gpsAgg.TargetCount++;
                    if (ob > 0.0) gpsAgg.DevicesWithData++;
                    gpsAgg.UptimeSeconds += up;
                    gpsAgg.DowntimeSeconds += dn;
                    gpsAgg.ObservedSeconds += ob;
                }

                if (!overallTypes.Contains(dtype)) continue;

                if (ip is not null) configuredCount++;
                if (!excluded && aggregate.TryGetValue(dtype, out var agg))
                {
                    if (ip is not null) agg.TargetCount++;
                    if (ob > 0.0) agg.DevicesWithData++;
                    agg.UptimeSeconds += up;
                    agg.DowntimeSeconds += dn;
                    agg.ObservedSeconds += ob;
                }

                unitUp += up;
                unitDown += dn;
                unitObs += ob;
            }

            double unitExpected = periodSeconds * configuredCount;
            unitRows.Add(new Dictionary<string, object?>
            {
                ["machine_oid"] = oid,
                ["machine_name"] = machine.Name,
                ["devices"] = devicesObj,
                ["unit_uptime_seconds"] = unitUp,
                ["unit_downtime_seconds"] = unitDown,
                ["unit_observed_seconds"] = unitObs,
                ["unit_availability_pct"] = AvailabilityPct(unitUp, unitObs),
                ["unit_coverage_pct"] = CoveragePct(unitObs, unitExpected),
                ["configured_device_count"] = configuredCount,
                ["excluded"] = excluded
            });
        }

        unitRows.Sort((a, b) =>
        {
            var av = a["unit_availability_pct"] as double?;
            var bv = b["unit_availability_pct"] as double?;
            int cmp = (av, bv) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                (double x, double y) => x.CompareTo(y)
            };
            return cmp != 0
                ? cmp
                : string.CompareOrdinal(a["machine_name"]?.ToString() ?? "", b["machine_name"]?.ToString() ?? "");
        });

        for (int i = 0; i < unitRows.Count; i++)
            unitRows[i]["rank"] = i + 1;

        var overallObj = new Dictionary<string, Dictionary<string, object?>>();
        long totalTargets = 0, totalWithData = 0;
        double totalUp = 0, totalDown = 0, totalObs = 0;

        foreach (var dtype in overallTypes)
        {
            var agg = aggregate[dtype];
            double expected = periodSeconds * agg.TargetCount;
            totalTargets += agg.TargetCount;
            totalWithData += agg.DevicesWithData;
            totalUp += agg.UptimeSeconds;
            totalDown += agg.DowntimeSeconds;
            totalObs += agg.ObservedSeconds;

            overallObj[dtype] = new Dictionary<string, object?>
            {
                ["target_count"] = agg.TargetCount,
                ["devices_with_data"] = agg.DevicesWithData,
                ["uptime_seconds"] = agg.UptimeSeconds,
                ["downtime_seconds"] = agg.DowntimeSeconds,
                ["observed_seconds"] = agg.ObservedSeconds,
                ["expected_seconds"] = expected,
                ["availability_pct"] = AvailabilityPct(agg.UptimeSeconds, agg.ObservedSeconds),
                ["coverage_pct"] = CoveragePct(agg.ObservedSeconds, expected)
            };
        }

        double totalExpected = periodSeconds * totalTargets;
        overallObj["combined"] = new Dictionary<string, object?>
        {
            ["target_count"] = totalTargets,
            ["devices_with_data"] = totalWithData,
            ["uptime_seconds"] = totalUp,
            ["downtime_seconds"] = totalDown,
            ["observed_seconds"] = totalObs,
            ["expected_seconds"] = totalExpected,
            ["availability_pct"] = AvailabilityPct(totalUp, totalObs),
            ["coverage_pct"] = CoveragePct(totalObs, totalExpected)
        };

        Dictionary<string, object?>? gpsOverall = null;
        if (gpsAgg.TargetCount > 0)
        {
            double exp = periodSeconds * gpsAgg.TargetCount;
            gpsOverall = new Dictionary<string, object?>
            {
                ["target_count"] = gpsAgg.TargetCount,
                ["devices_with_data"] = gpsAgg.DevicesWithData,
                ["uptime_seconds"] = gpsAgg.UptimeSeconds,
                ["downtime_seconds"] = gpsAgg.DowntimeSeconds,
                ["observed_seconds"] = gpsAgg.ObservedSeconds,
                ["expected_seconds"] = exp,
                ["availability_pct"] = AvailabilityPct(gpsAgg.UptimeSeconds, gpsAgg.ObservedSeconds),
                ["coverage_pct"] = CoveragePct(gpsAgg.ObservedSeconds, exp)
            };
        }

        Dictionary<string, object?>? scanInfo = null;
        if (latestScan is not null)
        {
            string? ToLocal(double? ts)
            {
                if (!ts.HasValue) return null;
                var dtUtc = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(ts.Value)).UtcDateTime;
                return TimeZoneInfo.ConvertTimeFromUtc(dtUtc, cfg.Timezone).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }

            scanInfo = new Dictionary<string, object?>
            {
                ["id"] = latestScan.Id,
                ["started_at"] = latestScan.StartedAt,
                ["finished_at"] = latestScan.FinishedAt,
                ["duration_seconds"] = latestScan.DurationSeconds,
                ["target_count"] = latestScan.TargetCount,
                ["checked_count"] = latestScan.CheckedCount,
                ["scan_status"] = latestScan.ScanStatus,
                ["error_message"] = latestScan.ErrorMessage,
                ["started_local"] = ToLocal(latestScan.StartedAt),
                ["finished_local"] = ToLocal(latestScan.FinishedAt)
            };
        }

        return new Dictionary<string, object?>
        {
            ["date"] = dateIso,
            ["timezone"] = cfg.TimezoneName,
            ["generated_at"] = nowLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["period_seconds"] = periodSeconds,
            ["unit_count"] = machineMap.Count,
            ["device_types"] = types,
            ["overall_device_types"] = overallTypes,
            ["excluded_count"] = machineMap.Keys.Count(oid => excludeOids.Contains(oid)),
            ["latest_status_count"] = statusMap.Count,
            ["latest_scan"] = scanInfo,
            ["overall"] = overallObj,
            ["gps_overall"] = gpsOverall,
            ["units"] = unitRows
        };
    }

    public static Dictionary<string, object?> BuildOverallPayload(AppConfig cfg, DateOnly reportDate)
    {
        var report = BuildReport(cfg, reportDate);
        return new Dictionary<string, object?>
        {
            ["date"] = report["date"],
            ["overall"] = report["overall"],
            ["gps_overall"] = report["gps_overall"],
            ["excluded_count"] = report["excluded_count"]
        };
    }

    private static void ApplyAvailabilityConditionalFormatting(IXLRange range)
    {
        range.AddConditionalFormat().WhenGreaterThan(90.0)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#00E676"))
            .Font.SetFontColor(XLColor.FromHtml("#064E3B"))
            .Font.SetBold();
        range.AddConditionalFormat().WhenBetween(80.0, 90.0)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#FFD600"))
            .Font.SetFontColor(XLColor.FromHtml("#3F3500"))
            .Font.SetBold();
        range.AddConditionalFormat().WhenLessThan(80.0)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#FF1744"))
            .Font.SetFontColor(XLColor.White)
            .Font.SetBold();
    }

    public static byte[] BuildXlsxBytes(Dictionary<string, object?> report)
    {
        using var wb = new XLWorkbook();
        var types = (List<string>)report["device_types"]!;
        var overallTypes = (List<string>)report["overall_device_types"]!;
        var overall = (Dictionary<string, Dictionary<string, object?>>)report["overall"]!;
        var units = (List<Dictionary<string, object?>>)report["units"]!;

        var ws1 = wb.Worksheets.Add("Overall Summary");
        var titleRange = ws1.Range(1, 1, 1, 8).Merge();
        titleRange.Value = "NETWORK AVAILABILITY - OVERALL SUMMARY";
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 14;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#182433");
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws1.Row(1).Height = 28;

        ws1.Cell(2, 1).Value = "Tanggal";
        ws1.Cell(2, 2).Value = report["date"]?.ToString() ?? "";
        ws1.Cell(2, 4).Value = "Timezone";
        ws1.Cell(2, 5).Value = report["timezone"]?.ToString() ?? "";
        ws1.Cell(2, 7).Value = "Jumlah unit";
        ws1.Cell(2, 8).Value = Convert.ToDouble(report["unit_count"] ?? 0);

        string[] sumHeaders = ["Perangkat", "Target IP", "IP Ada Data", "Uptime (jam)", "Downtime (jam)", "Observed (jam)", "Coverage (%)", "Availability (%)"];
        for (int c = 0; c < sumHeaders.Length; c++)
        {
            var cell = ws1.Cell(4, c + 1);
            cell.Value = sumHeaders[c];
            StyleHeader(cell, "#234A69");
        }
        ws1.Row(4).Height = 30;

        var keys = overallTypes.Select(t => (Key: t, Label: $"{t} - {DeviceLabel(t)}")).ToList();
        keys.Add(("combined", $"{string.Join(" + ", overallTypes)} (Gabungan)"));

        for (int i = 0; i < keys.Count; i++)
        {
            int r = 5 + i;
            var item = overall[keys[i].Key];
            ws1.Cell(r, 1).Value = keys[i].Label;
            ws1.Cell(r, 2).Value = Convert.ToDouble(item["target_count"] ?? 0);
            ws1.Cell(r, 3).Value = Convert.ToDouble(item["devices_with_data"] ?? 0);
            SetNumCell(ws1.Cell(r, 4), Convert.ToDouble(item["uptime_seconds"] ?? 0) / 3600.0, "0.00");
            SetNumCell(ws1.Cell(r, 5), Convert.ToDouble(item["downtime_seconds"] ?? 0) / 3600.0, "0.00");
            SetNumCell(ws1.Cell(r, 6), Convert.ToDouble(item["observed_seconds"] ?? 0) / 3600.0, "0.00");
            SetNumCell(ws1.Cell(r, 7), Convert.ToDouble(item["coverage_pct"] ?? 0), "0.00%");

            if (item["availability_pct"] is double av)
                SetNumCell(ws1.Cell(r, 8), av, "0.00%");
            else
                ws1.Cell(r, 8).Value = "N/A";
        }
        if (keys.Count > 0) ApplyAvailabilityConditionalFormatting(ws1.Range(5, 8, 4 + keys.Count, 8));
        ws1.SheetView.FreezeRows(4);
        ws1.Column(1).Width = 26;
        for (int c = 2; c <= 8; c++) ws1.Column(c).Width = 18;

        var ws2 = wb.Worksheets.Add("Unit Details");
        var detHeaders = new List<string> { "No", "OID", "Nama Unit" };
        foreach (var t in types)
            detHeaders.AddRange([$"IP {t}", $"Availability {t} (%)", $"Uptime {t} (jam)", $"Downtime {t} (jam)", $"Coverage {t} (%)"]);
        var joinedOverall = string.Join("+", overallTypes);
        detHeaders.Add($"Availability Unit {joinedOverall} (%)");
        detHeaders.Add($"Coverage Unit {joinedOverall} (%)");

        for (int c = 0; c < detHeaders.Count; c++)
        {
            var cell = ws2.Cell(1, c + 1);
            cell.Value = detHeaders[c];
            StyleHeader(cell, "#234A69");
        }
        ws2.Row(1).Height = 30;

        for (int i = 0; i < units.Count; i++)
        {
            int r = 2 + i;
            var u = units[i];
            bool excl = u["excluded"] is true;
            ws2.Cell(r, 1).Value = Convert.ToDouble(u["rank"] ?? i + 1);
            ws2.Cell(r, 2).Value = Convert.ToDouble(u["machine_oid"] ?? 0);
            ws2.Cell(r, 3).Value = $"{u["machine_name"]}{(excl ? " (BD)" : "")}";

            var devs = (Dictionary<string, Dictionary<string, object?>>)u["devices"]!;
            int col = 4;
            foreach (var t in types)
            {
                var d = devs[t];
                ws2.Cell(r, col).Value = d["ip_address"]?.ToString() ?? "-";
                if (d["availability_pct"] is double av) SetNumCell(ws2.Cell(r, col + 1), av, "0.00%");
                else ws2.Cell(r, col + 1).Value = "N/A";
                SetNumCell(ws2.Cell(r, col + 2), Convert.ToDouble(d["uptime_seconds"] ?? 0) / 3600.0, "0.00");
                SetNumCell(ws2.Cell(r, col + 3), Convert.ToDouble(d["downtime_seconds"] ?? 0) / 3600.0, "0.00");
                SetNumCell(ws2.Cell(r, col + 4), Convert.ToDouble(d["coverage_pct"] ?? 0), "0.00%");
                col += 5;
            }
            if (u["unit_availability_pct"] is double uav) SetNumCell(ws2.Cell(r, col), uav, "0.00%");
            else ws2.Cell(r, col).Value = "N/A";
            SetNumCell(ws2.Cell(r, col + 1), Convert.ToDouble(u["unit_coverage_pct"] ?? 0), "0.00%");
        }

        if (units.Count > 0)
        {
            int lastRow = units.Count + 1;
            for (int i = 0; i < types.Count; i++)
                ApplyAvailabilityConditionalFormatting(ws2.Range(2, 4 + i * 5 + 1, lastRow, 4 + i * 5 + 1));
            int uavCol = 4 + types.Count * 5;
            ApplyAvailabilityConditionalFormatting(ws2.Range(2, uavCol, lastRow, uavCol));
            ws2.Range(1, 1, lastRow, detHeaders.Count).SetAutoFilter();
        }
        ws2.SheetView.Freeze(1, 3);

        var ws3 = wb.Worksheets.Add("Unit Summary");
        var usHeaders = new List<string> { "No", "OID", "Nama Unit" };
        foreach (var t in types) usHeaders.Add($"Availability {t} (%)");
        usHeaders.AddRange([
            $"Availability Gabungan {joinedOverall} (%)",
            $"Coverage Gabungan {joinedOverall} (%)",
            $"Uptime Gabungan {joinedOverall} (jam)",
            $"Downtime Gabungan {joinedOverall} (jam)",
            $"Observed Gabungan {joinedOverall} (jam)"
        ]);

        for (int c = 0; c < usHeaders.Count; c++)
        {
            var cell = ws3.Cell(1, c + 1);
            cell.Value = usHeaders[c];
            StyleHeader(cell, "#234A69");
        }
        ws3.Row(1).Height = 30;

        for (int i = 0; i < units.Count; i++)
        {
            int r = 2 + i;
            var u = units[i];
            bool excl = u["excluded"] is true;
            ws3.Cell(r, 1).Value = Convert.ToDouble(u["rank"] ?? i + 1);
            ws3.Cell(r, 2).Value = Convert.ToDouble(u["machine_oid"] ?? 0);
            ws3.Cell(r, 3).Value = $"{u["machine_name"]}{(excl ? " (BD)" : "")}";

            var devs = (Dictionary<string, Dictionary<string, object?>>)u["devices"]!;
            int col = 4;
            foreach (var t in types)
            {
                if (devs[t]["availability_pct"] is double av) SetNumCell(ws3.Cell(r, col), av, "0.00%");
                else ws3.Cell(r, col).Value = "N/A";
                col++;
            }
            if (u["unit_availability_pct"] is double uav) SetNumCell(ws3.Cell(r, col), uav, "0.00%");
            else ws3.Cell(r, col).Value = "N/A";
            SetNumCell(ws3.Cell(r, col + 1), Convert.ToDouble(u["unit_coverage_pct"] ?? 0), "0.00%");
            SetNumCell(ws3.Cell(r, col + 2), Convert.ToDouble(u["unit_uptime_seconds"] ?? 0) / 3600.0, "0.00");
            SetNumCell(ws3.Cell(r, col + 3), Convert.ToDouble(u["unit_downtime_seconds"] ?? 0) / 3600.0, "0.00");
            SetNumCell(ws3.Cell(r, col + 4), Convert.ToDouble(u["unit_observed_seconds"] ?? 0) / 3600.0, "0.00");
        }
        if (units.Count > 0)
            ApplyAvailabilityConditionalFormatting(ws3.Range(2, 4, units.Count + 1, 4 + types.Count));
        ws3.SheetView.Freeze(1, 3);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    internal static void StyleHeader(IXLCell cell, string hexBg)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(hexBg);
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Alignment.WrapText = true;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }

    internal static void SetNumCell(IXLCell cell, double val, string fmt)
    {
        cell.Value = val;
        cell.Style.NumberFormat.Format = fmt;
    }

    public static byte[] BuildPdfBytes(Dictionary<string, object?> report)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var types = (List<string>)report["device_types"]!;
        var overallTypes = (List<string>)report["overall_device_types"]!;
        var overall = (Dictionary<string, Dictionary<string, object?>>)report["overall"]!;
        var units = (List<Dictionary<string, object?>>)report["units"]!;

        var sumKeys = overallTypes.Select(t => (Key: t, Label: $"{t} - {DeviceLabel(t)}")).ToList();
        sumKeys.Add(("combined", $"GABUNGAN {string.Join("+", overallTypes)}"));

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A3.Landscape());
                page.Margin(12, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontSize(7.5f).FontFamily("Helvetica"));

                page.Header().Column(col =>
                {
                    col.Item().Text("NETWORK AVAILABILITY & SQLITE STATUS REPORT").Bold().FontSize(14);
                    col.Item().Text(
                        $"Tanggal: {report["date"]} | Timezone: {report["timezone"]} | Dibuat: {report["generated_at"]} | Total unit: {report["unit_count"]} | Excluded BD: {report["excluded_count"]}")
                        .FontSize(8.5f);
                    col.Item().PaddingBottom(6);
                });

                page.Content().Column(col =>
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(2.5f);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1);
                            c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.2f);
                            c.RelativeColumn(1.2f);
                        });

                        table.Header(h =>
                        {
                            foreach (var hdr in new[] { "PERANGKAT", "TARGET", "ADA DATA", "UPTIME(h)", "DOWNTIME(h)", "COVERAGE", "AVAILABILITY" })
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(3).Text(hdr).Bold();
                        });

                        foreach (var (key, label) in sumKeys)
                        {
                            var item = overall[key];
                            string av = item["availability_pct"] is double d ? $"{d:F2}%" : "N/A";
                            table.Cell().Padding(2).Text(label);
                            table.Cell().Padding(2).Text($"{item["target_count"]}");
                            table.Cell().Padding(2).Text($"{item["devices_with_data"]}");
                            table.Cell().Padding(2).Text($"{Convert.ToDouble(item["uptime_seconds"]) / 3600.0:F2}");
                            table.Cell().Padding(2).Text($"{Convert.ToDouble(item["downtime_seconds"]) / 3600.0:F2}");
                            table.Cell().Padding(2).Text($"{Convert.ToDouble(item["coverage_pct"]):F2}%");
                            table.Cell().Padding(2).Text(av).Bold();
                        }
                    });

                    col.Item().PaddingVertical(6);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(28);
                            c.ConstantColumn(65);
                            c.RelativeColumn(1.8f);
                            c.ConstantColumn(32);
                            c.ConstantColumn(85);
                            c.ConstantColumn(50);
                            c.ConstantColumn(45);
                            c.ConstantColumn(55);
                            c.ConstantColumn(48);
                            c.ConstantColumn(48);
                            c.ConstantColumn(48);
                        });

                        table.Header(h =>
                        {
                            foreach (var hdr in new[] { "NO", "OID", "UNIT", "TIPE", "IP ADDRESS", "STATUS", "LOSS", "AVAIL.", "UP(h)", "DOWN(h)", "COV." })
                                h.Cell().Background(Colors.Grey.Lighten3).Padding(2).Text(hdr).Bold().FontSize(7);
                        });

                        foreach (var u in units)
                        {
                            var devs = (Dictionary<string, Dictionary<string, object?>>)u["devices"]!;
                            bool excl = u["excluded"] is true;
                            string name = $"{u["machine_name"]}{(excl ? " (BD)" : "")}";
                            foreach (var dtype in types)
                            {
                                var dev = devs[dtype];
                                string loss = dev["packet_loss_pct"] is double l ? $"{l:F1}%" : "N/A";
                                string av = dev["availability_pct"] is double a ? $"{a:F2}%" : "N/A";

                                table.Cell().Padding(1.5f).Text($"{u["rank"]}");
                                table.Cell().Padding(1.5f).Text($"{u["machine_oid"]}");
                                table.Cell().Padding(1.5f).Text(name);
                                table.Cell().Padding(1.5f).Text(dtype);
                                table.Cell().Padding(1.5f).Text(dev["ip_address"]?.ToString() ?? "-");
                                table.Cell().Padding(1.5f).Text(dev["status"]?.ToString() ?? "N/A");
                                table.Cell().Padding(1.5f).Text(loss);
                                table.Cell().Padding(1.5f).Text(av);
                                table.Cell().Padding(1.5f).Text($"{Convert.ToDouble(dev["uptime_seconds"]) / 3600.0:F2}");
                                table.Cell().Padding(1.5f).Text($"{Convert.ToDouble(dev["downtime_seconds"]) / 3600.0:F2}");
                                table.Cell().Padding(1.5f).Text($"{Convert.ToDouble(dev["coverage_pct"]):F1}%");
                            }
                        }
                    });
                });
            });
        });

        return doc.GeneratePdf();
    }
}
