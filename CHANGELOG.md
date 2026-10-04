# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions use [SemVer](https://semver.org/).

## [Unreleased]

### Added
- **Unblock domains** (no VPN): diagnose a domain (normal / DNS poisoning / SNI filtering / other),
  rank its real IPs by speed, and unblock it — hosts-file pin for DNS blocking, local loopback
  relay that splits the TLS ClientHello into two records for SNI filtering. Removal restores the
  hosts file byte-for-byte; relays stop and their hosts lines are removed on exit. "Quick: Steam"
  preset. Verified live against a Vietnamese ISP (direct: reset; via relay: HTTP 200).
- `Services/Unblock/*`, `UnblockViewModels.cs`, `Views/UnblockWindow.xaml(.cs)`, and tests
  (DNS wire parsing, bogon detection, DoH JSON, fragmenter, hosts block, diagnoser, manager,
  and loopback TLS integration tests for the relay).

### Changed
- **Renamed to LeaveMeAlone** (solution, projects, namespaces, executable, UI). Domain unblocking
  is now the main feature. Saved data moves from `%APPDATA%\FirewallBundleBlocker` to
  `%APPDATA%\LeaveMeAlone` and the "Start with Windows" entry is migrated on first launch; the
  `FBB::` rule tag and hosts-file markers are unchanged for compatibility.
- Shared `JsonFile` helper (atomic save, corrupt-file backup) now backs both JSON stores.
- `GuardedViewModel` base extracted from `MainViewModel` (status/busy/error guard).

### Fixed
- First Windows build: WinForms implicit global usings made WPF types ambiguous; firewall
  `RunScriptAsync` didn't return its result to `GetLiveRuleDisplayNamesAsync`.

### Deferred / planned
- Per-target inbound/outbound direction (currently outbound-only).
- Single-instance guard; optional close-to-tray behavior.

## [0.1.0] — initial scaffold

First end-to-end implementation. Authored on WSL; not yet compiled on Windows (WPF requires a
Windows build).

### Added
- **Core domain:** immutable `Bundle` / `FirewallTarget` records and `TargetType`
  (Program / RemoteAddress).
- **Firewall layer:** `IFirewallService` with a PowerShell (NetSecurity) implementation; rules
  tagged `FBB::<bundleId>::<targetId>` and grouped per bundle so the app only touches its own
  rules. `IProcessRunner` shell seam.
- **Persistence:** `JsonBundleStore` at `%APPDATA%\LeaveMeAlone\bundles.json` — atomic
  writes, enums as strings, corrupt-file backup.
- **Orchestration:** `BundleManager` — create/rename/delete bundles, add/remove targets, toggle,
  startup reconciliation (self-heals missing rules), import/export.
- **UI:** WPF MVVM — bundle list with one-click toggle switches, detail pane, add-program /
  add-IP / add-common-launcher dialogs, elevation banner, status bar; deliberate dark-slate theme.
- **Common launchers:** quick-add presets (Steam, Ubisoft, Epic, EA, Battle.net, GOG, Riot,
  Rockstar) detected by default install path.
- **System tray:** `NotifyIcon` quick-toggle menu + minimize-to-tray.
- **Start with Windows:** per-user Run-key registration toggle.
- **Import / Export:** save/load bundles as JSON; imports get fresh IDs to avoid collisions.
- **Elevation:** `app.manifest` requires administrator; self-relaunch via UAC.
- **Tests:** xUnit suite for domain immutability, JSON persistence, and `BundleManager`
  (against a `FakeFirewallService`).
