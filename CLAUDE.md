# CLAUDE.md — LeaveMeAlone

Project guide for Claude Code. Read this first when working on this repo.

## What this is

**LeaveMeAlone** (formerly *Firewall Bundle Blocker*) is a **Windows desktop app (WPF, .NET 8)**.
Main feature: **unblock domains** the ISP filters (DNS poisoning / SNI filtering) without a VPN —
see `docs/domain-unblock.md`. Secondary feature: group Windows Firewall rules into user-created,
named **bundles** toggled with one switch (e.g. force a game launcher offline).

Legacy identifiers kept on purpose (renaming would orphan data from older installs): firewall
rule tag `FBB::`, hosts-file markers `# >>> FirewallBundleBlocker unblock …`. `App` migrates
`%APPDATA%\FirewallBundleBlocker` → `%APPDATA%\LeaveMeAlone` and the old Run-key value on startup.

- A **bundle** = a named group of block targets, toggled as one unit.
- A **target** = a program (`.exe`, blocks outbound → launcher goes offline) or a remote IP/CIDR
  (optional port + protocol).
- Rules are tagged `FBB::<bundleId>::<targetId>` and grouped per bundle, so the app only ever
  touches its own rules — never the user's existing/system firewall rules.

## CRITICAL constraint: Windows-only

WPF **cannot build or run on Linux/WSL**. Build, run, and test on **Windows** with the .NET 8 SDK.
Do not run `dotnet build` under WSL. If editing from WSL, the source must be copied to a Windows
drive to build (e.g. rsync to `C:\Users\<you>\AppData\Local\Temp\fbb-build`, excluding bin/obj,
then run `/mnt/c/Program Files/dotnet/dotnet.exe build|test` from there via WSL interop).
Requires **administrator** rights at runtime (firewall + hosts changes need elevation; the app
self-elevates via `app.manifest` → `requireAdministrator`).

## Build / run / test (Windows)

```powershell
dotnet build
dotnet test
dotnet run --project src\LeaveMeAlone    # UAC prompt on launch
```
Install SDK if needed: `winget install Microsoft.DotNet.SDK.8`.

## Architecture

- **Composition root:** `App.xaml.cs` — checks elevation, builds `ProcessRunner` →
  `PowerShellFirewallService` → `JsonBundleStore` → `BundleManager` → `MainViewModel` →
  `MainWindow`, then creates the tray icon.
- **`BundleManager`** (Services): the orchestration seam. Coordinates the store + firewall, holds
  the in-memory bundle list, reconciles the firewall to config on startup. **Put business logic
  here** (it's unit-tested against a fake firewall).
- **`IFirewallService`** (Services/Firewall.cs): abstraction over Windows Firewall. Real impl
  `PowerShellFirewallService` shells `powershell.exe` NetSecurity cmdlets
  (`New-/Enable-/Disable-/Remove-NetFirewallRule`). `IProcessRunner` is the injected shell seam.
- **`JsonBundleStore`** (Services): persistence at `%APPDATA%\LeaveMeAlone\bundles.json`
  (atomic write; enums as strings; corrupt file → backed up + empty).
- **UI:** `ViewModels.cs` (MVVM), `Views/MainWindow.xaml(.cs)`, `Views/Dialogs.cs` (code-built
  modals), `Views/TrayIcon.cs` (WinForms `NotifyIcon`). Theme lives in `App.xaml`.
- **`Platform.cs`** (Services): elevation helpers + `StartupRegistration` (HKCU Run key).

## Conventions

- **Immutable domain:** `Bundle` / `FirewallTarget` are `record`s; mutate via `with`, never in
  place. `BundleManager` replaces list entries rather than editing them.
- Async all the way (`Task`), `ConfigureAwait(false)` in services.
- Errors surface to the user via `MainViewModel.StatusMessage` (see `RunGuardedAsync`); services
  throw `FirewallException` on non-zero PowerShell exit.
- Files stay small and feature-organized. Tests use xUnit + a `FakeFirewallService`.
- Follow the user's global rules (80%+ coverage target, no hardcoded secrets, small functions).

## File map

```
src/LeaveMeAlone/
  App.xaml(.cs), app.manifest        theme + composition root + admin manifest
  Models.cs                          Bundle, FirewallTarget, TargetType (records)
  ViewModels.cs                      ObservableObject, RelayCommand, *ViewModel, MainViewModel
  Services/Firewall.cs               IFirewallService + PowerShell impl + IProcessRunner
  Services/BundleStore.cs            IBundleStore + JsonBundleStore
  Services/BundleManager.cs          orchestration + reconciliation + import/export
  Services/CommonLaunchers.cs        quick-add launcher presets (detected by install path)
  Services/Platform.cs               elevation + StartupRegistration
  Services/JsonFile.cs               shared atomic JSON load/save
  Services/Unblock/                  domain unblocking (ISP DNS poisoning / SNI filtering) —
                                     design doc: docs/domain-unblock.md
    UnblockModels.cs                   BlockKind, UnblockMethod, DomainDiagnosis, UnblockedDomain, DomainName
    DnsWire.cs / Resolvers.cs          raw DNS to system resolvers (bypasses hosts) + DoH (1.1.1.1, 8.8.8.8)
    ConnectivityProbe.cs               TCP latency + TLS handshake probe, ClientHelloFragmentingStream
    TlsFragmenter.cs                   splits ClientHello into 2 TLS records through the SNI
    DomainDiagnoser.cs                 classification + IP ranking
    HostsFile.cs                       managed hosts block (exact-restore) + Windows hosts IO + flushdns
    SniRelay.cs                        loopback relay (127.77.0.x:443/80) used for SNI-filtered domains
    UnblockManager.cs / UnblockStore.cs  orchestration (relay → hosts → store, with rollback) + unblocked.json
  UnblockViewModels.cs               UnblockViewModel + row view-models
  Views/UnblockWindow.xaml(.cs)      unblock UI (diagnose / rank / unblock / status / remove)
  Views/MainWindow.xaml(.cs)         main UI
  Views/Dialogs.cs                   name / IP / launcher-picker modals
  Views/TrayIcon.cs                  system-tray quick-toggle
tests/LeaveMeAlone.Tests/   xUnit: Domain, BundleStore, BundleManager, FakeFirewallService,
                                     Unblock/ (fakes, diagnoser, manager, loopback TLS relay tests)
```

## Status & deferred work

- **Done:** bundles CRUD, per-bundle toggle, program + IP targets, common-launcher quick-add,
  startup reconciliation, JSON persistence, system tray + minimize-to-tray, start-with-Windows,
  import/export, unit tests. Builds and tests pass on Windows (.NET SDK 8.0.4xx).
- **Done (unblock domains):** diagnose / rank IPs / unblock (hosts pin or SNI relay) / remove,
  verified live against a Vietnamese ISP. Not yet exercised against the real system hosts file
  through the GUI.
- **Deferred (next):** per-target **inbound/outbound direction** (cross-cutting: `Models` →
  firewall rule builder → IP dialog → persistence). Ideas: single-instance guard, close-to-tray
  option, rule-verification UI polish.

See `CHANGELOG.md` for the version history.
