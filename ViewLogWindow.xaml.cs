using System.Windows;
using NetworkMonitor.Models;
using NetworkMonitor.Services;

namespace NetworkMonitor;

public partial class ViewLogWindow : Window
{
    private readonly DeviceRepository repository;
    public ViewLogWindow(DeviceRepository repository)
    {
        InitializeComponent(); this.repository = repository; RefreshLog();
    }
    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshLog();
    private void RefreshLog()
    {
        var rows = repository.AllChanges(SearchBox?.Text);
        ChangesGrid.ItemsSource = rows;
        CountText.Text = $"{rows.Count:N0} events";
    }
}
