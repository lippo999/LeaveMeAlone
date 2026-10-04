# Firewall Bundle Blocker

A Windows desktop app (WPF, .NET 8) that groups Windows Firewall rules into **named bundles**
you can toggle on and off with a single switch — much simpler than the built-in firewall UI.

Originally built for gaming (force Steam / Ubisoft Connect into offline mode), but the app is
fully generic: **you** create bundles and choose what each one blocks.

## How it works

- A **bundle** is a user-created group of block targets.
- A **target** is either:
  - a **program** — blocks that executable's outbound traffic (a launcher then drops into offline
    mode while its games still launch), or
  - a **remote IP / CIDR** — blocks a specific server address (optionally a port + protocol).
- Toggling a bundle enables/disables all of its firewall rules at once.
- Every rule this app creates is tagged `FBB::<bundleId>::<targetId>` and grouped per bundle, so
  the app only ever touches its own rules — never your existing firewall or system rules.
- Bundles are saved to `%APPDATA%\FirewallBundleBlocker\bundles.json`.

## Unblock domains (ISP filtering, no VPN)

Some ISPs (e.g. in Vietnam) block sites such as `store.steampowered.com` by **DNS poisoning**
(their DNS answers `127.0.0.1`) and/or **SNI filtering** (DPI resets the TLS connection when it
sees the domain name in the ClientHello). The server itself stays reachable. **Unblock domains**
(header button) works around both for the domains you choose — nothing else is affected:

| Detected | What the app does |
|---|---|
| DNS only | Pins the domain's real, verified-fastest IPs in a marked block of the hosts file. Works with the app closed. |
| SNI (± DNS) | Points the domain at a private loopback address (`127.77.0.x`) where the app relays traffic to the real IP, splitting the TLS ClientHello into two TLS records so DPI can't read the name. TLS stays end-to-end (no certificate tricks); runs while the app is open. |
| Other (IP blocked, TLS blocked even split) | Reported only; nothing is changed. |

- **Diagnose** compares the system DNS answer with DNS-over-HTTPS (`1.1.1.1`, `8.8.8.8` by IP — only
  the domain name is sent), ranks the real IPs by connect time and tests TLS with/without splitting.
- **Remove / Remove all** deletes the app's hosts lines and stops relays; the hosts file is restored
  byte-for-byte. On exit, relay lines are removed and re-added on next launch.
- **Quick: Steam** diagnoses the Steam store/community domains and unblocks only the blocked ones.
- Saved in `%APPDATA%\FirewallBundleBlocker\unblocked.json`; the previous hosts file is copied to
  `hosts.backup` there before every write.
- Caveats: a browser set to its *own* DNS-over-HTTPS in strict mode (Firefox TRR mode 3) ignores
  the hosts file; relayed traffic leaves through this app, so a bundle blocking a launcher's
  program doesn't block that launcher's relayed domains.

## Requirements

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) — `winget install Microsoft.DotNet.SDK.8`
- Administrator rights (the app requests elevation on launch; firewall changes require it)

## Build & run (on Windows)

```powershell
dotnet build
dotnet run --project src\FirewallBundleBlocker   # UAC prompt appears on launch
```

> WPF only builds and runs on Windows. If you edit the source on Linux/WSL, copy the folder to a
> Windows drive first (e.g. `robocopy \\wsl$\Ubuntu\home\<you>\projects\online-mode-blocker C:\dev\online-mode-blocker /E`).

## Test

```powershell
dotnet test
```

Unit tests cover the domain model (immutability), JSON persistence (round-trip, missing/corrupt
files, enums-as-strings), and the `BundleManager` orchestration + firewall reconciliation using a
fake firewall service.

## Project layout

```
src/FirewallBundleBlocker/
  Models.cs                     Bundle, FirewallTarget, TargetType (immutable records)
  Services/
    Firewall.cs                 IFirewallService + PowerShell (NetSecurity) implementation
    BundleStore.cs              JSON persistence (%APPDATA%)
    BundleManager.cs            store + firewall coordination and startup reconciliation
    CommonLaunchers.cs          quick-add presets for common game launchers
    Platform.cs                 elevation helpers
  ViewModels.cs                 MVVM view-models
  Views/
    MainWindow.xaml(.cs)        main UI (bundle list + toggle + detail)
    Dialogs.cs                  small modal dialogs (name / IP / launcher picker)
  App.xaml(.cs)                 theme + composition root
  app.manifest                  requestedExecutionLevel = requireAdministrator
tests/FirewallBundleBlocker.Tests/
```

## Extras

- **System tray:** minimizing hides the window to the tray. Right-click the tray icon to toggle any
  bundle on/off without opening the app; double-click (or "Show window") restores it; "Exit" quits.
- **Start with Windows:** the header checkbox registers/unregisters the app in the per-user
  `...\CurrentVersion\Run` key.
- **Import / Export:** save all bundles to a JSON file and load them on another machine. Imported
  bundles get fresh IDs, so they never collide with existing rules.

## Notes & limits

- Blocking a launcher's process also blocks any game multiplayer that routes through that process.
  Add only the executables you want offline; use per-executable granularity as needed.
- Firewall rules cannot match on domain names — this app blocks by program path or by IP/CIDR.
- v1 blocks **outbound** traffic (the right choice for forcing launchers offline).
