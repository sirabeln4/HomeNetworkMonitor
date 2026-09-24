using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using NetworkMonitor.Models;
using NetworkMonitor.Services;

namespace NetworkMonitor;

public partial class MainWindow : Window
{
    private readonly INetworkScanner scanner = new NetworkScanner();
    private readonly ObservableCollection<NetworkDevice> devices = new();
    private ICollectionView? devicesView;
    private readonly DeviceRepository repository = new();
    private readonly TrayNotifier notifier = new();
    private readonly EmailNotifier emailNotifier = new();
    private readonly System.Windows.Threading.DispatcherTimer timer;

    public MainWindow()
    {
        InitializeComponent();
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
            NameBox.Clear(); TypeBox.Clear(); OwnerBox.Clear(); NotesBox.Clear();
            return;
        }
        NameBox.Text = d.DisplayName; TypeBox.Text = d.DeviceType; OwnerBox.Text = d.Owner; NotesBox.Text = d.Notes;
        IdentityModeBox.SelectedItem = IdentityModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == d.IdentityMode) ?? IdentityModeBox.Items[0];
    }

    private void SaveDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not NetworkDevice d) return;
        try
        {
            d.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Unknown device" : NameBox.Text.Trim(); d.DeviceType = TypeBox.Text.Trim(); d.Owner = OwnerBox.Text.Trim(); d.Notes = NotesBox.Text.Trim(); d.IdentityMode = (IdentityModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Automatic"; d.IsNew = false;
            repository.Save(devices); DevicesGrid.Items.Refresh(); UpdateCounts(); StatusText.Text = "Device details saved.";
        }
        catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UI] Save failed: {ex}"); StatusText.Text = $"Save failed: {ex.Message}"; }
    }

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

    private static bool IsMulticast(string? address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var ip)) return false;
        var first = ip.GetAddressBytes()[0];
        return first >= 224 && first <= 239;
    }
}
