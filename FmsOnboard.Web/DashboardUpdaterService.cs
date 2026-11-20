using FmsOnboard.Core;
using Microsoft.AspNetCore.SignalR;

namespace FmsOnboard.Web;

public sealed class DashboardUpdaterService(
    AppConfig cfg,
    TelemetrySnapshotStore store,
    IHubContext<TelemetryHub> hubContext,
    ILogger<DashboardUpdaterService> logger) : BackgroundService
{
    public static Dictionary<string, object?> BuildCollectorStatus(AppConfig cfg)
    {
        var scan = DatabaseService.GetLatestScan(cfg.DbFile);
        double staleLimit = Math.Max(60, cfg.ScanIntervalSeconds) * 3.0;
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        bool stale, failed;
        string? lastMsg;
        if (scan is not null)
        {
            double finished = scan.FinishedAt ?? 0.0;
            stale = finished <= 0.0 || (now - finished) > staleLimit;
            failed = scan.ScanStatus == "FAILED";
            lastMsg = scan.ErrorMessage;
        }
        else
        {
            stale = true;
            failed = false;
            lastMsg = "Belum ada siklus scan SQLite.";
        }

        return new Dictionary<string, object?>
        {
            ["stale"] = stale,
            ["failed"] = failed,
            ["error_message"] = lastMsg,
            ["latest_scan"] = scan
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone));
        Dictionary<string, object?>? initOverall = null;
        try { initOverall = AvailabilityReportService.BuildOverallPayload(cfg, today); } catch { }
        store.Update([], initOverall ?? [], BuildCollectorStatus(cfg));

        var interval = TimeSpan.FromSeconds(Math.Max(5, cfg.SqlRefreshSeconds));
        double satStaleLimit = Math.Max(600.0, Math.Max(1, cfg.GpsQueryRefreshMinutes) * 180.0);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (store.ActiveClients <= 0)
            {
                logger.LogInformation("[WS] Tidak ada klien aktif; polling dashboard dijeda.");
                await store.WaitForActiveClientAsync(stoppingToken);
            }

            try
            {
                var (rows, overall, collector) = await Task.Run(() =>
                {
                    double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    var r = DatabaseService.BuildEnrichedDashboardRows(cfg, now, satStaleLimit);
                    var td = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, cfg.Timezone));
                    var ov = AvailabilityReportService.BuildOverallPayload(cfg, td);
                    var col = BuildCollectorStatus(cfg);
                    return (r, ov, col);
                }, stoppingToken);

                store.Update(rows, overall, collector);
                await hubContext.Clients.All.SendAsync("availability_overall", overall, stoppingToken);
                await hubContext.Clients.All.SendAsync("collector_status", collector, stoppingToken);
                await hubContext.Clients.All.SendAsync("database_data", rows, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("[WS] Gagal memperbarui data dashboard: {Msg}", ex.Message); }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
