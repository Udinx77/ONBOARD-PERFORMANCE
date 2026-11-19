using System.Globalization;
using ClosedXML.Excel;

namespace FmsOnboard.Core;

public static class MonthlyReportService
{
    private static double? Pct(double up, double obs) =>
        obs > 0.0 ? Math.Round(up / obs * 100.0 * 10000.0) / 10000.0 : null;

    private static double Cov(double obs, double exp) =>
        exp > 0.0 ? Math.Round(Math.Min(obs / exp * 100.0, 100.0) * 10000.0) / 10000.0 : 0.0;

    public static Dictionary<string, object?> BuildMonthlyContext(AppConfig cfg)
    {
        var months = DatabaseService.ListMonthStatus(cfg.DbFile);
        var defaultMonth = months.FirstOrDefault()?["month"]?.ToString()
            ?? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        return new Dictionary<string, object?>
        {
            ["months"] = months,
            ["default_month"] = defaultMonth,
            ["keep_months"] = cfg.AvailabilityKeepMonths,
            ["purge"] = cfg.AvailabilityPurge
        };
    }

    private sealed class DevAcc
    {
        public string Ip = "";
        public double Up;
        public double Down;
        public double Obs;
        public readonly HashSet<string> Days = [];
        public double ExpAll;
        public double ExpNet;
    }

    public static Dictionary<string, object?> BuildMonthlyReport(AppConfig cfg, string month)
    {
        if (month.Length != 7 || month[4] != '-')
            throw new FormatException("Format bulan harus YYYY-MM, misalnya 2026-10.");

        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, cfg.Timezone);
        var todayDate = DateOnly.FromDateTime(nowLocal);

