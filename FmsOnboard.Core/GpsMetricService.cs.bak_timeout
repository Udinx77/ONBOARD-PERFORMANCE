using System.Data.Odbc;
using System.Globalization;

namespace FmsOnboard.Core;

public static class GpsMetricService
{
    public static bool SatelliteMode(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.GpsMetricColumn) || !File.Exists(cfg.GpsQueryFile))
            return false;

        return File.ReadLines(cfg.GpsQueryFile)
            .Select(l => l.Trim())
            .Any(t => !string.IsNullOrEmpty(t) && !t.StartsWith("--"));
    }

    public static string? EvaluateGpsState(AppConfig cfg, double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value))
            return cfg.GpsAbsentIsRed ? "crit" : null;

        if (value.Value <= cfg.GpsRedBelow) return "crit";
        if (value.Value <= cfg.GpsYellowMax) return "warn";
        return "ok";
    }

    public static Dictionary<long, double?> FetchGpsValues(AppConfig cfg)
    {
        var rawSql = File.ReadAllText(cfg.GpsQueryFile).TrimStart('\uFEFF');
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone);
        var startLocal = nowLocal.AddMinutes(-Math.Max(1, cfg.GpsQueryWindowMinutes));

        var sql = rawSql
            .Replace("__START_WITA__", startLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Replace("__END_WITA__", nowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

        using var conn = new OdbcConnection(cfg.SqlConnectionString);
        conn.Open();
        using var cmd = new OdbcCommand(sql, conn);
        cmd.CommandTimeout = 60;
        using var reader = cmd.ExecuteReader();

        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            cols[reader.GetName(i)] = i;

        if (!cols.TryGetValue("oid", out int oidIdx) && !cols.TryGetValue("machine_oid", out oidIdx))
            throw new InvalidOperationException("Kolom OID tidak ditemukan pada hasil query_gps.sql");

        var targetCol = cfg.GpsMetricColumn.Trim();
        if (!cols.TryGetValue(targetCol, out int valIdx))
            throw new InvalidOperationException($"Kolom '{cfg.GpsMetricColumn}' tidak ditemukan pada hasil query_gps.sql");

        var samplesByOid = new Dictionary<long, List<double?>>();

        while (reader.Read())
        {
            if (reader.IsDBNull(oidIdx)) continue;
            var oidStr = reader.GetValue(oidIdx)?.ToString()?.Trim();
            if (!double.TryParse(oidStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double oidD))
                continue;
            long oid = (long)oidD;

            double? val = null;
            if (!reader.IsDBNull(valIdx))
            {
                var valStr = reader.GetValue(valIdx)?.ToString()?.Trim();
                if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && !double.IsNaN(parsed))
                    val = parsed;
            }

            if (!samplesByOid.TryGetValue(oid, out var list))
            {
                list = [];
                samplesByOid[oid] = list;
            }
            list.Add(val);
        }

        var result = new Dictionary<long, double?>(samplesByOid.Count);
        foreach (var (oid, vals) in samplesByOid)
        {
            var nonNull = vals.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (nonNull.Count == 0)
            {
                result[oid] = null;
                continue;
            }

            result[oid] = cfg.GpsMetricAggregate switch
            {
                "min" => nonNull.Min(),
                "max" => nonNull.Max(),
                "avg" or "mean" => nonNull.Average(),
                _ => vals.LastOrDefault(v => v.HasValue) ?? nonNull.Last()
            };
        }
        return result;
    }
}
