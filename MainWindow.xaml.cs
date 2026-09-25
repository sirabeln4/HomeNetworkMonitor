using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.IO;
using System.Text.Json;
using NetworkMonitor.Models;
using NetworkMonitor.Services;

namespace NetworkMonitor;

public partial class MainWindow : Window
{
    private readonly INetworkScanner scanner = new NetworkScanner();
    private readonly ObservableCollection<NetworkDevice> devices = new();
    private ICollectionView? devicesView;
    private readonly List<string> managedByOptions = new() { "None", "Unknown", "Google Home", "SmartThings", "Alexa", "Home Assistant", "Ring", "Philips Hue" };
    private readonly DeviceRepository repository = new();
    private readonly TrayNotifier notifier = new();
    private readonly EmailNotifier emailNotifier = new();
    private readonly System.Windows.Threading.DispatcherTimer timer;

    public MainWindow()
    {
        InitializeComponent();
        ManagedByBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler((sender, args) => EditorChanged(sender, args)));
        ManagedByBox.AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler((sender, args) => UpdateAddServiceState()));
        var optionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeNetworkMonitor", "managed-by-options.json");
        if (File.Exists(optionsPath)) managedByOptions.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(optionsPath))?.Except(managedByOptions) ?? []);
        ManagedByBox.ItemsSource = managedByOptions;
        DevicesGrid.ItemsSource = devices;
        var view = CollectionViewSource.GetDefaultView(devices);
        devicesView = view;
        view.SortDescriptions.Add(new SortDescription(nameof(NetworkDevice.DeviceType), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(NetworkDevice.DisplayName), ListSortDirection.Ascending));
        view.Filter = item => ShowMulticastCheckBox.IsChecked == true || !IsMulticast((item as NetworkDevice)?.IpAddress);
        DevicesGrid.SelectionChanged += (_, _) => LoadSelectedDetails();
        foreach (var device in repository.LoadAll()) devices.Add(device);
        UpdateCounts();
        timer = new() { Interval = TimeSpan.FromMinutes(15) };
        timer.Tick += async (_, _) => await RunScanAsync(false);
        timer.Start();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await RunScanAsync(true);

    private void LoadSelectedDetails()
    {
        if (DevicesGrid.SelectedItem is not NetworkDevice d)
        {
            NameBox.Clear(); TypeBox.Clear(); OwnerBox.Clear(); NotesBox.Clear(); ManagedByBox.Text = "";
            SaveButton.IsEnabled = false;
            return;
        }
        NameBox.Text = d.DisplayName; TypeBox.Text = d.DeviceType; OwnerBox.Text = d.Owner; NotesBox.Text = d.Notes; ManagedByBox.Text = d.ManagedBy;
        IdentityModeBox.SelectedItem = IdentityModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == d.IdentityMode) ?? IdentityModeBox.Items[0];
        SaveButton.IsEnabled = false;
    }

    private void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not NetworkDevice d) return;
        try
        {
            d.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unknown device" : NameBox.Text.Trim(); d.DeviceType = TypeBox.Text.Trim(); d.Owner = OwnerBox.Text.Trim(); d.Notes = NotesBox.Text.Trim(); d.ManagedBy = ManagedByBox.Text.Trim(); d.IdentityMode = (IdentityModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Automatic"; d.IsNew = false;
            repository.Save(devices); DevicesGrid.Items.Refresh(); UpdateCounts(); StatusText.Text = "Device details saved.";
            SaveButton.IsEnabled = false;
        }
        catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UI] Save failed: {ex}"); StatusText.Text = $"Save failed: {ex.Message}"; }
    }

    private void EditorChanged(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not NetworkDevice d) { SaveButton.IsEnabled = false; return; }
        var mode = (IdentityModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Automatic";
        SaveButton.IsEnabled = d.DisplayName != (string.IsNullOrWhiteSpace(NameBox.Text) ? "Unknown device" : NameBox.Text.Trim()) || d.DeviceType != TypeBox.Text.Trim() || d.ManagedBy != ManagedByBox.Text.Trim() || d.Owner != OwnerBox.Text.Trim() || d.Notes != NotesBox.Text.Trim() || d.IdentityMode != mode;
    }

    private void AddManagedBy_Click(object sender, RoutedEventArgs e)
    {
        var value = ManagedByBox.Text.Trim(); if (string.IsNullOrWhiteSpace(value)) return;
        if (!managedByOptions.Contains(value, StringComparer.OrdinalIgnoreCase)) managedByOptions.Add(value);
        ManagedByBox.Items.Refresh(); ManagedByBox.Text = value; UpdateAddServiceState(); var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeNetworkMonitor", "managed-by-options.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(managedByOptions)); StatusText.Text = $"Added managed-by service: {value}";
    }

    private void UpdateAddServiceState() => AddServiceButton.IsEnabled = !string.IsNullOrWhiteSpace(ManagedByBox.Text) && !managedByOptions.Contains(ManagedByBox.Text.Trim(), StringComparer.OrdinalIgnoreCase);

    private async Task RunScanAsync(bool showStatus)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UI] Scan requested.");
        ScanButton.IsEnabled = false; StatusText.Text = "Preparing scan…";
        await Task.Yield();
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var results = await Task.Run(async () => await scanner.ScanAsync(progress));
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UI] Scanner returned {results.Count} device(s).");
            var changes = repository.RecordScan(results);
            DevicesGrid.SelectedItem = null;
            devices.Clear(); foreach (var device in repository.LoadAll()) devices.Add(device);
            UpdateCounts();
            RecentChangesText.Text = string.Join(Environment.NewLine, repository.RecentChanges().Select(c => $"{c.OccurredUtc.ToLocalTime():g}  {c.Description}"));
            foreach (var change in changes.Where(c => c.Description.StartsWith("First seen")).Take(5))
                notifier.Show("Network change detected", change.Description);
            try { await emailNotifier.SendAsync(changes); } catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Email] {ex.Message}"); }
            StatusText.Text = $"Last scan: {DateTime.Now:g}";
        }
        catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UI] Scan failed: {ex}"); StatusText.Text = $"Scan failed: {ex.Message}"; }
        finally { ScanButton.IsEnabled = true; }
    }

    protected override void OnClosed(EventArgs e) { notifier.Dispose(); base.OnClosed(e); }

    private void UpdateCounts()
    {
        TotalText.Text = devices.Count.ToString(); OnlineText.Text = devices.Count(d => d.IsOnline).ToString();
        OfflineText.Text = devices.Count(d => !d.IsOnline).ToString(); UnknownText.Text = devices.Count(d => d.IsNew || d.DisplayName == "Unknown device").ToString();
    }

    private void DisplayFilterChanged(object sender, RoutedEventArgs e) => devicesView?.Refresh();

    private void AdvancedSort_Click(object sender, RoutedEventArgs e) => SortPanel.Visibility = SortPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void ApplyAdvancedSort_Click(object sender, RoutedEventArgs e)
    {
        if (devicesView is null) return;
        var propertyMap = new Dictionary<string, string> { ["Type"] = nameof(NetworkDevice.DeviceType), ["Name"] = nameof(NetworkDevice.DisplayName), ["Managed by"] = nameof(NetworkDevice.ManagedBy), ["IP address"] = nameof(NetworkDevice.IpAddress), ["Manufacturer"] = nameof(NetworkDevice.Manufacturer), ["First seen"] = nameof(NetworkDevice.FirstSeenUtc), ["Last seen"] = nameof(NetworkDevice.LastSeenUtc) };
        devicesView.SortDescriptions.Clear(); AddSort(propertyMap, PrimarySortBox, PrimaryDirectionBox); AddSort(propertyMap, SecondarySortBox, SecondaryDirectionBox); AddSort(propertyMap, ThirdSortBox, ThirdDirectionBox); devicesView.Refresh();
    }

    private void AddSort(IReadOnlyDictionary<string, string> map, System.Windows.Controls.ComboBox column, System.Windows.Controls.ComboBox direction)
    {
        var name = (column.SelectedItem as ComboBoxItem)?.Content?.ToString(); if (name is null || !map.TryGetValue(name, out var property)) return;
        devicesView!.SortDescriptions.Add(new SortDescription(property, (direction.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Descending" ? ListSortDirection.Descending : ListSortDirection.Ascending));
    }

    private static bool IsMulticast(string? address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;
        var first = ip.GetAddressBytes()[0];
        return first >= 224 && first <= 239;
    }
}
