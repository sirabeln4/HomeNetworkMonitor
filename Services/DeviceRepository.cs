using System.IO;
using Microsoft.Data.Sqlite;
using NetworkMonitor.Models;

namespace NetworkMonitor.Services;

public sealed class DeviceRepository
{
    private readonly string connectionString;
    private readonly Dictionary<string, int> offlineMisses = new();
    private const int OfflineScanThreshold = 3;

    public DeviceRepository()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeNetworkMonitor");
        Directory.CreateDirectory(folder);
        connectionString = $"Data Source={Path.Combine(folder, "network-monitor.db")}";
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Devices (
              DeviceKey TEXT PRIMARY KEY, MacAddress TEXT NOT NULL, IpAddress TEXT NOT NULL,
              Hostname TEXT NOT NULL, Manufacturer TEXT NOT NULL, DisplayName TEXT NOT NULL,
              DeviceType TEXT NOT NULL, Owner TEXT NOT NULL DEFAULT '', IsOnline INTEGER NOT NULL, IsNew INTEGER NOT NULL,
              IdentityMode TEXT NOT NULL DEFAULT 'Automatic',
              ManagedBy TEXT NOT NULL DEFAULT '',
              FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, Notes TEXT NOT NULL,
              DetectedServices TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        try { using var migrate = connection.CreateCommand(); migrate.CommandText = "ALTER TABLE Devices ADD COLUMN Owner TEXT NOT NULL DEFAULT ''"; migrate.ExecuteNonQuery(); } catch (SqliteException) { }
        try { using var migrate = connection.CreateCommand(); migrate.CommandText = "ALTER TABLE Devices ADD COLUMN IdentityMode TEXT NOT NULL DEFAULT 'Automatic'"; migrate.ExecuteNonQuery(); } catch (SqliteException) { }
        try { using var migrate = connection.CreateCommand(); migrate.CommandText = "ALTER TABLE Devices ADD COLUMN ManagedBy TEXT NOT NULL DEFAULT ''"; migrate.ExecuteNonQuery(); } catch (SqliteException) { }
        using var history = connection.CreateCommand(); history.CommandText = "CREATE TABLE IF NOT EXISTS ScanHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT, OccurredUtc TEXT NOT NULL, DeviceCount INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS DeviceChanges (Id INTEGER PRIMARY KEY AUTOINCREMENT, OccurredUtc TEXT NOT NULL, DeviceKey TEXT NOT NULL, Description TEXT NOT NULL);"; history.ExecuteNonQuery();
        using var cleanup = connection.CreateCommand(); cleanup.CommandText = "DELETE FROM Devices AS old WHERE old.MacAddress = '' AND EXISTS (SELECT 1 FROM Devices AS newer WHERE newer.IpAddress = old.IpAddress AND newer.MacAddress <> '')"; cleanup.ExecuteNonQuery();
        using var broadcastCleanup = connection.CreateCommand(); broadcastCleanup.CommandText = "DELETE FROM Devices WHERE IpAddress = '255.255.255.255' OR IpAddress LIKE '%.255'"; broadcastCleanup.ExecuteNonQuery();
        using var historyCleanup = connection.CreateCommand(); historyCleanup.CommandText = "DELETE FROM DeviceChanges WHERE Description LIKE 'First seen: 255.255.255.255%' OR Description LIKE 'Services changed: HTTP (80)%'"; historyCleanup.ExecuteNonQuery();
    }

