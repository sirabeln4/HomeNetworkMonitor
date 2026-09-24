namespace NetworkMonitor.Models;
public sealed class DeviceChange { public DateTime OccurredUtc { get; init; } public string DeviceKey { get; init; } = ""; public string Description { get; init; } = ""; }
