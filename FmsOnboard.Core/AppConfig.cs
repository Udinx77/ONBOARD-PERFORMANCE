using System.Globalization;
using System.Net;

namespace FmsOnboard.Core;

public sealed class IpCidr
{
    private readonly byte[] _networkBytes;
    private readonly int _prefixLength;

    public IpCidr(IPAddress address, int prefixLength)
    {
        _networkBytes = address.GetAddressBytes();
        _prefixLength = prefixLength;
    }

    public static bool TryParse(string raw, out IpCidr? cidr)
    {
        cidr = null;
        var s = raw.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        var parts = s.Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        int maxBits = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        int prefix = maxBits;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxBits))
            return false;

        cidr = new IpCidr(ip, prefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var target = address.GetAddressBytes();
        if (target.Length != _networkBytes.Length) return false;

        int fullBytes = _prefixLength / 8;
        int remBits = _prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
            if (target[i] != _networkBytes[i]) return false;

        if (remBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remBits));
            if ((target[fullBytes] & mask) != (_networkBytes[fullBytes] & mask)) return false;
        }
        return true;
    }
}

public sealed class AppConfig
{
    public string BaseDir { get; init; } = Directory.GetCurrentDirectory();
    public string DbFile { get; init; } = "";
    public string MachineQueryFile { get; init; } = "";
    public string GpsQueryFile { get; init; } = "";
    public string SqlConnectionString { get; init; } = "";
    public TimeZoneInfo Timezone { get; init; } = TimeZoneInfo.Utc;
    public string TimezoneName { get; init; } = "Asia/Makassar";
    public TimeOnly BreakStart { get; init; } = new(12, 0);
    public TimeOnly BreakEnd { get; init; } = new(13, 0);

    public int ScanIntervalSeconds { get; init; } = 60;
    public int PingCount { get; init; } = 5;
    public double PingIntervalSeconds { get; init; } = 0.2;
    public double PingTimeoutSeconds { get; init; } = 1.0;
    public int ConcurrentTasks { get; init; } = 75;
    public double FailureLossThresholdPercent { get; init; } = 40.0;
    public int RedAfterMinutes { get; init; } = 15;
    public int MaxObservationGapSeconds { get; init; } = 180;
    public int PingHistoryRetentionDays { get; init; } = 30;
    public List<IPAddress> CanaryIps { get; init; } = [];

    public string GpsMetricColumn { get; init; } = "Satellites";
    public string GpsMetricAggregate { get; init; } = "last";
    public double GpsRedBelow { get; init; } = 5.0;
    public double GpsYellowMax { get; init; } = 8.0;
    public bool GpsAbsentIsRed { get; init; } = true;
    public int GpsQueryRefreshMinutes { get; init; } = 3;
    public int GpsQueryWindowMinutes { get; init; } = 3;
    public int MachineRefreshMinutes { get; init; } = 10;

    public bool AvailabilityPurge { get; init; } = true;
    public int AvailabilityKeepMonths { get; init; } = 2;

    public int SqlRefreshSeconds { get; init; } = 30;
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 5001;
    public List<IpCidr> AllowedNetworks { get; init; } = [];
    public string DashboardUser { get; init; } = "admin";
    public string DashboardPassword { get; init; } = "";
    public string CorsAllowedOrigins { get; init; } = "";

    public static void LoadDotEnv(string baseDir)
    {
        var envPath = Path.Combine(baseDir, ".env");
        if (!File.Exists(envPath)) return;

        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if ((val.StartsWith('"') && val.EndsWith('"')) || (val.StartsWith('\'') && val.EndsWith('\'')))
                val = val[1..^1];

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static string EnvStr(string key, string def)
    {
        var v = Environment.GetEnvironmentVariable(key)?.Trim();
        return string.IsNullOrEmpty(v) ? def : v;
    }

    private static bool EnvBool(string key, bool def)
    {
        var v = Environment.GetEnvironmentVariable(key)?.Trim().ToLowerInvariant();
        return v is null ? def : v is "1" or "true" or "yes" or "on";
    }

    private static int EnvInt(string key, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(key)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : def;

    private static double EnvDouble(string key, double def) =>
        double.TryParse(Environment.GetEnvironmentVariable(key)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : def;

    private static string BuildOdbcConnectionString()
    {
        var explicitStr = EnvStr("SQL_CONNECTION_STRING", "");
        if (!string.IsNullOrEmpty(explicitStr)) return explicitStr;

        var driver = EnvStr("SQL_DRIVER", "ODBC Driver 17 for SQL Server");
        var server = EnvStr("SQL_SERVER", "10.4.17.13");
        var database = EnvStr("SQL_DATABASE", "");
        var username = EnvStr("SQL_USERNAME", "");
        var password = EnvStr("SQL_PASSWORD", "");
        var trusted = EnvBool("SQL_TRUSTED_CONNECTION", false);
        var timeout = EnvInt("SQL_CONNECTION_TIMEOUT", 10);
        var encrypt = EnvStr("SQL_ENCRYPT", "no");
        var trustCert = EnvStr("SQL_TRUST_SERVER_CERTIFICATE", "yes");

        var parts = new List<string>
        {
            $"Driver={{{driver}}}",
            $"Server={server}",
            $"Connection Timeout={timeout}",
            $"Encrypt={encrypt}",
            $"TrustServerCertificate={trustCert}"
        };
        if (!string.IsNullOrEmpty(database)) parts.Add($"Database={database}");
        if (trusted) parts.Add("Trusted_Connection=yes");
        else if (!string.IsNullOrEmpty(username))
        {
            parts.Add($"UID={username}");
            parts.Add($"PWD={password}");
        }
        return string.Join(";", parts) + ";";
    }

    private static TimeZoneInfo ResolveTimezone(string tzName)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(tzName); }
        catch
        {
            if (tzName == "Asia/Makassar")
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time"); } catch { }
                return TimeZoneInfo.CreateCustomTimeZone("WITA", TimeSpan.FromHours(8), "WITA", "WITA");
            }
            throw;
        }
    }

