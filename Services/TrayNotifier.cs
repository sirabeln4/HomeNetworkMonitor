using Forms = System.Windows.Forms;

namespace NetworkMonitor.Services;

public sealed class TrayNotifier : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    public TrayNotifier()
    {
        icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Visible = true, Text = "Home Network Monitor" };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown()); icon.ContextMenuStrip = menu;
    }
    public void Show(string title, string message) => icon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
    public void Dispose() { icon.Visible = false; icon.Dispose(); }
}
