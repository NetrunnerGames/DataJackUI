# Plan: Port DataJackUI to Linux & macOS (Avalonia)

Decision: **Avalonia** (desktop-first; Uno's XAML-compat win matters mostly for mobile/WASM, which is out of scope).

## Constraints from the user

- No rewrite in another language; C#/.NET only.
- Native libs differ per OS: `.dll` → `.so` (Linux) / `.dylib` (macOS).
- Steam paths differ per distro; isolated layouts (Bazzite/Flatpak/etc.) are **out of scope**.
- DepotDownloader (repacked `DepotDownloaderMod`), Steamless, SteamAutoCrack are **consumed as prebuilt binaries** — never compiled from source here.
- No Windows-only regressions on the existing WPF build.

## Verified current state (what's Windows-bound)

- **WPF/WPF-UI**: all of `Views/`, `MainWindow.xaml`, `WPF-UI` package, `DarkAcrylicMenuRenderer` (user32/dwmapi P/Invoke), WinForms `NotifyIcon` tray, `ToastService` acrylic toasts, `FolderBrowserDialog`.
- **Registry**: Steam detection (`SOFTWARE\Valve\Steam`), `datajackui://` protocol (ProtocolService), run-on-startup key, machine GUID (AuthService).
- **Processes**: `cmd.exe` wrappers, `rclone.exe` discovery, embedded tool exes (`DepotDownloaderMod.exe`, SteamAutoCrack + its .NET runtime bootstrap, Steamless), `version.dll`-style fix payloads.
- **Velopack**: updates (Linux/macOS builds exist in Velopack).
- Already portable: ViewModels (CommunityToolkit.Mvvm), all scraping/API/jobs services, `libraryfolders.vdf` parsing, `DenuvoFix` apply/revert logic (pure file ops).

## Architecture

```
src/DataJackUI.Core          net8.0    services, ViewModels, Models (most of Services/ today)
src/DataJackUI.Abstractions  net8.0    IPlatformEnvironment (below)
src/DataJackUI.Wpf           net8.0-windows  existing app, head unchanged
src/DataJackUI.Avalonia      net8.0    new head: Linux + macOS (+ win fallback optional)
```

`IPlatformEnvironment` (single interface, injected via DI):
- `string? DetectSteamPath()` — per-OS detection (below)
- `string SteamExeMarker` — `steam.exe` / `steam.sh` / `Contents/MacOS/steam` (validity check uses the `steamapps` folder instead of an exe marker — most reliable everywhere)
- `void RegisterProtocol(string scheme)` — registry (win) / `.desktop` file (linux) / `Info.plist` `CFBundleURLTypes` (mac)
- `void SetRunOnStartup(bool)` — Run key / `.desktop` autostart / `LaunchAgents` plist
- `string MachineId()` — machine GUID / `/etc/machine-id` / `IOPlatformUUID`
- Tray + toast abstractions
- `string ToolDir` + `string RcloneBinary` ("rclone.exe" vs "rclone"), `ProcessShell` ("cmd.exe /c" vs `bash -c` / direct exec)

## Steam path detection (no isolated/flatpak layouts)

- **Linux**: probe in order: `~/.steam/steam` (universal symlink), `~/.local/share/Steam`, `~/.Steam/steam` — each validated by presence of `steamapps/`. Reject paths whose `steamapps` isn't readable. (Bazzite/Flatpak/ostree explicitly unsupported.)
- **macOS**: `~/Library/Application Support/Steam/steamapps` (and `~/Library/Application Support/Steam (mac)/` for older installs). Note: Steam on macOS may have no native Steam builds of some games — game installs still resolve via the same VDF walk.
- **Windows**: existing registry path logic, unchanged.
- Steam language: registry (win) / parse `~/.steam/steam/config/config.vdf` `language` field (linux/mac).

## Tools (consumed as-is)

- Keep the current **download-from-GitHub-release → extract → run** flow, but select assets per OS:
  - `DepotDownloaderMod`: use its Windows exe today; on Linux/macOS either (a) an upstream linux build if the repo publishes one, or (b) run the Windows build under **Wine** (Linux) / **Wine-Crossover** (mac) — feature-flagged, off by default until tested. (Assets are zips; only the launch wrapper differs.)
  - **Steamless**: runs on JVM — on Linux/macOS expect `java -jar` invocations instead of an exe.
  - **SteamAutoCrack**: .NET/Python tool; per-OS release asset + its own runtime bootstrap; on Linux/macOS, run CLI mode (the SAC GUI host feature stays Windows-only).
  - `rclone`: official per-OS binaries already exist; binary name differs (`rclone`).
- Remove `cmd.exe` assumptions from service code; use `Process` with explicit FileName and argument arrays (no shell), which also fixes macOS quoting.

## Dlls/exports

- Per user decision: on **Linux and macOS, Windows games run through Proton/Wine compatibility** — game installs live in `steamapps/common/<Game>` and the fix payloads (`version.dll` etc.) apply unchanged into those folders; Proton executes them. No `.so`/`.dylib` porting of fix payloads is needed; just resolve install dirs via the VDF walk as today.
- Any *app-side* native imports (`user32`, `dwmapi`) are WPF-head-only and don't follow Core.
- Audit any `net8.0-windows` TFM refs in Core — after split, Core targets `net8.0` with no `Microsoft.Windows.*` packages.

## UI port (Avalonia head)

- Recreate `MainWindow` + navigation (Avalonia `NavigationView`-style layout from the CommunityToolkit or manual split view) — WPF-UI package is replaced by Avalonia Fluent theme; symbol icons via Avalonia `PathIcon`/font glyphs.
- All 20+ views converted screen-by-screen; converters rewritten to Avalonia bindings (`IsVisible`, value converters remain, `IValueConverter` interface is the same shape).
- Tray: Avalonia `TrayIcon`. Toasts: in-app toast overlay window (Avalonia has no native OS toast API).
- Protocol deep link (`datajackui://fix/{appid}`) parsed at `Program.Main` in each head.

## Packaging & updates

- **Velopack** builds: `win-x64` (existing), `linux-x64` (AppImage), `osx-x64/osx-arm64` (.app bundle). Same update channel/versioning rules as the existing skill.
- Icons: `.ico` (win), `.png` set (linux `.desktop`), `.icns` (mac).

## Ordered tasks

1. **Split Core out of the WPF project** — move `Services/`, `Models/`, `ViewModels/`, `Resources/` into `DataJackUI.Core` (net8.0); WPF project references it and keeps Views/XAML. Introduce `IPlatformEnvironment` with a Windows implementation (registry/NotifyIcon/cmd wrappers unchanged). WPF app must behave identically. Update + run tests.
2. **Multi-OS Steam path + shell abstractions** — implement Linux/macOS `DetectSteamPath`, tool binary names, `ProcessShell` (no `cmd.exe`), protocol/autostart. Add unit tests per platform via interface fakes.
3. **Avalonia head skeleton** — app boot, DI host reuse, navigation shell, tray, toast overlay, Settings + protocol handler. Enough to run and see Fixes grid.
4. **Port views screen-by-screen**, Fixes tab first (it's the newest and least coupled), then Downloads, Manage, Cloud Saves, Builds, Settings. Keep WPF head green after each.
5. **Tool launch paths per OS** — DepotDownloaderMod/Steamless/SteamAutoCrack/rclone asset selection + launch wrappers; Wine fallback flag for Windows-only tools.
6. **CI + packaging** — Velopack linux/macOS builds; smoke-test on a real Ubuntu and macOS VM.

## Validation

- `dotnet test` on all three TFMs (existing 330 tests must keep passing; add platform-detection tests with fake home dirs).
- Manual matrix: Windows (regression), Ubuntu 24.04 (Steam apt install), macOS (standard Steam path) — Fixes tab: list → scrape online-fix → download → apply into `steamapps/common/<Game>` (Linux/macOS run the Windows game build via Proton/Wine, per user decision), revert; depot download; SteamAutoCrack launch; protocol handler; tray; updates.

## Explicitly out of scope

- Flatpak/Bazzite/snap Steam layouts (blocked by upstream sandboxing, per user).
- Rewrite in any other language; mobile/WASM targets.
- Making Denuvo fix payloads cross-OS (they stay Windows `.dll` payloads for Windows game builds).
