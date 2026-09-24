using System.Net;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Text.Json;
using System.IO;
using NetworkMonitor.Models;

namespace NetworkMonitor.Services;

public interface INetworkScanner
{
    Task<IReadOnlyList<NetworkDevice>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class NetworkScanner : INetworkScanner
{
    private static readonly Lazy<Dictionary<string, string>> OuiDatabase = new(LoadOuiDatabase);
    public async Task<IReadOnlyList<NetworkDevice>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Selecting active Wi-Fi adapter…");
        Log("Starting network scan.");
        var local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        var gateway = local?.GetIPProperties().GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
        var address = local?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
        Log($"Adapter: {local?.Name ?? "none"}; local IP: {address}; gateway: {gateway}");
        if (address is null || gateway is null)
        {
            Log("No usable IPv4 address and gateway were found.");
            return Array.Empty<NetworkDevice>();
        }

        var prefix = string.Join('.', address.GetAddressBytes().Take(3));
        var found = new List<NetworkDevice>();
        var jobs = Enumerable.Range(1, 254).Select(async i =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(IPAddress.Parse($"{prefix}.{i}"), 500);
                if (reply.Status == IPStatus.Success)
                {
                    var ip = reply.Address.ToString();
                    lock (found) found.Add(new NetworkDevice { IpAddress = ip, Hostname = TryResolveName(reply.Address), DisplayName = ip, IsOnline = true });
                }
            }
            catch (PingException) { }
        });
        await Task.WhenAll(jobs);
        progress?.Report($"Ping discovery complete: {found.Count} device(s). Probing services…");
        Log($"Ping phase found {found.Count} device(s).");

        var candidates = Enumerable.Range(1, 254)
            .Select(i => IPAddress.Parse($"{prefix}.{i}"))
            .Where(ip => !found.Any(d => d.IpAddress == ip.ToString()))
            .ToList();
        var tcpJobs = candidates.Select(ip => ProbeTcpAsync(ip, cancellationToken));
        var tcpResults = await Task.WhenAll(tcpJobs);
        foreach (var result in tcpResults.Where(r => r is not null).Select(r => r!))
            found.Add(result);
        Log($"TCP phase added {tcpResults.Count(r => r is not null)} device(s).");
        progress?.Report("Reading Windows ARP table and matching MAC addresses…");

        // Windows maintains an ARP cache for devices the laptop has recently
        // communicated with, including devices that do not answer ICMP ping.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (process is not null)
            {
                Log("Running: arp -a");
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                Log($"ARP command exit code: {process.ExitCode}; output length: {output.Length}.");
                var arpBefore = found.Count;
                var pattern = new Regex(@"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F-]{17})\s+(dynamic|static)", RegexOptions.Multiline);
                foreach (Match match in pattern.Matches(output))
                {
                    var ip = match.Groups[1].Value;
                    if (ip == "255.255.255.255" || ip == "224.0.0.1" || ip == $"{prefix}.255") continue;
                    var mac = match.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
                    var existing = found.FirstOrDefault(d => d.IpAddress == ip);
                    if (existing is not null) { existing.MacAddress = mac; existing.Manufacturer = ManufacturerFor(mac); }
                    else
                        found.Add(new NetworkDevice { IpAddress = ip, MacAddress = mac, Manufacturer = ManufacturerFor(mac), DisplayName = ip, IsOnline = true });
                }
                Log($"ARP phase added {found.Count - arpBefore} device(s).");
            }
        }
        catch (Exception ex) { Log($"ARP phase failed: {ex.GetType().Name}: {ex.Message}"); }

        // The default gateway should always be represented even if it hides ICMP.
        if (!found.Any(d => d.IpAddress == gateway.ToString()))
            found.Add(new NetworkDevice { IpAddress = gateway.ToString(), DisplayName = "Network gateway", IsOnline = true });
        Log($"Scan complete: {found.Count} device(s).");
        progress?.Report($"Scan complete: {found.Count} device(s) found.");
        found = found.Where(d => IsUsableHostAddress(d.IpAddress)).ToList();
        return found.OrderBy(d => d.IpAddress).ToList();
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Scanner] {message}");

    private static async Task<NetworkDevice?> ProbeTcpAsync(IPAddress ip, CancellationToken cancellationToken)
    {
        var ports = new[] { (80, "HTTP"), (443, "HTTPS"), (22, "SSH"), (53, "DNS"), (445, "SMB"), (3389, "RDP") };
        var open = new List<string>();
        foreach (var (port, name) in ports)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(180);
                await client.ConnectAsync(ip, port, timeout.Token);
                open.Add($"{name} ({port})");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        if (open.Count == 0) return null;
        return new NetworkDevice { IpAddress = ip.ToString(), Hostname = TryResolveName(ip), DisplayName = ip.ToString(), IsOnline = true, DetectedServices = string.Join(", ", open) };
    }

    private static string TryResolveName(IPAddress ip)
    {
        try { return Dns.GetHostEntry(ip).HostName; } catch (SocketException) { return ""; }
    }

    private static bool IsUsableHostAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = ip.GetAddressBytes();
        return bytes[3] != 255 && bytes[0] < 224;
    }

    private static string ManufacturerFor(string mac)
    {
        var prefix = mac.Replace(":", "").Replace("-", "")[..6].ToUpperInvariant();
        return OuiDatabase.Value.TryGetValue(prefix, out var name) ? name : "Unknown OUI";
    }

    private static Dictionary<string, string> LoadOuiDatabase()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "ouidb.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var prefix = entry.GetProperty("prefix").GetString();
                var name = entry.GetProperty("organization").GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(name)) result[prefix!] = name!;
            }
            Log($"Loaded {result.Count:N0} OUI manufacturer entries.");
        }
        catch (Exception ex) { Log($"OUI database unavailable: {ex.Message}"); }
        return result;
    }
}
