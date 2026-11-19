using System.Data.Odbc;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FmsOnboard.Core;

public static class MachineMapService
{
    private static readonly (string Key, string[] Aliases)[] SqlColumnAliases =
    [
        ("OID", ["oid", "parentoid"]),
        ("Name", ["sn unit", "name", "snunit", "primarymachinename"]),
        ("R", ["ip wgb radio", "ip radio", "r"]),
        ("D", ["ip display", "d"]),
        ("P", ["ip ple", "p"]),
        ("G", ["ip gps", "gps", "g"])
    ];

    private sealed class RawCacheRow
    {
        [JsonPropertyName("OID")] public string? Oid { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("R")] public string? R { get; set; }
        [JsonPropertyName("D")] public string? D { get; set; }
        [JsonPropertyName("P")] public string? P { get; set; }
        [JsonPropertyName("G")] public string? G { get; set; }
    }

    private sealed class CachePayload
    {
        [JsonPropertyName("saved_at")] public double SavedAt { get; set; }
        [JsonPropertyName("rows")] public List<RawCacheRow> Rows { get; set; } = [];
    }

    private static string CachePath(AppConfig cfg) => Path.Combine(cfg.BaseDir, "machine_map_cache.json");

    private static string? CleanCell(string? val)
    {
        var s = val?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.ToLowerInvariant() is "nan" or "none" or "null" ? null : s;
    }

    private static bool TryParseOid(string? val, out long oid)
    {
        oid = 0;
        var s = CleanCell(val);
        if (s is null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
            return false;
        oid = (long)f;
        return true;
    }

    private static (Dictionary<long, MachineInfo>, List<MachineTarget>) BuildFromRows(List<RawCacheRow> rows, bool satMode)
    {
        var oidMap = new Dictionary<long, MachineInfo>();
        var targets = new List<MachineTarget>();
        var seenKeys = new HashSet<(long, string, string)>();

        foreach (var row in rows)
        {
            if (!TryParseOid(row.Oid, out long oid)) continue;
            var name = CleanCell(row.Name) ?? $"OID-{oid}";
            var info = new MachineInfo { Name = name };

            (string Dtype, string? RawIp)[] pairs =
            [
                ("R", row.R),
                ("D", row.D),
                ("P", row.P),
                ("G", row.G)
            ];

            foreach (var (dtype, rawIpOpt) in pairs)
            {
                if (dtype == "G" && satMode) continue;
                var rawIp = CleanCell(rawIpOpt);
                if (rawIp is null || !IPAddress.TryParse(rawIp, out var parsedIp)) continue;

                var ipStr = parsedIp.ToString();
                switch (dtype)
                {
                    case "R": info.R = ipStr; break;
                    case "D": info.D = ipStr; break;
                    case "P": info.P = ipStr; break;
                    case "G": info.G = ipStr; break;
                }

                if (seenKeys.Add((oid, dtype, ipStr)))
                    targets.Add(new MachineTarget(oid, name, dtype, ipStr));
            }

            oidMap[oid] = info;
        }

        if (oidMap.Count == 0)
            throw new InvalidOperationException("Tidak ada data OID valid pada mapping unit.");
        if (targets.Count == 0)
            throw new InvalidOperationException("Tidak ada IP perangkat R/D/P valid pada mapping unit.");

        targets.Sort();
        return (oidMap, targets);
    }

    private static List<RawCacheRow> FetchSqlRows(AppConfig cfg)
    {
        var sql = File.ReadAllText(cfg.MachineQueryFile).TrimStart('\uFEFF');
        using var conn = new OdbcConnection(cfg.SqlConnectionString);
        conn.Open();
        using var cmd = new OdbcCommand(sql, conn);
        using var reader = cmd.ExecuteReader();

        var lowered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reader.FieldCount; i++)
            lowered[reader.GetName(i)] = i;

        var colIdx = new Dictionary<string, int?>();
        foreach (var (key, aliases) in SqlColumnAliases)
        {
            int? found = null;
            foreach (var a in aliases)
            {
                if (lowered.TryGetValue(a, out int idx)) { found = idx; break; }
            }
            colIdx[key] = found;
        }

        foreach (var req in new[] { "OID", "Name", "R", "D", "P" })
        {
            if (!colIdx.TryGetValue(req, out var idx) || !idx.HasValue)
                throw new InvalidOperationException($"Kolom wajib '{req}' tidak ditemukan pada hasil query_machine_ips.sql");
        }

        var rows = new List<RawCacheRow>();
        while (reader.Read())
        {
            string? GetCol(string k)
            {
                var idx = colIdx[k];
                if (!idx.HasValue || reader.IsDBNull(idx.Value)) return null;
                return reader.GetValue(idx.Value)?.ToString();
            }

            rows.Add(new RawCacheRow
            {
                Oid = GetCol("OID"),
                Name = GetCol("Name"),
                R = GetCol("R"),
                D = GetCol("D"),
                P = GetCol("P"),
                G = GetCol("G")
            });
        }
        return rows;
    }

