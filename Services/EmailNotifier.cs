using System.IO;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using NetworkMonitor.Models;

namespace NetworkMonitor.Services;

public sealed class EmailNotifier
{
    private readonly string settingsPath;
    public EmailNotifier()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeNetworkMonitor");
        Directory.CreateDirectory(folder); settingsPath = Path.Combine(folder, "email-settings.json");
        if (!File.Exists(settingsPath)) File.WriteAllText(settingsPath, JsonSerializer.Serialize(new EmailSettings(), new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task SendAsync(IEnumerable<DeviceChange> changes)
    {
        var settings = JsonSerializer.Deserialize<EmailSettings>(await File.ReadAllTextAsync(settingsPath)) ?? new();
        var relevant = changes.Where(c => c.Description.StartsWith("First seen")).ToList();
        if (!settings.Enabled || relevant.Count == 0) return;
        using var message = new MailMessage(settings.From, settings.To) { Subject = "Home Network Monitor alert", Body = string.Join(Environment.NewLine, relevant.Select(c => $"{c.OccurredUtc.ToLocalTime():g}: {c.Description}")) };
        using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort) { EnableSsl = settings.EnableSsl, Credentials = new NetworkCredential(settings.Username, settings.Password) };
        await client.SendMailAsync(message);
    }

    private sealed class EmailSettings
    {
        public bool Enabled { get; set; }
        public string SmtpHost { get; set; } = "smtp.example.com";
        public int SmtpPort { get; set; } = 587;
        public bool EnableSsl { get; set; } = true;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }
}
