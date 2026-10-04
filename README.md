# LeaveMeAlone

*Tell your ISP — and your apps — to leave you alone.*

A Windows desktop app (WPF, .NET 8) with two jobs:

1. **Unblock domains** your ISP filters (DNS poisoning / SNI filtering) — without a VPN, only for
   the domains you pick, traffic still going straight to the real server.
2. **Firewall bundles** — group Windows Firewall rules into named bundles and toggle each with one
   switch (e.g. force a game launcher into offline mode).

> Formerly *Firewall Bundle Blocker*. On first launch the app moves its saved data from
> `%APPDATA%\FirewallBundleBlocker` to `%APPDATA%\LeaveMeAlone` and updates the "Start with
> Windows" entry. Firewall rules (`FBB::…`) and the hosts-file block keep their original tags so
> rules and blocks written by older versions are still recognized.

## Download

No install and no .NET needed — download one file and run it (accept the administrator prompt):

- **Latest build of `main`:** [LeaveMeAlone.exe (nightly)](https://github.com/lippo999/LeaveMeAlone/releases/download/nightly/LeaveMeAlone.exe)
- **Latest stable release:** [LeaveMeAlone.exe](https://github.com/lippo999/LeaveMeAlone/releases/latest/download/LeaveMeAlone.exe)
  (available once a `v*` tag has been released)

Each release also has `LeaveMeAlone.exe.sha256`. Builds come from
[.github/workflows/build.yml](.github/workflows/build.yml): every push to `main` runs the tests and
refreshes *nightly*; pushing a tag such as `v0.2.0` publishes a versioned release.

Windows SmartScreen may warn because the exe isn't code-signed: *More info → Run anyway*.

## Unblock domains (ISP filtering, no VPN)

Some ISPs (e.g. in Vietnam) block sites such as `store.steampowered.com` by **DNS poisoning**
(their DNS answers `127.0.0.1`) and/or **SNI filtering** (DPI resets the TLS connection when it
sees the domain name in the ClientHello). The server itself stays reachable. **Unblock domains**
(header button) works around both for the domains you choose — nothing else is affected.
Full design notes: [docs/domain-unblock.md](docs/domain-unblock.md).

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
- Saved in `%APPDATA%\LeaveMeAlone\unblocked.json`; the previous hosts file is copied to
  `hosts.backup` there before every write.
- Caveats: a browser set to its *own* DNS-over-HTTPS in strict mode (Firefox TRR mode 3) ignores
  the hosts file; relayed traffic leaves through this app, so a bundle blocking a launcher's
  program doesn't block that launcher's relayed domains.

## Firewall bundles

- A **bundle** is a user-created group of block targets.
- A **target** is either:
  - a **program** — blocks that executable's outbound traffic (a launcher then drops into offline
    mode while its games still launch), or
  - a **remote IP / CIDR** — blocks a specific server address (optionally a port + protocol).
- Toggling a bundle enables/disables all of its firewall rules at once.
- Every rule this app creates is tagged `FBB::<bundleId>::<targetId>` and grouped per bundle, so
  the app only ever touches its own rules — never your existing firewall or system rules.
- Bundles are saved to `%APPDATA%\LeaveMeAlone\bundles.json`.

## Requirements

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) — `winget install Microsoft.DotNet.SDK.8`
- Administrator rights (the app requests elevation on launch; firewall changes require it)

## Build & run (on Windows)

```powershell
dotnet build
dotnet run --project src\LeaveMeAlone   # UAC prompt appears on launch
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
src/LeaveMeAlone/
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
tests/LeaveMeAlone.Tests/
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
