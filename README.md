# Home Network Monitor

Windows/WPF prototype for discovering and monitoring devices on a home network.

## Current prototype

- Manual local IPv4 discovery using ICMP ping plus the Windows ARP table
- Controlled TCP probing of common services (HTTP, HTTPS, SSH, DNS, SMB, and RDP)
- Reverse-DNS hostname resolution when available
- Optional SMTP email alerts configured in `%LOCALAPPDATA%\HomeNetworkMonitor\email-settings.json`
- Dashboard and device table
- Automatic light scan every 15 minutes while the app is running
- Project structure ready for SQLite persistence, change history, notifications, and deeper service scans

## Build

Install the .NET 8 SDK, then run `dotnet build` from this folder. The current machine has .NET runtimes but not the SDK, so compilation could not be verified here.

## Important limitation

Some devices ignore ping and the ARP table only contains devices the laptop has recently encountered. The production scanner should add controlled TCP probing and router integration where possible, while preserving device identity using MAC addresses.

Email is disabled by default. Run the app once, edit `email-settings.json` with your SMTP provider details, set `Enabled` to `true`, and restart the app. Use an app password when your provider supports it.