    public static AppConfig FromEnv()
    {
        var baseDir = Directory.GetCurrentDirectory();
        LoadDotEnv(baseDir);

        var dbRaw = EnvStr("DB_FILE", "monitoring.db");
        var dbFile = Path.IsPathRooted(dbRaw) ? dbRaw : Path.Combine(baseDir, dbRaw);
        var tzName = EnvStr("TIMEZONE_NAME", "Asia/Makassar");

        var breakStart = TimeOnly.TryParseExact(EnvStr("BREAK_START", "12:00"), "HH:mm", out var bs) ? bs : new TimeOnly(12, 0);
        var breakEnd = TimeOnly.TryParseExact(EnvStr("BREAK_END", "13:00"), "HH:mm", out var be) ? be : new TimeOnly(13, 0);

        var canaryIps = EnvStr("CANARY_IPS", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Cast<IPAddress>()
            .ToList();

        var allowedNets = new List<IpCidr>();
        foreach (var raw in EnvStr("ALLOWED_NETWORKS", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IpCidr.TryParse(raw, out var cidr) && cidr is not null)
                allowedNets.Add(cidr);
        }

        return new AppConfig
        {
            BaseDir = baseDir,
            DbFile = dbFile,
            MachineQueryFile = Path.Combine(baseDir, EnvStr("MACHINE_QUERY_FILE", "query_machine_ips.sql")),
            GpsQueryFile = Path.Combine(baseDir, EnvStr("GPS_QUERY_FILE", "query_gps.sql")),
            SqlConnectionString = BuildOdbcConnectionString(),
            Timezone = ResolveTimezone(tzName),
            TimezoneName = tzName,
            BreakStart = breakStart,
            BreakEnd = breakEnd,
            ScanIntervalSeconds = EnvInt("SCAN_INTERVAL_SECONDS", 60),
            PingCount = EnvInt("PING_COUNT", 5),
            PingIntervalSeconds = EnvDouble("PING_INTERVAL_SECONDS", 0.2),
            PingTimeoutSeconds = EnvDouble("PING_TIMEOUT_SECONDS", 1.0),
            ConcurrentTasks = EnvInt("CONCURRENT_TASKS", 75),
            FailureLossThresholdPercent = EnvDouble("FAILURE_LOSS_THRESHOLD_PERCENT", 40.0),
            RedAfterMinutes = EnvInt("RED_AFTER_MINUTES", 15),
            MaxObservationGapSeconds = EnvInt("MAX_OBSERVATION_GAP_SECONDS", 180),
            PingHistoryRetentionDays = EnvInt("PING_HISTORY_RETENTION_DAYS", 30),
            CanaryIps = canaryIps,
            GpsMetricColumn = EnvStr("GPS_METRIC_COLUMN", "Satellites"),
            GpsMetricAggregate = EnvStr("GPS_METRIC_AGGREGATE", "last").ToLowerInvariant(),
            GpsRedBelow = EnvDouble("GPS_RED_BELOW", 5.0),
            GpsYellowMax = EnvDouble("GPS_YELLOW_MAX", 8.0),
            GpsAbsentIsRed = EnvBool("GPS_ABSENT_IS_RED", true),
            GpsQueryRefreshMinutes = EnvInt("GPS_QUERY_REFRESH_MINUTES", 3),
            GpsQueryWindowMinutes = EnvInt("GPS_QUERY_WINDOW_MINUTES", 3),
            MachineRefreshMinutes = EnvInt("MACHINE_REFRESH_MINUTES", 10),
            AvailabilityPurge = EnvBool("AVAILABILITY_PURGE", true),
            AvailabilityKeepMonths = EnvInt("AVAILABILITY_KEEP_MONTHS", 2),
            SqlRefreshSeconds = EnvInt("SQL_REFRESH_SECONDS", 30),
            Host = EnvStr("FLASK_HOST", "0.0.0.0"),
            Port = EnvInt("FLASK_PORT", 5001),
            AllowedNetworks = allowedNets,
            DashboardUser = EnvStr("DASHBOARD_USER", "admin"),
            DashboardPassword = EnvStr("DASHBOARD_PASSWORD", ""),
            CorsAllowedOrigins = EnvStr("CORS_ALLOWED_ORIGINS", "")
        };
    }
}