    private static void SaveCache(AppConfig cfg, List<RawCacheRow> rows)
    {
        try
        {
            var payload = new CachePayload
            {
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                Rows = rows
            };
            var json = JsonSerializer.Serialize(payload);
            var target = CachePath(cfg);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, target, overwrite: true);
        }
        catch { }
    }

    private static CachePayload? LoadCache(AppConfig cfg)
    {
        var path = CachePath(cfg);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CachePayload>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public static List<(long Oid, string Name)> LoadCachedOidNames(AppConfig cfg)
    {
        var payload = LoadCache(cfg) ?? throw new InvalidOperationException("Cache mapping belum tersedia");
        var outList = new List<(long, string)>();
        foreach (var r in payload.Rows)
        {
            if (TryParseOid(r.Oid, out long oid))
                outList.Add((oid, CleanCell(r.Name) ?? $"OID-{oid}"));
        }
        return outList;
    }

    public static Dictionary<long, MachineInfo> LoadMachineMapForReport(AppConfig cfg)
    {
        bool satMode = GpsMetricService.SatelliteMode(cfg);
        try
        {
            var rows = FetchSqlRows(cfg);
            SaveCache(cfg, rows);
            return BuildFromRows(rows, satMode).Item1;
        }
        catch
        {
            var cached = LoadCache(cfg) ?? throw new InvalidOperationException("SQL Server gagal diakses dan machine_map_cache.json kosong");
            return BuildFromRows(cached.Rows, satMode).Item1;
        }
    }

    public sealed class MapWatcher
    {
        private readonly AppConfig _cfg;
        private readonly bool _satMode;
        private readonly TimeSpan _interval;
        private readonly ILogger _logger;
        private DateTime _lastTry;
        private Dictionary<long, MachineInfo> _oidMap;
        private List<MachineTarget> _targets;

        public MapWatcher(AppConfig cfg, ILogger logger)
        {
            _cfg = cfg;
            _logger = logger;
            _satMode = GpsMetricService.SatelliteMode(cfg);
            _interval = TimeSpan.FromMinutes(Math.Max(1, cfg.MachineRefreshMinutes));

            try
            {
                var rows = FetchSqlRows(cfg);
                (_oidMap, _targets) = BuildFromRows(rows, _satMode);
                SaveCache(cfg, rows);
                _logger.LogInformation("[SQL] Berhasil memuat {Units} unit dan {Targets} target perangkat.", _oidMap.Count, _targets.Count);
            }
            catch (Exception ex)
            {
                var cached = LoadCache(cfg) ?? throw new InvalidOperationException($"Gagal memuat mapping dari SQL Server ({ex.Message}) dan belum ada cache.", ex);
                (_oidMap, _targets) = BuildFromRows(cached.Rows, _satMode);
                _logger.LogWarning("[SQL] SQL Server tidak terjangkau ({Msg}); memakai cache ({Units} unit).", ex.Message, _oidMap.Count);
            }
            _lastTry = DateTime.UtcNow;
        }

        public List<MachineTarget> Targets => [.. _targets];

        public bool Poll()
        {
            if (DateTime.UtcNow - _lastTry < _interval) return false;
            _lastTry = DateTime.UtcNow;

            List<RawCacheRow> rows;
            Dictionary<long, MachineInfo> newMap;
            List<MachineTarget> newTargets;
            try
            {
                rows = FetchSqlRows(_cfg);
                (newMap, newTargets) = BuildFromRows(rows, _satMode);
            }
            catch
            {
                _logger.LogWarning("[SQL] Muat ulang mapping gagal, memakai mapping lama.");
                return false;
            }

            if (_oidMap.Count >= 10 && newMap.Count < _oidMap.Count * 0.5)
            {
                _logger.LogWarning("[SQL] Mapping turun drastis dari {Old} ke {New} unit; hasil baru diabaikan.", _oidMap.Count, newMap.Count);
                return false;
            }

            if (newTargets.SequenceEqual(_targets)) return false;

            _logger.LogInformation("[SQL] Mapping diperbarui: {Units} unit, {Targets} target perangkat.", newMap.Count, newTargets.Count);
            SaveCache(_cfg, rows);
            _oidMap = newMap;
            _targets = newTargets;
            return true;
        }
    }
}
