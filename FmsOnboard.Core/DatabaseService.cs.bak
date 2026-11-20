using System.Data.Odbc;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FmsOnboard.Core;

public static class DatabaseService
{
    private static volatile HashSet<long> _excludedAvailabilityOids = [];
    private static long _lastExcludedRefreshSec;

    private static void EnsureExcludedOidsFresh(AppConfig cfg)
    {
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long prev = Interlocked.Read(ref _lastExcludedRefreshSec);
        if (nowSec - prev < 30 || Interlocked.CompareExchange(ref _lastExcludedRefreshSec, nowSec, prev) != prev)
            return;

        try
        {
            const string sql = """
                SELECT
                    m.MACHINE_OID,
                    CAST(m.IS_ACTIVE AS INT) AS IsActive,
                    J1.NAME AS DelayName,
                    J1.DELAYCATEGORY,
                    J1.DELAYCLASS_OID
                FROM [msmodel_rep].[dbo].[MACHINE] m
                LEFT JOIN [mshist_rep].[dbo].[DELAY] P
                    ON P.TARGET_MACHINE = m.MACHINE_OID AND P.STATUS = 3
                LEFT JOIN [msmodel_rep].[dbo].[DELAYCLASS] J1
                    ON P.DELAYCLASS = J1.DELAYCLASS_OID
                WHERE LEFT(UPPER(LTRIM(RTRIM(m.NAME))), 2) IN ('CB', 'HB', 'KB', 'CT', 'KT')
                """;
            using var odbcConn = new OdbcConnection(cfg.SqlConnectionString);
            odbcConn.Open();
            using var cmd = new OdbcCommand(sql, odbcConn);
            using var rdr = cmd.ExecuteReader();
            var excluded = new HashSet<long>();
            while (rdr.Read())
            {
                if (rdr.IsDBNull(0) || !long.TryParse(rdr.GetValue(0)?.ToString()?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out long oid))
                    continue;
                bool isActive = !rdr.IsDBNull(1) && rdr.GetValue(1)?.ToString()?.Trim() == "1";
                string dName = rdr.IsDBNull(2) ? "" : (rdr.GetValue(2)?.ToString()?.Trim() ?? "");
                long? dCat = !rdr.IsDBNull(3) && long.TryParse(rdr.GetValue(3)?.ToString()?.Trim(), out long dc) ? dc : null;
                long? dCls = !rdr.IsDBNull(4) && long.TryParse(rdr.GetValue(4)?.ToString()?.Trim(), out long dco) ? dco : null;
                bool isBd = dCat is 165096025 or 165096022 || dCls is 165097066 or 165097069;
                bool hasDelay = !string.IsNullOrWhiteSpace(dName) || dCat.HasValue || dCls.HasValue;
                if (!isActive || isBd || hasDelay) excluded.Add(oid);
            }
            _excludedAvailabilityOids = excluded;
        }
        catch { }
    }
    public static SqliteConnection ConnectDb(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Default Timeout=30;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 30000;
            PRAGMA synchronous = NORMAL;
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    public static void InitializeDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS scan_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at REAL NOT NULL,
                finished_at REAL,
                duration_seconds REAL,
                target_count INTEGER NOT NULL DEFAULT 0,
                checked_count INTEGER NOT NULL DEFAULT 0,
                scan_status TEXT NOT NULL,
                error_message TEXT
            );

