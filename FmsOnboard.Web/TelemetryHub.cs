using FmsOnboard.Core;
using Microsoft.AspNetCore.SignalR;

namespace FmsOnboard.Web;

public sealed class TelemetrySnapshotStore
{
    private readonly object _lock = new();
    private int _activeClients;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    public List<Dictionary<string, object?>> Rows { get; private set; } = [];
    public Dictionary<string, object?>? Overall { get; private set; }
    public Dictionary<string, object?>? Collector { get; private set; }

    public int ActiveClients => Volatile.Read(ref _activeClients);

    public int ClientConnected()
    {
        int count = Interlocked.Increment(ref _activeClients);
        if (count == 1 && _wakeSignal.CurrentCount == 0)
            _wakeSignal.Release();
        return count;
    }

    public int ClientDisconnected() => Interlocked.Decrement(ref _activeClients);

    public Task WaitForActiveClientAsync(CancellationToken ct) => _wakeSignal.WaitAsync(ct);

    public void Update(
        List<Dictionary<string, object?>> rows,
        Dictionary<string, object?> overall,
        Dictionary<string, object?> collector)
    {
        lock (_lock)
        {
            Rows = rows;
            Overall = overall;
            Collector = collector;
        }
    }

    public (List<Dictionary<string, object?>> Rows, Dictionary<string, object?>? Overall, Dictionary<string, object?>? Collector) Read()
    {
        lock (_lock) return (Rows, Overall, Collector);
    }
}

public sealed class TelemetryHub(TelemetrySnapshotStore store, ILogger<TelemetryHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        int total = store.ClientConnected();
        logger.LogInformation("[WS] Klien terhubung ({Id}), total aktif: {Total}", Context.ConnectionId, total);

        var (rows, overall, collector) = store.Read();
        if (overall is not null) await Clients.Caller.SendAsync("availability_overall", overall);
        if (collector is not null) await Clients.Caller.SendAsync("collector_status", collector);
        if (rows.Count > 0) await Clients.Caller.SendAsync("database_data", rows);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        store.ClientDisconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
