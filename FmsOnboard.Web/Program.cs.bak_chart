using System.Globalization;
using FmsOnboard.Core;
using FmsOnboard.Web;

var cfg = AppConfig.FromEnv();
DatabaseService.InitializeDb(cfg.DbFile);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{cfg.Host}:{cfg.Port}");

builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<TelemetrySnapshotStore>();
builder.Services.AddRazorPages();
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 1024 * 1024;
});
builder.Services.AddHostedService<DashboardUpdaterService>();

var app = builder.Build();

app.UseMiddleware<AuthMiddleware>();
app.UseStaticFiles();
app.MapRazorPages();
app.MapHub<TelemetryHub>("/telemetryHub");

app.MapGet("/api/status", (AppConfig c) =>
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, c.Timezone));
    object? overall = null;
    try { overall = AvailabilityReportService.BuildOverallPayload(c, today); } catch { }
    return Results.Json(new
    {
        status = "ok",
        overall,
        collector = DashboardUpdaterService.BuildCollectorStatus(c)
    });
});

app.MapGet("/export/availability.xlsx", async (AppConfig c, string? date) =>
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, c.Timezone));
    var reportDate = DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : today;
    var dateStr = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    try
    {
        var bytes = await Task.Run(() =>
        {
            var rep = AvailabilityReportService.BuildReport(c, reportDate);
            return AvailabilityReportService.BuildXlsxBytes(rep);
        });
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"network_availability_{dateStr}.xlsx");
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/export/availability.pdf", async (AppConfig c, string? date) =>
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, c.Timezone));
    var reportDate = DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : today;
    var dateStr = reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    try
    {
        var bytes = await Task.Run(() =>
        {
            var rep = AvailabilityReportService.BuildReport(c, reportDate);
            return AvailabilityReportService.BuildPdfBytes(rep);
        });
        return Results.File(bytes, "application/pdf", $"network_availability_{dateStr}.pdf");
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/export/availability_monthly.xlsx", async (AppConfig c, string? month) =>
{
    var defaultM = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, c.Timezone).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    var m = string.IsNullOrWhiteSpace(month) ? defaultM : month.Trim();

    try
    {
        var bytes = await Task.Run(() => MonthlyReportService.BuildMonthlyXlsxBytes(c, m));
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"availability_ONB_{m}.xlsx");
    }
    catch (Exception ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/api/availability/hourly", (AppConfig c, string? date) =>
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, c.Timezone));
    var reportDate = DateOnly.TryParseExact(date?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : today;
    try
    {
        var payload = DatabaseService.GetHourlyRdpAvailability(c, reportDate);
        return Results.Json(payload);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();