            CREATE TABLE IF NOT EXISTS ping_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scan_id INTEGER NOT NULL,
                checked_at REAL NOT NULL,
                machine_oid INTEGER NOT NULL,
                machine_name TEXT,
                device_type TEXT NOT NULL,
                ip_address TEXT NOT NULL,
                packets_sent INTEGER NOT NULL,
                packets_received INTEGER NOT NULL,
                packet_loss_pct REAL NOT NULL,
                avg_rtt_ms REAL,
                min_rtt_ms REAL,
                max_rtt_ms REAL,
                jitter_ms REAL,
                is_available INTEGER NOT NULL CHECK(is_available IN (0, 1)),
                FOREIGN KEY(scan_id) REFERENCES scan_runs(id)
            );

            CREATE TABLE IF NOT EXISTS device_status (
                machine_oid INTEGER NOT NULL,
                machine_name TEXT,
                device_type TEXT NOT NULL,
                ip_address TEXT NOT NULL,
                status TEXT NOT NULL,
                is_available INTEGER NOT NULL CHECK(is_available IN (0, 1)),
                fail_since REAL,
                last_checked_at REAL NOT NULL,
                packets_sent INTEGER NOT NULL,
                packets_received INTEGER NOT NULL,
                packet_loss_pct REAL NOT NULL,
                avg_rtt_ms REAL,
                PRIMARY KEY(machine_oid, device_type, ip_address)
            );

            CREATE TABLE IF NOT EXISTS downtime_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                machine_oid INTEGER NOT NULL,
                machine_name TEXT,
                device_type TEXT NOT NULL,
                ip_address TEXT NOT NULL,
                started_at REAL NOT NULL,
                ended_at REAL,
                duration_seconds REAL,
                created_at REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS availability_daily (
                machine_oid INTEGER NOT NULL,
                machine_name TEXT,
                device_type TEXT NOT NULL,
                ip_address TEXT NOT NULL,
                local_date TEXT NOT NULL,
                uptime_seconds REAL NOT NULL DEFAULT 0,
                downtime_seconds REAL NOT NULL DEFAULT 0,
                observed_seconds REAL NOT NULL DEFAULT 0,
                PRIMARY KEY(machine_oid, device_type, ip_address, local_date)
            );

            CREATE TABLE IF NOT EXISTS bd_daily (
                local_date TEXT NOT NULL,
                machine_oid INTEGER NOT NULL,
                PRIMARY KEY(local_date, machine_oid)
            );

            CREATE TABLE IF NOT EXISTS gps_state (
                machine_oid INTEGER PRIMARY KEY,
                last_ts REAL NOT NULL,
                last_normal INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS gps_satellite_status (
                machine_oid INTEGER PRIMARY KEY,
                machine_name TEXT,
                satellites REAL,
                gps_state TEXT NOT NULL,
                fetched_at REAL NOT NULL
            );

            CREATE TABLE IF NOT EXISTS monthly_exports (
                month TEXT PRIMARY KEY,
                exported_at REAL NOT NULL,
                rows INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_ping_history_time ON ping_history(checked_at);
            CREATE INDEX IF NOT EXISTS idx_ping_history_device_time ON ping_history(machine_oid, device_type, ip_address, checked_at);
            CREATE INDEX IF NOT EXISTS idx_downtime_device_start ON downtime_history(machine_oid, device_type, ip_address, started_at);
            CREATE INDEX IF NOT EXISTS idx_availability_date ON availability_daily(local_date, machine_oid, device_type);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_one_open_downtime_per_device ON downtime_history(machine_oid, device_type, ip_address) WHERE ended_at IS NULL;
            """;
        cmd.ExecuteNonQuery();
    }

    public static double LocalTs(TimeZoneInfo tz, DateOnly d, TimeOnly t)
    {
        var dt = d.ToDateTime(t);
        if (tz.IsInvalidTime(dt)) dt = dt.AddHours(1);
        var offset = tz.GetUtcOffset(dt);
        return new DateTimeOffset(dt, offset).ToUnixTimeMilliseconds() / 1000.0;
    }

    private static double BreakOverlap(double segStartTs, double segEndTs, DateOnly d, TimeZoneInfo tz, TimeOnly breakStart, TimeOnly breakEnd)
    {
        double bs = LocalTs(tz, d, breakStart);
        double be = LocalTs(tz, d, breakEnd);
        return Math.Max(0.0, Math.Min(segEndTs, be) - Math.Max(segStartTs, bs));
    }

    public static double CountedSecondsInDay(DateOnly d, DateTimeOffset? untilUtc, TimeZoneInfo tz, TimeOnly breakStart, TimeOnly breakEnd)
    {
        double startTs = LocalTs(tz, d, TimeOnly.MinValue);
        double endTs = LocalTs(tz, d.AddDays(1), TimeOnly.MinValue);
        if (untilUtc.HasValue)
            endTs = Math.Min(endTs, untilUtc.Value.ToUnixTimeMilliseconds() / 1000.0);

        return Math.Max(0.0, (endTs - startTs) - BreakOverlap(startTs, endTs, d, tz, breakStart, breakEnd));
    }

    private static void SplitIntervalByLocalDay(
        SqliteConnection conn,
        SqliteTransaction tx,
        double startTs,
        double endTs,
        long machineOid,
        string? machineName,
        string deviceType,
        string ipAddress,
        bool wasAvailable,
        TimeZoneInfo tz,
        TimeOnly breakStart,
        TimeOnly breakEnd)
    {
        double cursorTs = startTs;
        while (cursorTs < endTs)
        {
            var utcDt = DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(cursorTs)).UtcDateTime;
            var localDt = TimeZoneInfo.ConvertTimeFromUtc(utcDt, tz);
            var d = DateOnly.FromDateTime(localDt);
            double nextMidnight = LocalTs(tz, d.AddDays(1), TimeOnly.MinValue);
            double segmentEnd = Math.Min(endTs, nextMidnight);
            double seconds = Math.Max(0.0, (segmentEnd - cursorTs) - BreakOverlap(cursorTs, segmentEnd, d, tz, breakStart, breakEnd));

            if (seconds > 0.0)
            {
                double uptime = wasAvailable ? seconds : 0.0;
                double downtime = wasAvailable ? 0.0 : seconds;
                string localDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO availability_daily
                    (machine_oid, machine_name, device_type, ip_address, local_date,
                     uptime_seconds, downtime_seconds, observed_seconds)
                    VALUES ($oid, $name, $dtype, $ip, $ldate, $up, $down, $obs)
                    ON CONFLICT(machine_oid, device_type, ip_address, local_date)
                    DO UPDATE SET
                      machine_name = excluded.machine_name,
                      uptime_seconds = availability_daily.uptime_seconds + excluded.uptime_seconds,
                      downtime_seconds = availability_daily.downtime_seconds + excluded.downtime_seconds,
                      observed_seconds = availability_daily.observed_seconds + excluded.observed_seconds
                    """;
                cmd.Parameters.AddWithValue("$oid", machineOid);
                cmd.Parameters.AddWithValue("$name", (object?)machineName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dtype", deviceType);
                cmd.Parameters.AddWithValue("$ip", ipAddress);
                cmd.Parameters.AddWithValue("$ldate", localDate);
                cmd.Parameters.AddWithValue("$up", uptime);
                cmd.Parameters.AddWithValue("$down", downtime);
                cmd.Parameters.AddWithValue("$obs", seconds);
                cmd.ExecuteNonQuery();
            }
            cursorTs = segmentEnd;
        }
    }

    private static void CloseOpenDowntime(SqliteConnection conn, SqliteTransaction tx, long oid, string deviceType, string ipAddress, double endedAt, double? failSince)
    {
        double started = failSince ?? endedAt;
        double dur = Math.Max(0.0, endedAt - started);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE downtime_history
            SET ended_at = $ended, duration_seconds = $dur
            WHERE id = (
                SELECT id FROM downtime_history
                WHERE machine_oid = $oid AND device_type = $dtype AND ip_address = $ip
                  AND ended_at IS NULL
                ORDER BY id DESC LIMIT 1
            )
            """;
        cmd.Parameters.AddWithValue("$ended", endedAt);
        cmd.Parameters.AddWithValue("$dur", dur);
        cmd.Parameters.AddWithValue("$oid", oid);
        cmd.Parameters.AddWithValue("$dtype", deviceType);
        cmd.Parameters.AddWithValue("$ip", ipAddress);
        cmd.ExecuteNonQuery();
    }

    public static long SaveScanResults(
        AppConfig cfg,
        double startedAt,
        double finishedAt,
        List<MachineTarget> targets,
        Dictionary<string, PingStat> results,
        string[] skipAvailabilityTypes)
    {
        EnsureExcludedOidsFresh(cfg);
        using var conn = ConnectDb(cfg.DbFile);
        using var tx = conn.BeginTransaction();

        long scanId;
        using (var runCmd = conn.CreateCommand())
        {
            runCmd.Transaction = tx;
            runCmd.CommandText = """
                INSERT INTO scan_runs
                (started_at, finished_at, duration_seconds, target_count, checked_count, scan_status)
                VALUES ($s, $f, $d, $tc, $cc, 'SUCCESS');
                SELECT last_insert_rowid();
                """;
            runCmd.Parameters.AddWithValue("$s", startedAt);
            runCmd.Parameters.AddWithValue("$f", finishedAt);
            runCmd.Parameters.AddWithValue("$d", Math.Max(0.0, finishedAt - startedAt));
            runCmd.Parameters.AddWithValue("$tc", targets.Count);
            runCmd.Parameters.AddWithValue("$cc", targets.Count);
            scanId = (long)(runCmd.ExecuteScalar() ?? 0L);
        }

        foreach (var target in targets)
        {
            results.TryGetValue(target.IpAddress, out var stat);
            int packetsSent = stat?.PacketsSent ?? cfg.PingCount;
            int packetsReceived = stat?.PacketsReceived ?? 0;
            double loss = stat?.PacketLossPct ?? 100.0;
            double? avgRtt = stat?.AvgRttMs;
            bool isAvailable = loss <= cfg.FailureLossThresholdPercent;

            using (var histCmd = conn.CreateCommand())
            {
                histCmd.Transaction = tx;
                histCmd.CommandText = """
                    INSERT INTO ping_history
                    (scan_id, checked_at, machine_oid, machine_name, device_type,
                     ip_address, packets_sent, packets_received, packet_loss_pct,
                     avg_rtt_ms, min_rtt_ms, max_rtt_ms, jitter_ms, is_available)
                    VALUES ($sid, $chk, $oid, $name, $dtype, $ip, $ps, $pr, $loss, $rtt, NULL, NULL, NULL, $avail)
                    """;
                histCmd.Parameters.AddWithValue("$sid", scanId);
                histCmd.Parameters.AddWithValue("$chk", finishedAt);
                histCmd.Parameters.AddWithValue("$oid", target.MachineOid);
                histCmd.Parameters.AddWithValue("$name", target.MachineName);
                histCmd.Parameters.AddWithValue("$dtype", target.DeviceType);
                histCmd.Parameters.AddWithValue("$ip", target.IpAddress);
                histCmd.Parameters.AddWithValue("$ps", packetsSent);
                histCmd.Parameters.AddWithValue("$pr", packetsReceived);
                histCmd.Parameters.AddWithValue("$loss", loss);
                histCmd.Parameters.AddWithValue("$rtt", (object?)avgRtt ?? DBNull.Value);
                histCmd.Parameters.AddWithValue("$avail", isAvailable ? 1 : 0);
                histCmd.ExecuteNonQuery();
            }

            bool hasPrev = false;
            bool prevIsAvail = false;
            double? prevFailSince = null;
            double prevLastChk = 0.0;

            using (var prevCmd = conn.CreateCommand())
            {
                prevCmd.Transaction = tx;
                prevCmd.CommandText = """
                    SELECT is_available, fail_since, last_checked_at
                    FROM device_status
                    WHERE machine_oid = $oid AND device_type = $dtype AND ip_address = $ip
                    """;
                prevCmd.Parameters.AddWithValue("$oid", target.MachineOid);
                prevCmd.Parameters.AddWithValue("$dtype", target.DeviceType);
                prevCmd.Parameters.AddWithValue("$ip", target.IpAddress);

                using var rdr = prevCmd.ExecuteReader();
                if (rdr.Read())
                {
                    hasPrev = true;
                    prevIsAvail = rdr.GetInt64(0) != 0;
                    prevFailSince = rdr.IsDBNull(1) ? null : rdr.GetDouble(1);
                    prevLastChk = rdr.GetDouble(2);
                }
            }

            double? gapSeconds = null;
            if (hasPrev)
            {
                double gap = finishedAt - prevLastChk;
                gapSeconds = gap;
                bool isExcludedUnit = _excludedAvailabilityOids.Contains(target.MachineOid);
                if (!isExcludedUnit && gap > 0.0 && gap <= cfg.MaxObservationGapSeconds && !skipAvailabilityTypes.Contains(target.DeviceType))
                {
                    SplitIntervalByLocalDay(
                        conn, tx, prevLastChk, finishedAt,
                        target.MachineOid, target.MachineName, target.DeviceType, target.IpAddress,
                        prevIsAvail, cfg.Timezone, cfg.BreakStart, cfg.BreakEnd);
                }
            }

            bool staleGap = gapSeconds.HasValue && (gapSeconds.Value <= 0.0 || gapSeconds.Value > cfg.MaxObservationGapSeconds);
            double? failSince;

            if (hasPrev && !prevIsAvail && staleGap)
            {
                CloseOpenDowntime(conn, tx, target.MachineOid, target.DeviceType, target.IpAddress, prevLastChk, prevFailSince);
                if (!isAvailable)
                {
                    failSince = finishedAt;
                    InsertOpenDowntime(conn, tx, target, finishedAt);
                }
                else
                {
                    failSince = null;
                }
            }
            else if (isAvailable)
            {
                if (hasPrev && !prevIsAvail)
                    CloseOpenDowntime(conn, tx, target.MachineOid, target.DeviceType, target.IpAddress, finishedAt, prevFailSince);
                failSince = null;
            }
            else if (hasPrev && !prevIsAvail)
            {
                failSince = prevFailSince ?? finishedAt;
            }
            else
            {
                failSince = finishedAt;
                InsertOpenDowntime(conn, tx, target, finishedAt);
            }

            string status;
            if (isAvailable)
            {
                status = loss == 0.0 ? "Green" : "Yellow";
            }
            else
            {
                double elapsed = Math.Max(0.0, finishedAt - (failSince ?? finishedAt));
                status = elapsed >= cfg.RedAfterMinutes * 60.0 ? "Red" : "Orange";
            }

            using var upsertCmd = conn.CreateCommand();
            upsertCmd.Transaction = tx;
            upsertCmd.CommandText = """
                INSERT INTO device_status
                (machine_oid, machine_name, device_type, ip_address, status,
                 is_available, fail_since, last_checked_at, packets_sent,
                 packets_received, packet_loss_pct, avg_rtt_ms)
                VALUES ($oid, $name, $dtype, $ip, $st, $avail, $fs, $chk, $ps, $pr, $loss, $rtt)
                ON CONFLICT(machine_oid, device_type, ip_address) DO UPDATE SET
                  machine_name = excluded.machine_name,
                  status = excluded.status,
                  is_available = excluded.is_available,
                  fail_since = excluded.fail_since,
                  last_checked_at = excluded.last_checked_at,
                  packets_sent = excluded.packets_sent,
                  packets_received = excluded.packets_received,
                  packet_loss_pct = excluded.packet_loss_pct,
                  avg_rtt_ms = excluded.avg_rtt_ms
                """;
            upsertCmd.Parameters.AddWithValue("$oid", target.MachineOid);
            upsertCmd.Parameters.AddWithValue("$name", target.MachineName);
            upsertCmd.Parameters.AddWithValue("$dtype", target.DeviceType);
            upsertCmd.Parameters.AddWithValue("$ip", target.IpAddress);
            upsertCmd.Parameters.AddWithValue("$st", status);
            upsertCmd.Parameters.AddWithValue("$avail", isAvailable ? 1 : 0);
            upsertCmd.Parameters.AddWithValue("$fs", (object?)failSince ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue("$chk", finishedAt);
            upsertCmd.Parameters.AddWithValue("$ps", packetsSent);
            upsertCmd.Parameters.AddWithValue("$pr", packetsReceived);
            upsertCmd.Parameters.AddWithValue("$loss", loss);
            upsertCmd.Parameters.AddWithValue("$rtt", (object?)avgRtt ?? DBNull.Value);
            upsertCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return scanId;
    }

    private static void InsertOpenDowntime(SqliteConnection conn, SqliteTransaction tx, MachineTarget target, double ts)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO downtime_history
            (machine_oid, machine_name, device_type, ip_address, started_at, created_at)
            VALUES ($oid, $name, $dtype, $ip, $st, $cr)
            """;
        cmd.Parameters.AddWithValue("$oid", target.MachineOid);
        cmd.Parameters.AddWithValue("$name", target.MachineName);
        cmd.Parameters.AddWithValue("$dtype", target.DeviceType);
        cmd.Parameters.AddWithValue("$ip", target.IpAddress);
        cmd.Parameters.AddWithValue("$st", ts);
        cmd.Parameters.AddWithValue("$cr", ts);
        cmd.ExecuteNonQuery();
    }

    public static void RecordFailedScan(string dbPath, double startedAt, double finishedAt, int targetCount, string errorMessage)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        var msg = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;
        cmd.CommandText = """
            INSERT INTO scan_runs
            (started_at, finished_at, duration_seconds, target_count, checked_count, scan_status, error_message)
            VALUES ($s, $f, $d, $tc, 0, 'FAILED', $msg)
            """;
        cmd.Parameters.AddWithValue("$s", startedAt);
        cmd.Parameters.AddWithValue("$f", finishedAt);
        cmd.Parameters.AddWithValue("$d", Math.Max(0.0, finishedAt - startedAt));
        cmd.Parameters.AddWithValue("$tc", targetCount);
        cmd.Parameters.AddWithValue("$msg", msg);
        cmd.ExecuteNonQuery();
    }

    public static List<(long Oid, string Name)> LoadCachedMachineMap(AppConfig cfg)
    {
        try
        {
            var cached = MachineMapService.LoadCachedOidNames(cfg);
            if (cached.Count > 0) return cached;
        }
        catch { }

        using var conn = ConnectDb(cfg.DbFile);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT machine_oid, COALESCE(machine_name, 'OID-' || machine_oid)
            FROM device_status ORDER BY machine_oid
            """;
        using var rdr = cmd.ExecuteReader();
        var list = new List<(long, string)>();
        while (rdr.Read())
            list.Add((rdr.GetInt64(0), rdr.GetString(1)));
        return list;
    }

    public static void UpsertGpsSatelliteStatus(string dbPath, List<(long Oid, string Name, double? Sat, string State, double FetchedAt)> rows)
    {
        using var conn = ConnectDb(dbPath);
        using var tx = conn.BeginTransaction();
        foreach (var (oid, name, sat, state, fetchedAt) in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO gps_satellite_status (machine_oid, machine_name, satellites, gps_state, fetched_at)
                VALUES ($oid, $name, $sat, $st, $fa)
                ON CONFLICT(machine_oid) DO UPDATE SET
                  machine_name = excluded.machine_name,
                  satellites = excluded.satellites,
                  gps_state = excluded.gps_state,
                  fetched_at = excluded.fetched_at
                """;
            cmd.Parameters.AddWithValue("$oid", oid);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$sat", (object?)sat ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$st", state);
            cmd.Parameters.AddWithValue("$fa", fetchedAt);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static int SaveGpsSamples(AppConfig cfg, List<(long Oid, string Name, bool Normal)> samples, int maxGapSeconds)
    {
        EnsureExcludedOidsFresh(cfg);
        double nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        int counted = 0;

        using var conn = ConnectDb(cfg.DbFile);
        using var tx = conn.BeginTransaction();

        foreach (var (oid, name, normal) in samples)
        {
            double? lastTs = null;
            long lastNormal = 0;

            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT last_ts, last_normal FROM gps_state WHERE machine_oid = $oid";
                sel.Parameters.AddWithValue("$oid", oid);
                using var rdr = sel.ExecuteReader();
                if (rdr.Read())
                {
                    lastTs = rdr.GetDouble(0);
                    lastNormal = rdr.GetInt64(1);
                }
            }

            if (lastTs.HasValue)
            {
                double gap = nowTs - lastTs.Value;
                if (!_excludedAvailabilityOids.Contains(oid) && gap > 0.0 && gap <= maxGapSeconds)
                {
                    SplitIntervalByLocalDay(
                        conn, tx, lastTs.Value, nowTs,
                        oid, name, "G", Constants.GpsAvailabilityKey,
                        lastNormal != 0, cfg.Timezone, cfg.BreakStart, cfg.BreakEnd);
                    counted++;
                }
            }

            using var up = conn.CreateCommand();
            up.Transaction = tx;
            up.CommandText = """
                INSERT INTO gps_state (machine_oid, last_ts, last_normal)
                VALUES ($oid, $ts, $norm)
                ON CONFLICT(machine_oid) DO UPDATE SET
                  last_ts = excluded.last_ts,
                  last_normal = excluded.last_normal
                """;
            up.Parameters.AddWithValue("$oid", oid);
            up.Parameters.AddWithValue("$ts", nowTs);
            up.Parameters.AddWithValue("$norm", normal ? 1 : 0);
            up.ExecuteNonQuery();
        }

        tx.Commit();
        return counted;
    }

    public static Dictionary<(long Oid, string Dtype, string Ip), DeviceStatusRow> GetLatestStatusMap(string dbPath)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT machine_oid, machine_name, device_type, ip_address, status,
                   is_available, fail_since, last_checked_at, packets_sent,
                   packets_received, packet_loss_pct, avg_rtt_ms
            FROM device_status
            """;
        using var rdr = cmd.ExecuteReader();
        var map = new Dictionary<(long, string, string), DeviceStatusRow>();
        while (rdr.Read())
        {
            var row = new DeviceStatusRow
            {
                MachineOid = rdr.GetInt64(0),
                MachineName = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                DeviceType = rdr.GetString(2),
                IpAddress = rdr.GetString(3),
                Status = rdr.GetString(4),
                IsAvailable = rdr.GetInt64(5) != 0,
                FailSince = rdr.IsDBNull(6) ? null : rdr.GetDouble(6),
                LastCheckedAt = rdr.GetDouble(7),
                PacketsSent = rdr.GetInt64(8),
                PacketsReceived = rdr.GetInt64(9),
                PacketLossPct = rdr.GetDouble(10),
                AvgRttMs = rdr.IsDBNull(11) ? null : rdr.GetDouble(11)
            };
            map[(row.MachineOid, row.DeviceType, row.IpAddress)] = row;
        }
        return map;
    }

    public static Dictionary<(long Oid, string Dtype, string Ip), DailyAvailability> GetDailyAvailabilityMap(string dbPath, string localDate)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT machine_oid, device_type, ip_address, uptime_seconds, downtime_seconds, observed_seconds
            FROM availability_daily WHERE local_date = $ldate
            """;
        cmd.Parameters.AddWithValue("$ldate", localDate);
        using var rdr = cmd.ExecuteReader();
        var map = new Dictionary<(long, string, string), DailyAvailability>();
        while (rdr.Read())
        {
            long oid = rdr.GetInt64(0);
            string dtype = rdr.GetString(1);
            string ip = rdr.GetString(2);
            double up = rdr.GetDouble(3);
            double down = rdr.GetDouble(4);
            double obs = rdr.GetDouble(5);
            double? pct = obs > 0.0 ? (up / obs * 100.0) : null;
            map[(oid, dtype, ip)] = new DailyAvailability(up, down, obs, pct);
        }
        return map;
    }

    public static ScanRun? GetLatestScan(string dbPath)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, started_at, finished_at, duration_seconds, target_count,
                   checked_count, scan_status, error_message
            FROM scan_runs ORDER BY id DESC LIMIT 1
            """;
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read()) return null;
        return new ScanRun
        {
            Id = rdr.GetInt64(0),
            StartedAt = rdr.GetDouble(1),
            FinishedAt = rdr.IsDBNull(2) ? null : rdr.GetDouble(2),
            DurationSeconds = rdr.IsDBNull(3) ? null : rdr.GetDouble(3),
            TargetCount = rdr.GetInt64(4),
            CheckedCount = rdr.GetInt64(5),
            ScanStatus = rdr.GetString(6),
            ErrorMessage = rdr.IsDBNull(7) ? null : rdr.GetString(7)
        };
    }

    public static (int DeletedHistory, int DeletedScans) CleanupOldHistory(string dbPath, int retentionDays)
    {
        if (retentionDays <= 0) return (0, 0);
        double cutoff = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0) - (retentionDays * 86400.0);

        using var conn = ConnectDb(dbPath);
        using var tx = conn.BeginTransaction();

        int delHist, delScans;
        using (var c1 = conn.CreateCommand())
        {
            c1.Transaction = tx;
            c1.CommandText = "DELETE FROM ping_history WHERE checked_at < $cut";
            c1.Parameters.AddWithValue("$cut", cutoff);
            delHist = c1.ExecuteNonQuery();
        }
        using (var c2 = conn.CreateCommand())
        {
            c2.Transaction = tx;
            c2.CommandText = "DELETE FROM scan_runs WHERE finished_at < $cut";
            c2.Parameters.AddWithValue("$cut", cutoff);
            delScans = c2.ExecuteNonQuery();
        }
        tx.Commit();
        return (delHist, delScans);
    }

    public static string AddMonths(string month, int n)
    {
        if (month.Length < 7 || !int.TryParse(month[..4], out int year) || !int.TryParse(month.Substring(5, 2), out int m))
            throw new FormatException("Format bulan salah");
        int total = year * 12 + (m - 1) + n;
        int newYear = (int)Math.Floor(total / 12.0);
        int newMonth = ((total % 12) + 12) % 12 + 1;
        return $"{newYear:D4}-{newMonth:D2}";
    }

    public static (string FirstDate, string NextMonthFirstDate) MonthRange(string month) =>
        ($"{month}-01", $"{AddMonths(month, 1)}-01");

    public static void RecordBdDay(string dbPath, string localDate, IEnumerable<long> oids)
    {
        var list = oids.ToList();
        if (list.Count == 0) return;
        using var conn = ConnectDb(dbPath);
        using var tx = conn.BeginTransaction();
        foreach (var oid in list)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO bd_daily (local_date, machine_oid) VALUES ($d, $o)";
            cmd.Parameters.AddWithValue("$d", localDate);
            cmd.Parameters.AddWithValue("$o", oid);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static HashSet<(string LocalDate, long Oid)> GetBdDays(string dbPath, string firstDate, string lastDate)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT local_date, machine_oid FROM bd_daily WHERE local_date >= $f AND local_date <= $l";
        cmd.Parameters.AddWithValue("$f", firstDate);
        cmd.Parameters.AddWithValue("$l", lastDate);
        using var rdr = cmd.ExecuteReader();
        var set = new HashSet<(string, long)>();
        while (rdr.Read())
            set.Add((rdr.GetString(0), rdr.GetInt64(1)));
        return set;
    }

    public static void MarkMonthExported(string dbPath, string month, int rows)
    {
        double nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO monthly_exports (month, exported_at, rows) VALUES ($m, $e, $r)
            ON CONFLICT(month) DO UPDATE SET exported_at = excluded.exported_at, rows = excluded.rows
            """;
        cmd.Parameters.AddWithValue("$m", month);
        cmd.Parameters.AddWithValue("$e", nowTs);
        cmd.Parameters.AddWithValue("$r", rows);
        cmd.ExecuteNonQuery();
    }

    public static List<Dictionary<string, object?>> ListMonthStatus(string dbPath)
    {
        using var conn = ConnectDb(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT substr(a.local_date, 1, 7) AS month,
                   MIN(a.local_date) AS first_date,
                   MAX(a.local_date) AS last_date,
                   COUNT(DISTINCT a.local_date) AS days,
                   e.exported_at
            FROM availability_daily a
            LEFT JOIN monthly_exports e ON e.month = substr(a.local_date, 1, 7)
            GROUP BY substr(a.local_date, 1, 7)
            ORDER BY month DESC
            """;
        using var rdr = cmd.ExecuteReader();
        var outList = new List<Dictionary<string, object?>>();
        while (rdr.Read())
        {
            outList.Add(new Dictionary<string, object?>
            {
                ["month"] = rdr.GetString(0),
                ["first_date"] = rdr.GetString(1),
                ["last_date"] = rdr.GetString(2),
                ["days"] = rdr.GetInt64(3),
                ["exported_at"] = rdr.IsDBNull(4) ? null : rdr.GetDouble(4)
            });
        }
        return outList;
    }

    public static Dictionary<string, (int Avail, int Bd, int Downtime)> PurgeExportedMonths(AppConfig cfg, int keepMonths)
    {
        var outMap = new Dictionary<string, (int, int, int)>();
        if (keepMonths < 0) return outMap;

        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone);
        var curMonth = $"{todayLocal.Year:D4}-{todayLocal.Month:D2}";
        var cutoff = AddMonths(curMonth, -keepMonths);

        using var conn = ConnectDb(cfg.DbFile);
        var months = new List<string>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT month FROM monthly_exports WHERE month < $cut ORDER BY month";
            sel.Parameters.AddWithValue("$cut", cutoff);
            using var rdr = sel.ExecuteReader();
            while (rdr.Read()) months.Add(rdr.GetString(0));
        }

        foreach (var month in months)
        {
            var (first, nxt) = MonthRange(month);
            var dStart = DateOnly.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dEnd = DateOnly.ParseExact(nxt, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            double startTs = LocalTs(cfg.Timezone, dStart, TimeOnly.MinValue);
            double endTs = LocalTs(cfg.Timezone, dEnd, TimeOnly.MinValue);

            using var tx = conn.BeginTransaction();
            int a, b, d;
            using (var c1 = conn.CreateCommand())
            {
                c1.Transaction = tx;
                c1.CommandText = "DELETE FROM availability_daily WHERE local_date >= $f AND local_date < $n";
                c1.Parameters.AddWithValue("$f", first);
                c1.Parameters.AddWithValue("$n", nxt);
                a = c1.ExecuteNonQuery();
            }
            using (var c2 = conn.CreateCommand())
            {
                c2.Transaction = tx;
                c2.CommandText = "DELETE FROM bd_daily WHERE local_date >= $f AND local_date < $n";
                c2.Parameters.AddWithValue("$f", first);
                c2.Parameters.AddWithValue("$n", nxt);
                b = c2.ExecuteNonQuery();
            }
            using (var c3 = conn.CreateCommand())
            {
                c3.Transaction = tx;
                c3.CommandText = "DELETE FROM downtime_history WHERE started_at >= $s AND started_at < $e AND ended_at IS NOT NULL";
                c3.Parameters.AddWithValue("$s", startTs);
                c3.Parameters.AddWithValue("$e", endTs);
                d = c3.ExecuteNonQuery();
            }
            tx.Commit();
            if (a > 0 || b > 0 || d > 0)
                outMap[month] = (a, b, d);
        }
        return outMap;
    }

    public static List<Dictionary<string, object?>> BuildEnrichedDashboardRows(AppConfig cfg, double nowTs, double staleLimitSecs)
    {
        var statusMap = GetLatestStatusMap(cfg.DbFile);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var availMap = GetDailyAvailabilityMap(cfg.DbFile, todayLocal);

        var gpsMap = new Dictionary<long, (double? Sats, string State)>();
        using (var sqliteConn = ConnectDb(cfg.DbFile))
        using (var gpsCmd = sqliteConn.CreateCommand())
        {
            gpsCmd.CommandText = "SELECT machine_oid, satellites, gps_state, fetched_at FROM gps_satellite_status";
            using var rdr = gpsCmd.ExecuteReader();
            while (rdr.Read())
            {
                long oid = rdr.GetInt64(0);
                double? sats = rdr.IsDBNull(1) ? null : rdr.GetDouble(1);
                string state = rdr.GetString(2);
                double fetchedAt = rdr.GetDouble(3);
                if ((nowTs - fetchedAt) <= Math.Max(staleLimitSecs, 600.0))
                    gpsMap[oid] = (sats, state);
            }
        }

        const string sql = """
            WITH FilteredDelays AS (
                SELECT DISTINCT
                    P.TARGET_MACHINE AS MACHINE_OID,
                    J1.NAME AS DELAY_NAME,
                    J1.DELAYCATEGORY,
                    J1.DELAYCLASS_OID
                FROM [mshist_rep].[dbo].[DELAY] AS P
                LEFT JOIN [msmodel_rep].[dbo].[DELAYCLASS] AS J1
                    ON P.DELAYCLASS = J1.DELAYCLASS_OID
                LEFT JOIN [msmodel_rep].[dbo].[DELAYCATEGORY] AS J3
                    ON J1.DELAYCATEGORY = J3.DELAYCATEGORY_OID
                WHERE P.STATUS = 3
            )
            SELECT
                m.MACHINE_OID AS OID,
                m.NAME AS MachineName,
                CAST(m.IS_ACTIVE AS INT) AS MachineStatus,
                d.DELAYCATEGORY AS DelayCategory,
                d.DELAYCLASS_OID AS DelayClassOid,
                d.DELAY_NAME AS DelayClassName
            FROM [msmodel_rep].[dbo].[MACHINE] m
            LEFT JOIN FilteredDelays d ON d.MACHINE_OID = m.MACHINE_OID
            WHERE LEFT(UPPER(LTRIM(RTRIM(m.NAME))), 2) IN ('CB', 'HB', 'KB', 'CT', 'KT')
            ORDER BY m.NAME
            """;

        using var odbcConn = new OdbcConnection(cfg.SqlConnectionString);
        odbcConn.Open();
        using var odbcCmd = new OdbcCommand(sql, odbcConn);
        using var odbcReader = odbcCmd.ExecuteReader();

        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < odbcReader.FieldCount; i++)
            cols[odbcReader.GetName(i)] = i;

        int oidIdx = cols.GetValueOrDefault("OID", 0);
        int nameIdx = cols.GetValueOrDefault("MachineName", 1);
        cols.TryGetValue("MachineStatus", out int mstatIdx);
        bool hasMstat = cols.ContainsKey("MachineStatus");
        bool hasDcat = cols.TryGetValue("DelayCategory", out int dcatIdx);
        bool hasDcls = cols.TryGetValue("DelayClassOid", out int dclsIdx);
        bool hasDname = cols.TryGetValue("DelayClassName", out int dnameIdx);

        var devByOidType = new Dictionary<(long, string), DeviceStatusRow>();
        foreach (var ((oid, dtype, _), dev) in statusMap)
            devByOidType[(oid, dtype)] = dev;

        var rowsOut = new List<Dictionary<string, object?>>();
        var bdOids = new List<long>();
        var excludedOids = new HashSet<long>();

        while (odbcReader.Read())
        {
            if (odbcReader.IsDBNull(oidIdx)) continue;
            var oidStr = odbcReader.GetValue(oidIdx)?.ToString()?.Trim();
            if (!double.TryParse(oidStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double oidD)) continue;
            long oid = (long)oidD;

            string machineName = odbcReader.IsDBNull(nameIdx)
                ? $"OID-{oid}"
                : (odbcReader.GetValue(nameIdx)?.ToString()?.Trim() is { Length: > 0 } n ? n : $"OID-{oid}");

            long mStatus = 0;
            if (hasMstat && !odbcReader.IsDBNull(mstatIdx))
                long.TryParse(odbcReader.GetValue(mstatIdx)?.ToString()?.Trim(), out mStatus);

            long? delayCat = null;
            if (hasDcat && !odbcReader.IsDBNull(dcatIdx) && long.TryParse(odbcReader.GetValue(dcatIdx)?.ToString()?.Trim(), out long dc))
                delayCat = dc;

            long? delayCls = null;
            if (hasDcls && !odbcReader.IsDBNull(dclsIdx) && long.TryParse(odbcReader.GetValue(dclsIdx)?.ToString()?.Trim(), out long dco))
                delayCls = dco;

            string delayName = (hasDname && !odbcReader.IsDBNull(dnameIdx))
                ? (odbcReader.GetValue(dnameIdx)?.ToString()?.Trim() ?? "")
                : "";

            bool isIdwp = delayName.Contains("IDWP", StringComparison.OrdinalIgnoreCase);
            if (isIdwp)
            {
                bdOids.Add(oid);
                excludedOids.Add(oid);
                continue;
            }

            var pingObj = new Dictionary<string, object?>();
            foreach (var dtype in Constants.OverallDeviceTypes)
            {
                if (devByOidType.TryGetValue((oid, dtype), out var dev) && !string.IsNullOrWhiteSpace(dev.IpAddress))
                {
                    availMap.TryGetValue((oid, dtype, dev.IpAddress), out var av);
                    pingObj[dtype] = new Dictionary<string, object?>
                    {
                        ["ip"] = dev.IpAddress,
                        ["status"] = dev.Status,
                        ["is_available"] = dev.IsAvailable,
                        ["loss_pct"] = dev.PacketLossPct,
                        ["avg_rtt_ms"] = dev.AvgRttMs,
                        ["availability_pct"] = av?.AvailabilityPct
                    };
                }
            }

            if (pingObj.Count == 0) continue;

            bool isAntri = delayName.Contains("Antri", StringComparison.OrdinalIgnoreCase);
            bool isBd = delayCat is 165096025 or 165096022 || delayCls is 165097066 or 165097069;
            bool hasDelay = !string.IsNullOrWhiteSpace(delayName) || delayCat.HasValue || delayCls.HasValue;
            if (mStatus != 1 || isBd || hasDelay) excludedOids.Add(oid);
            if (isBd || isAntri) bdOids.Add(oid);

            double? satellites = null;
            string? gpsStateStr = null;
            if (gpsMap.TryGetValue(oid, out var gInfo))
            {
                satellites = gInfo.Sats;
                gpsStateStr = gInfo.State;
            }
            availMap.TryGetValue((oid, "G", Constants.GpsAvailabilityKey), out var gpsAv);

            rowsOut.Add(new Dictionary<string, object?>
            {
                ["OID"] = oid,
                ["Name"] = machineName,
                ["MachineStatus"] = mStatus,
                ["DelayCategory"] = delayCat,
                ["DelayClassOid"] = delayCls,
                ["DelayClassName"] = delayName,
                ["is_bd"] = isBd,
                ["ping"] = pingObj,
                ["GPS_SATELLITES"] = satellites,
                ["GPS_STATE"] = gpsStateStr,
                ["gps_availability_pct"] = gpsAv?.AvailabilityPct
            });
        }

        _excludedAvailabilityOids = excludedOids;
        Interlocked.Exchange(ref _lastExcludedRefreshSec, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (bdOids.Count > 0)
        {
            try { RecordBdDay(cfg.DbFile, todayLocal, bdOids); } catch { }
        }

        return rowsOut;
    }
}