    public List<NetworkDevice> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MacAddress,IpAddress,Hostname,Manufacturer,DisplayName,DeviceType,Owner,IsOnline,IsNew,FirstSeenUtc,LastSeenUtc,Notes,DetectedServices,IdentityMode,ManagedBy FROM Devices ORDER BY DisplayName";
        using var reader = command.ExecuteReader();
        var result = new List<NetworkDevice>();
        while (reader.Read()) result.Add(new NetworkDevice {
            MacAddress = reader.GetString(0), IpAddress = reader.GetString(1), Hostname = reader.GetString(2),
            Manufacturer = reader.GetString(3), DisplayName = reader.GetString(4), DeviceType = reader.GetString(5), Owner = reader.GetString(6),
            IsOnline = reader.GetInt32(7) != 0, IsNew = reader.GetInt32(8) != 0,
            FirstSeenUtc = DateTime.Parse(reader.GetString(9)), LastSeenUtc = DateTime.Parse(reader.GetString(10)),
            Notes = reader.GetString(11), DetectedServices = reader.GetString(12), IdentityMode = reader.GetString(13), ManagedBy = reader.GetString(14)
        });
        return result.Where(d => !d.IpAddress.EndsWith(".255", StringComparison.Ordinal) && d.IpAddress != "255.255.255.255").GroupBy(d => d.IpAddress).Select(group =>
        {
            var preferred = group.OrderByDescending(d => !string.IsNullOrWhiteSpace(d.MacAddress)).First();
            foreach (var duplicate in group.Where(d => !ReferenceEquals(d, preferred)))
            {
                if (string.IsNullOrWhiteSpace(preferred.MacAddress)) preferred.MacAddress = duplicate.MacAddress;
                if (preferred.Manufacturer.StartsWith("Unknown")) preferred.Manufacturer = duplicate.Manufacturer;
                preferred.FirstSeenUtc = preferred.FirstSeenUtc < duplicate.FirstSeenUtc ? preferred.FirstSeenUtc : duplicate.FirstSeenUtc;
                preferred.LastSeenUtc = preferred.LastSeenUtc > duplicate.LastSeenUtc ? preferred.LastSeenUtc : duplicate.LastSeenUtc;
                preferred.IsOnline |= duplicate.IsOnline;
            }
            return preferred;
        }).ToList();
    }

    public void Save(IEnumerable<NetworkDevice> devices)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var device in devices)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Devices(DeviceKey,MacAddress,IpAddress,Hostname,Manufacturer,DisplayName,DeviceType,Owner,IsOnline,IsNew,FirstSeenUtc,LastSeenUtc,Notes,DetectedServices,IdentityMode,ManagedBy)
                VALUES($key,$mac,$ip,$host,$maker,$name,$type,$owner,$online,$new,$first,$last,$notes,$services,$identity,$managedBy)
                ON CONFLICT(DeviceKey) DO UPDATE SET MacAddress=$mac,IpAddress=$ip,Hostname=$host,Manufacturer=$maker,DisplayName=$name,DeviceType=$type,Owner=$owner,IsOnline=$online,IsNew=$new,LastSeenUtc=$last,Notes=$notes,DetectedServices=$services,IdentityMode=$identity,ManagedBy=$managedBy;
                """;
            command.Parameters.AddWithValue("$key", Key(device)); command.Parameters.AddWithValue("$mac", device.MacAddress);
            command.Parameters.AddWithValue("$ip", device.IpAddress); command.Parameters.AddWithValue("$host", device.Hostname);
            command.Parameters.AddWithValue("$maker", device.Manufacturer); command.Parameters.AddWithValue("$name", device.DisplayName);
            command.Parameters.AddWithValue("$type", device.DeviceType); command.Parameters.AddWithValue("$online", device.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("$owner", device.Owner);
            command.Parameters.AddWithValue("$new", device.IsNew ? 1 : 0); command.Parameters.AddWithValue("$first", device.FirstSeenUtc.ToString("O"));
            command.Parameters.AddWithValue("$last", device.LastSeenUtc.ToString("O")); command.Parameters.AddWithValue("$notes", device.Notes);
            command.Parameters.AddWithValue("$services", device.DetectedServices); command.Parameters.AddWithValue("$identity", device.IdentityMode); command.Parameters.AddWithValue("$managedBy", device.ManagedBy); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<DeviceChange> RecordScan(IReadOnlyList<NetworkDevice> scanned)
    {
        var previous = LoadAll().ToDictionary(Key); var now = DateTime.UtcNow; var changes = new List<DeviceChange>();
        foreach (var device in scanned) { var priorIp = previous.Values.FirstOrDefault(old => !string.IsNullOrWhiteSpace(device.MacAddress) && old.MacAddress.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase) && old.IpAddress != device.IpAddress); if (priorIp is not null) { device.DisplayName = priorIp.DisplayName; device.DeviceType = priorIp.DeviceType; device.Owner = priorIp.Owner; device.Notes = priorIp.Notes; device.FirstSeenUtc = priorIp.FirstSeenUtc; device.IdentityMode = priorIp.IdentityMode; device.ManagedBy = priorIp.ManagedBy; changes.Add(new() { OccurredUtc = now, DeviceKey = Key(device), Description = $"Device moved from {priorIp.IpAddress} to {device.IpAddress}" }); previous.Remove(Key(priorIp)); } var sameIpDifferentMac = previous.Values.FirstOrDefault(old => old.IpAddress == device.IpAddress && old.IdentityMode != "Track by IP" && !string.IsNullOrWhiteSpace(old.MacAddress) && !string.IsNullOrWhiteSpace(device.MacAddress) && !old.MacAddress.Equals(device.MacAddress, StringComparison.OrdinalIgnoreCase)); if (sameIpDifferentMac is not null) { sameIpDifferentMac.DisplayName = "Unknown device"; sameIpDifferentMac.DeviceType = "Unknown"; sameIpDifferentMac.Owner = ""; sameIpDifferentMac.Notes = ""; sameIpDifferentMac.IsOnline = false; changes.Add(new() { OccurredUtc = now, DeviceKey = Key(device), Description = $"New device at {device.IpAddress}; previous MAC was {sameIpDifferentMac.MacAddress}" }); device.DisplayName = "Unknown device"; device.DeviceType = "Unknown"; device.Owner = ""; device.Notes = ""; device.IsNew = true; device.FirstSeenUtc = now; device.LastSeenUtc = now; } else { var key = Key(device); device.LastSeenUtc = now; var old = previous.TryGetValue(key, out var exact) ? exact : previous.Values.FirstOrDefault(candidate => candidate.IpAddress == device.IpAddress && candidate.IdentityMode == "Track by IP"); if (old is null) changes.Add(new() { OccurredUtc = now, DeviceKey = key, Description = $"First seen: {device.IpAddress}" }); else { if (!old.IsOnline) changes.Add(new() { OccurredUtc = now, DeviceKey = key, Description = $"Came online: {device.IpAddress}" }); if (old.IpAddress != device.IpAddress) changes.Add(new() { OccurredUtc = now, DeviceKey = key, Description = $"IP changed: {old.IpAddress} -> {device.IpAddress}" }); if (!string.IsNullOrWhiteSpace(old.DetectedServices) && !string.IsNullOrWhiteSpace(device.DetectedServices) && old.DetectedServices != device.DetectedServices) changes.Add(new() { OccurredUtc = now, DeviceKey = key, Description = $"Services changed: {device.DetectedServices}" }); device.DisplayName = old.DisplayName; device.DeviceType = old.DeviceType; device.Owner = old.Owner; device.Notes = old.Notes; device.IdentityMode = old.IdentityMode; device.ManagedBy = old.ManagedBy; device.IsNew = old.IsNew; } } }
        var currentKeys = scanned.Select(Key).ToHashSet();
        foreach (var old in previous.Values)
        {
            var key = Key(old);
            if (currentKeys.Contains(key)) { offlineMisses.Remove(key); continue; }
            var misses = offlineMisses.TryGetValue(key, out var count) ? count + 1 : 1;
            offlineMisses[key] = misses;
            if (old.IsOnline && misses >= OfflineScanThreshold)
            {
                old.IsOnline = false; offlineMisses.Remove(key);
                changes.Add(new() { OccurredUtc = now, DeviceKey = key, Description = $"Went offline: {old.IpAddress}" });
            }
            scanned = scanned.Append(old).ToList();
        }
        Save(scanned); using var connection = Open(); using var tx = connection.BeginTransaction(); using var scan = connection.CreateCommand(); scan.Transaction = tx; scan.CommandText = "INSERT INTO ScanHistory(OccurredUtc,DeviceCount) VALUES($time,$count)"; scan.Parameters.AddWithValue("$time", now.ToString("O")); scan.Parameters.AddWithValue("$count", scanned.Count(d => d.IsOnline)); scan.ExecuteNonQuery();
        foreach (var change in changes) { using var item = connection.CreateCommand(); item.Transaction = tx; item.CommandText = "INSERT INTO DeviceChanges(OccurredUtc,DeviceKey,Description) VALUES($time,$key,$description)"; item.Parameters.AddWithValue("$time", change.OccurredUtc.ToString("O")); item.Parameters.AddWithValue("$key", change.DeviceKey); item.Parameters.AddWithValue("$description", change.Description); item.ExecuteNonQuery(); } tx.Commit(); return changes;
    }

    public List<DeviceChange> RecentChanges(int limit = 8) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT OccurredUtc,DeviceKey,Description FROM DeviceChanges ORDER BY Id DESC LIMIT $limit"; q.Parameters.AddWithValue("$limit", limit); using var r = q.ExecuteReader(); var list = new List<DeviceChange>(); while (r.Read()) list.Add(new() { OccurredUtc = DateTime.Parse(r.GetString(0)), DeviceKey = r.GetString(1), Description = r.GetString(2) }); return list; }

    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static string Key(NetworkDevice d) => string.IsNullOrWhiteSpace(d.MacAddress) ? $"ip:{d.IpAddress}" : $"mac:{d.MacAddress}";
}
