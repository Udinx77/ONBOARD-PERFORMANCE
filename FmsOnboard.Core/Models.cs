using System.Text.Json.Serialization;

namespace FmsOnboard.Core;

public static class Constants
{
    public const string GpsAvailabilityKey = "SATELLITE";
    public static readonly string[] DeviceTypes = ["R", "D", "P", "G"];
    public static readonly string[] OverallDeviceTypes = ["R", "D", "P"];
}

public sealed record PingStat(
    string IpAddress,
    int PacketsSent,
    int PacketsReceived,
    double PacketLossPct,
    double? AvgRttMs
);

public sealed class MachineInfo
{
    public string Name { get; set; } = "";
    public string? R { get; set; }
    public string? D { get; set; }
    public string? P { get; set; }
    public string? G { get; set; }

    public string? GetIp(string deviceType) => deviceType switch
    {
        "R" => R,
        "D" => D,
        "P" => P,
        "G" => G,
        _ => null
    };
}

public sealed record MachineTarget(
    long MachineOid,
    string MachineName,
    string DeviceType,
    string IpAddress
) : IComparable<MachineTarget>
{
    public int CompareTo(MachineTarget? other)
    {
        if (other is null) return 1;
        int c = MachineOid.CompareTo(other.MachineOid);
        if (c != 0) return c;
        c = string.CompareOrdinal(MachineName, other.MachineName);
        if (c != 0) return c;
        c = string.CompareOrdinal(DeviceType, other.DeviceType);
        return c != 0 ? c : string.CompareOrdinal(IpAddress, other.IpAddress);
    }
}

public sealed class ScanRun
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("started_at")] public double StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public double? FinishedAt { get; set; }
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; set; }
    [JsonPropertyName("target_count")] public long TargetCount { get; set; }
    [JsonPropertyName("checked_count")] public long CheckedCount { get; set; }
    [JsonPropertyName("scan_status")] public string ScanStatus { get; set; } = "";
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
}

public sealed class DeviceStatusRow
{
    public long MachineOid { get; set; }
    public string? MachineName { get; set; }
    public string DeviceType { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsAvailable { get; set; }
    public double? FailSince { get; set; }
    public double LastCheckedAt { get; set; }
    public long PacketsSent { get; set; }
    public long PacketsReceived { get; set; }
    public double PacketLossPct { get; set; }
    public double? AvgRttMs { get; set; }
}

public sealed record DailyAvailability(
    double UptimeSeconds,
    double DowntimeSeconds,
    double ObservedSeconds,
    double? AvailabilityPct
);
