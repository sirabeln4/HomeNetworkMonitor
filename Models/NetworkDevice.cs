namespace NetworkMonitor.Models;

public sealed class NetworkDevice
{
    public string MacAddress { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Manufacturer { get; set; } = "Unknown";
    public string DisplayName { get; set; } = "Unknown device";
    public string DeviceType { get; set; } = "Unknown";
    public string Owner { get; set; } = "";
    public bool IsOnline { get; set; }
    public bool IsNew { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = "";
    public string DetectedServices { get; set; } = "";
    public string IdentityMode { get; set; } = "Automatic";
}