        var (firstIso, nextIso) = DatabaseService.MonthRange(month);
        var first = DateOnly.ParseExact(firstIso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = DateOnly.ParseExact(nextIso, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1);
        var lastIso = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (first > todayDate)
            throw new InvalidOperationException("Bulan itu belum dimulai.");

        var lastCounted = last < todayDate ? last : todayDate;
        int numDays = lastCounted.DayNumber - first.DayNumber + 1;
        var days = Enumerable.Range(0, numDays).Select(i => first.AddDays(i)).ToList();

        var expectedDay = new Dictionary<string, double>();
        foreach (var d in days)
        {
            DateTimeOffset? until = d == todayDate ? nowUtc : null;
            double sec = DatabaseService.CountedSecondsInDay(d, until, cfg.Timezone, cfg.BreakStart, cfg.BreakEnd);
            expectedDay[d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = sec;
        }

        var rows = new List<(string LocalDate, long Oid, string? Name, string Dtype, string Ip, double Up, double Down, double Obs)>();
        using (var conn = DatabaseService.ConnectDb(cfg.DbFile))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT local_date, machine_oid, machine_name, device_type, ip_address,
                       uptime_seconds, downtime_seconds, observed_seconds
                FROM availability_daily
                WHERE local_date >= $f AND local_date <= $l
                ORDER BY local_date
                """;
            cmd.Parameters.AddWithValue("$f", firstIso);
            cmd.Parameters.AddWithValue("$l", lastIso);
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                rows.Add((
                    rdr.GetString(0),
                    rdr.GetInt64(1),
                    rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    rdr.GetString(3),
                    rdr.GetString(4),
                    rdr.IsDBNull(5) ? 0.0 : rdr.GetDouble(5),
                    rdr.IsDBNull(6) ? 0.0 : rdr.GetDouble(6),
                    rdr.IsDBNull(7) ? 0.0 : rdr.GetDouble(7)
                ));
            }
        }

        if (rows.Count == 0)
            throw new InvalidOperationException($"Tidak ada data availability pada bulan {month}.");

        var bdDays = DatabaseService.GetBdDays(cfg.DbFile, firstIso, lastIso);
        var types = Constants.DeviceTypes.Where(t => rows.Any(r => r.Dtype == t)).ToList();
        var overallTypes = Constants.OverallDeviceTypes.Where(t => types.Contains(t)).ToList();

        var names = new Dictionary<long, string>();
        var dev = new Dictionary<(long, string), DevAcc>();
        var dayType = new Dictionary<(string, string), (double Up, double Obs)>();

        foreach (var (localDate, oid, nameOpt, dtype, ip, up, down, obs) in rows)
        {
            if (!string.IsNullOrEmpty(nameOpt)) names[oid] = nameOpt;
            if (!dev.TryGetValue((oid, dtype), out var entry))
            {
                entry = new DevAcc { Ip = ip };
                dev[(oid, dtype)] = entry;
            }
            entry.Ip = ip;
            entry.Up += up;
            entry.Down += down;
            entry.Obs += obs;
            entry.Days.Add(localDate);

            if (!bdDays.Contains((localDate, oid)))
            {
                dayType.TryGetValue((localDate, dtype), out var cur);
                dayType[(localDate, dtype)] = (cur.Up + up, cur.Obs + obs);
            }
        }

        try
        {
            var mmap = MachineMapService.LoadMachineMapForReport(cfg);
            foreach (var (oid, info) in mmap)
            {
                if (names.ContainsKey(oid) || dev.Keys.Any(k => k.Item1 == oid))
                    names[oid] = info.Name;
            }
        }
        catch { }

        foreach (var ((oid, _), d) in dev)
        {
            foreach (var day in days)
            {
                var iso = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                double sec = expectedDay.GetValueOrDefault(iso, 0.0);
                d.ExpAll += sec;
                if (!bdDays.Contains((iso, oid))) d.ExpNet += sec;
            }
        }

        var overallObj = new Dictionary<string, Dictionary<string, object?>>();
        var allKeys = new List<string>(types) { "combined" };

        foreach (var key in allKeys)
        {
            var selected = key == "combined" ? overallTypes : [key];
            double up = 0, down = 0, obs = 0, exp = 0;
            long count = 0;

            foreach (var (localDate, oid, _, dtype, _, rUp, rDown, rObs) in rows)
            {
                if (selected.Contains(dtype) && !bdDays.Contains((localDate, oid)))
                {
                    up += rUp;
                    down += rDown;
                    obs += rObs;
                }
            }
            foreach (var ((_, dtype), d) in dev)
            {
                if (selected.Contains(dtype))
                {
                    count++;
                    exp += d.ExpNet;
                }
            }

            overallObj[key] = new Dictionary<string, object?>
            {
                ["devices"] = count,
                ["uptime_seconds"] = up,
                ["downtime_seconds"] = down,
                ["observed_seconds"] = obs,
                ["availability_pct"] = Pct(up, obs),
                ["coverage_pct"] = Cov(obs, exp)
            };
        }

        var oids = dev.Keys.Select(k => k.Item1).Distinct()
            .OrderBy(o => names.GetValueOrDefault(o, ""), StringComparer.Ordinal)
            .ThenBy(o => o)
            .ToList();

        var units = new List<Dictionary<string, object?>>(oids.Count);
        foreach (var oid in oids)
        {
            var devicesObj = new Dictionary<string, Dictionary<string, object?>>();
            double uUp = 0, uObs = 0, uExp = 0;
            var obsDays = new HashSet<string>();

            foreach (var t in types)
            {
                if (dev.TryGetValue((oid, t), out var d))
                {
                    devicesObj[t] = new Dictionary<string, object?>
                    {
                        ["ip"] = d.Ip,
                        ["availability_pct"] = Pct(d.Up, d.Obs),
                        ["downtime_seconds"] = d.Down,
                        ["observed_seconds"] = d.Obs,
                        ["coverage_pct"] = Cov(d.Obs, d.ExpAll)
                    };
                    obsDays.UnionWith(d.Days);
                    if (overallTypes.Contains(t))
                    {
                        uUp += d.Up;
                        uObs += d.Obs;
                        uExp += d.ExpAll;
                    }
                }
                else
                {
                    devicesObj[t] = new Dictionary<string, object?>
                    {
                        ["ip"] = null,
                        ["availability_pct"] = null,
                        ["downtime_seconds"] = 0.0,
                        ["observed_seconds"] = 0.0,
                        ["coverage_pct"] = 0.0
                    };
                }
            }

            units.Add(new Dictionary<string, object?>
            {
                ["oid"] = oid,
                ["name"] = names.GetValueOrDefault(oid, $"OID-{oid}"),
                ["devices"] = devicesObj,
                ["unit_availability_pct"] = Pct(uUp, uObs),
                ["unit_coverage_pct"] = Cov(uObs, uExp),
                ["bd_days"] = bdDays.Count(x => x.Oid == oid),
                ["observed_days"] = obsDays.Count
            });
        }

        units.Sort((a, b) =>
        {
            var aa = a["unit_availability_pct"] as double?;
            var ba = b["unit_availability_pct"] as double?;
            int cmp = (aa, ba) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                (double x, double y) => x.CompareTo(y)
            };
            return cmp != 0 ? cmp : string.CompareOrdinal(a["name"]?.ToString() ?? "", b["name"]?.ToString() ?? "");
        });
        for (int i = 0; i < units.Count; i++) units[i]["rank"] = i + 1;

        var daily = new List<Dictionary<string, object?>>(days.Count);
        foreach (var d in days)
        {
            var iso = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var item = new Dictionary<string, object?>
            {
                ["date"] = iso,
                ["bd_units"] = bdDays.Count(x => x.LocalDate == iso)
            };
            double cUp = 0, cObs = 0;
            foreach (var t in types)
            {
                var (up, obs) = dayType.GetValueOrDefault((iso, t), (0.0, 0.0));
                item[t] = Pct(up, obs);
                if (overallTypes.Contains(t))
                {
                    cUp += up;
                    cObs += obs;
                }
            }
            item["combined"] = Pct(cUp, cObs);
            daily.Add(item);
        }

        bool complete = todayDate > last;
        int bdUnitDays = bdDays.Count(x => names.ContainsKey(x.Oid));

        return new Dictionary<string, object?>
        {
            ["month"] = month,
            ["first_date"] = firstIso,
            ["last_date"] = lastIso,
            ["complete"] = complete,
            ["generated_at"] = nowLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["timezone"] = cfg.TimezoneName,
            ["types"] = types,
            ["overall_types"] = overallTypes,
            ["overall"] = overallObj,
            ["units"] = units,
            ["daily"] = daily,
            ["bd_unit_days"] = bdUnitDays,
            ["row_count"] = rows.Count
        };
    }

    private static void ApplyMonthlyCf(IXLRange range)
    {
        range.AddConditionalFormat().WhenGreaterThan(90.0).Fill.SetBackgroundColor(XLColor.FromHtml("#C6EFCE"));
        range.AddConditionalFormat().WhenBetween(80.0, 90.0).Fill.SetBackgroundColor(XLColor.FromHtml("#FFEB9C"));
        range.AddConditionalFormat().WhenLessThan(80.0).Fill.SetBackgroundColor(XLColor.FromHtml("#FFC7CE"));
    }

    public static byte[] BuildMonthlyXlsxBytes(AppConfig cfg, string month)
    {
        var report = BuildMonthlyReport(cfg, month);
        using var wb = new XLWorkbook();

        var types = (List<string>)report["types"]!;
        var overallTypes = (List<string>)report["overall_types"]!;
        var overall = (Dictionary<string, Dictionary<string, object?>>)report["overall"]!;
        var units = (List<Dictionary<string, object?>>)report["units"]!;
        var daily = (List<Dictionary<string, object?>>)report["daily"]!;

        var ws1 = wb.Worksheets.Add("Ringkasan");
        ws1.Cell(1, 1).Value = $"AVAILABILITY ONB - {report["month"]}";
        ws1.Cell(2, 1).Value = $"Periode: {report["first_date"]} s.d. {report["last_date"]} ({report["timezone"]})";
        ws1.Cell(3, 1).Value = $"Dibuat: {report["generated_at"]}";

        string[] sumHeaders = ["Perangkat", "Jumlah perangkat", "Uptime (jam)", "Downtime (jam)", "Observed (jam)", "Coverage (%)", "Availability (%)"];
        for (int c = 0; c < sumHeaders.Length; c++)
        {
            ws1.Cell(5, c + 1).Value = sumHeaders[c];
            AvailabilityReportService.StyleHeader(ws1.Cell(5, c + 1), "#1F3A4D");
        }

        var rowsMeta = types.Select(t =>
        {
            string suffix = overallTypes.Contains(t) ? "" : " (tidak masuk overall)";
            return (Label: $"{t} - {AvailabilityReportService.DeviceLabel(t)}{suffix}", Key: t);
        }).ToList();
        rowsMeta.Add(($"{string.Join(" + ", overallTypes)} (Overall)", "combined"));

        for (int i = 0; i < rowsMeta.Count; i++)
        {
            int r = 6 + i;
            var o = overall[rowsMeta[i].Key];
            ws1.Cell(r, 1).Value = rowsMeta[i].Label;
            ws1.Cell(r, 2).Value = Convert.ToDouble(o["devices"] ?? 0);
            AvailabilityReportService.SetNumCell(ws1.Cell(r, 3), Convert.ToDouble(o["uptime_seconds"] ?? 0) / 3600.0, "0.00");
            AvailabilityReportService.SetNumCell(ws1.Cell(r, 4), Convert.ToDouble(o["downtime_seconds"] ?? 0) / 3600.0, "0.00");
            AvailabilityReportService.SetNumCell(ws1.Cell(r, 5), Convert.ToDouble(o["observed_seconds"] ?? 0) / 3600.0, "0.00");
            AvailabilityReportService.SetNumCell(ws1.Cell(r, 6), Convert.ToDouble(o["coverage_pct"] ?? 0), "0.00%");
            if (o["availability_pct"] is double av) AvailabilityReportService.SetNumCell(ws1.Cell(r, 7), av, "0.00%");
            else ws1.Cell(r, 7).Value = "N/A";
        }
        if (rowsMeta.Count > 0) ApplyMonthlyCf(ws1.Range(6, 7, 5 + rowsMeta.Count, 7));
        ws1.Column(1).Width = 42;
        for (int c = 2; c <= 7; c++) ws1.Column(c).Width = 18;

        var ws2 = wb.Worksheets.Add("Per Unit");
        var uHeaders = new List<string> { "No", "OID", "Nama Unit" };
        foreach (var t in types)
        {
            uHeaders.Add($"Availability {AvailabilityReportService.DeviceLabel(t)} (%)");
            uHeaders.Add($"Downtime {AvailabilityReportService.DeviceLabel(t)} (jam)");
        }
        uHeaders.AddRange([
            $"Availability Unit {string.Join("+", overallTypes)} (%)",
            "Coverage Unit (%)",
            "Hari terpantau",
            "Hari BD"
        ]);
        for (int c = 0; c < uHeaders.Count; c++)
        {
            ws2.Cell(1, c + 1).Value = uHeaders[c];
            AvailabilityReportService.StyleHeader(ws2.Cell(1, c + 1), "#1F3A4D");
        }

        for (int i = 0; i < units.Count; i++)
        {
            int r = 2 + i;
            var u = units[i];
            ws2.Cell(r, 1).Value = Convert.ToDouble(u["rank"] ?? i + 1);
            ws2.Cell(r, 2).Value = Convert.ToDouble(u["oid"] ?? 0);
            ws2.Cell(r, 3).Value = u["name"]?.ToString() ?? "";

            var devs = (Dictionary<string, Dictionary<string, object?>>)u["devices"]!;
            int col = 4;
            foreach (var t in types)
            {
                var d = devs[t];
                if (d["availability_pct"] is double av) AvailabilityReportService.SetNumCell(ws2.Cell(r, col), av, "0.00%");
                else ws2.Cell(r, col).Value = "N/A";
                AvailabilityReportService.SetNumCell(ws2.Cell(r, col + 1), Convert.ToDouble(d["downtime_seconds"] ?? 0) / 3600.0, "0.00");
                col += 2;
            }
            if (u["unit_availability_pct"] is double uav) AvailabilityReportService.SetNumCell(ws2.Cell(r, col), uav, "0.00%");
            else ws2.Cell(r, col).Value = "N/A";
            AvailabilityReportService.SetNumCell(ws2.Cell(r, col + 1), Convert.ToDouble(u["unit_coverage_pct"] ?? 0), "0.00%");
            ws2.Cell(r, col + 2).Value = Convert.ToDouble(u["observed_days"] ?? 0);
            ws2.Cell(r, col + 3).Value = Convert.ToDouble(u["bd_days"] ?? 0);
        }
        if (units.Count > 0)
        {
            int lastU = units.Count + 1;
            for (int i = 0; i < types.Count; i++)
                ApplyMonthlyCf(ws2.Range(2, 4 + i * 2, lastU, 4 + i * 2));
            int uavC = 4 + types.Count * 2;
            ApplyMonthlyCf(ws2.Range(2, uavC, lastU, uavC));
        }
        ws2.SheetView.Freeze(1, 3);

        var ws3 = wb.Worksheets.Add("Harian");
        var dHeaders = new List<string> { "Tanggal" };
        foreach (var t in types) dHeaders.Add($"Availability {AvailabilityReportService.DeviceLabel(t)} (%)");
        dHeaders.Add($"Overall {string.Join("+", overallTypes)} (%)");
        dHeaders.Add("Unit BD dikecualikan");

        for (int c = 0; c < dHeaders.Count; c++)
        {
            ws3.Cell(1, c + 1).Value = dHeaders[c];
            AvailabilityReportService.StyleHeader(ws3.Cell(1, c + 1), "#1F3A4D");
        }

        for (int i = 0; i < daily.Count; i++)
        {
            int r = 2 + i;
            var d = daily[i];
            ws3.Cell(r, 1).Value = d["date"]?.ToString() ?? "";
            int col = 2;
            foreach (var t in types)
            {
                if (d[t] is double v) AvailabilityReportService.SetNumCell(ws3.Cell(r, col), v, "0.00%");
                else ws3.Cell(r, col).Value = "N/A";
                col++;
            }
            if (d["combined"] is double cv) AvailabilityReportService.SetNumCell(ws3.Cell(r, col), cv, "0.00%");
            else ws3.Cell(r, col).Value = "N/A";
            ws3.Cell(r, col + 1).Value = Convert.ToDouble(d["bd_units"] ?? 0);
        }
        if (daily.Count > 0)
            ApplyMonthlyCf(ws3.Range(2, 2, daily.Count + 1, 2 + types.Count));
        ws3.SheetView.Freeze(1, 1);

        if (report["complete"] is true)
        {
            try { DatabaseService.MarkMonthExported(cfg.DbFile, month, Convert.ToInt32(report["row_count"] ?? 0)); }
            catch { }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
