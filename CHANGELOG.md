# Changelog

All notable changes to this project are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions use [SemVer](https://semver.org/).

## [Unreleased]

### Fixed
- **Start with Windows** now actually starts the app after a reboot. It used the HKCU Run key,
  which Windows silently skips for apps that require administrator. The setting is now a Task
  Scheduler logon task that runs with highest privileges (no UAC prompt, works on battery, no
  72 h time limit). An existing Run entry is moved to the task automatically on next launch, and
  the task is repointed if the exe moved.

## [0.4.0] — 2026-11-17

### Added
- **Steam "Scan & fix"** (Unblock → Quick): finds blocked Steam servers the presets don't list yet —
  from the Windows DNS cache (names answered with a bogus address), Steam's own logs and the live
  store/community pages — diagnoses them and lets the user tick which to unblock. Strictly Steam-only:
  every candidate must pass the `SteamDomains` allowlist, so other sites are never touched. Findings
  are logged with their source so they can be added to the presets.

## [0.3.1] — 2026-11-17

### Added
- **Version + "Check for updates"** at the bottom of the sidebar (and the version in the window
  title). A manual check always reports its result — "You're up to date.", the new version, or the
  error — while the startup / 24 h background checks stay silent.

## [0.3.0] — 2026-11-17

### Added
- **Automatic updates**: on launch (and every 24 h) the app asks GitHub for the latest stable
  release. When a newer one exists the sidebar shows an *Update & restart* card; nothing installs
  without a click. The new exe is downloaded next to the current one, checked against the release's
  `LeaveMeAlone.exe.sha256`, swapped in (running exe renamed to `.old`, rolled back on failure),
  and started only after the old instance has released its relays and hosts lines. Drafts,
  pre-releases and `nightly` are never offered; `dotnet run` builds never update themselves.

### Fixed
- **Steam store loaded without styles/scripts** (`jQuery is not defined`, TLS resets in `cef_log`):
  Steam now serves assets from `*.fastly.steamstatic.com`, which the preset did not list. The
  Steam preset now covers all three static CDNs (akamai / fastly / cloudflare) plus chat, s.team,
  workshop (`*.steamusercontent.com`), legacy `*-a.akamaihd.net` and valvesoftware hosts — 35
  hosts the ISP was poisoning. Steam downloads gains the `client-update.*` hosts.

### Added
- **New UI**: one window, sidebar with **Block** and **Unblock** as equal pages (each shows its live
  status), every page split into **Quick** (one switch per launcher / Steam preset) and **Custom**
  (full bundle editor / check-and-unblock any domain). Quick block creates the launcher's bundle on
  first use; "Customize" jumps to it. New palette (Ember for Block, Signal teal for Unblock), dark
  caption bar, slim scrollbars, animated switches.
- **App icon** (`Assets/app.ico` + `app.png`, built by `tools/make_icon.ps1`) for the exe, window,
  sidebar and tray — previously the tray fell back to a stock shield and nothing else had one.
- **Logs** page + `AppLog`: actions, results, errors (with exception details) and every system
  command, in memory and in daily files under `%APPDATA%\LeaveMeAlone\logs`; problems filter, copy,
  open folder. Unhandled exceptions are logged.
- **Unblock domains** (no VPN): diagnose a domain (normal / DNS poisoning / SNI filtering / other),
  rank its real IPs by speed, and unblock it — hosts-file pin for DNS blocking, local loopback
  relay that splits the TLS ClientHello into two records for SNI filtering. Removal restores the
  hosts file byte-for-byte; relays stop and their hosts lines are removed on exit. "Quick: Steam"
  preset. Verified live against a Vietnamese ISP (direct: reset; via relay: HTTP 200).
- `Services/Unblock/*`, `UnblockViewModels.cs`, the unblock UI, and tests
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
