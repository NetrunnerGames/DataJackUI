# Versioning & Release Rules

- **SemVer Rules**:
  - **Major (`X.0.0`)**: Breaking architectural changes, protocol rewrites.
  - **Minor (`x.Y.0`)**: New features, pages, settings, provider integrations.
  - **Patch (`x.y.Z`)**: Bug fixes, minor UI polish, edge-case fixes.
- **Component Scope**:
  - App: `src/DataJackUIGui/DataJackUIGui.csproj`.
  - Jack-in: `NetrunnerGames/Jack-in` (only if `plugin/` code changes).
  - IceBreaker: `NetrunnerGames/IceBreaker` (only if C++ proxy DLL changes).
- **Trigger**:
  - Do NOT publish on every commit.
  - Execute release ONLY on explicit request (e.g. "publish release", "release build", "release X.Y.Z datajack").
- **Release Execution**:
  1. Write `[+]` / `[-]` lines to `changelog.txt`.
  2. Write bullet points to `highlights.txt`.
  3. Execute the consolidated release pipeline:
     `powershell -ExecutionPolicy Bypass -File .\publish_release.ps1 -Bump <patch|minor|major|none> -Title "<Title>"`
     *(This handles: version bump, framework-dependent build, Inno Setup compilation, Velopack delta packages, Portable & Setup ZIP packaging, git commit, tag, push, GitHub Release asset upload, and Discord release announcement dispatch).*
- **Release Packages Published**:
  - `DataJackUI-Inno-Setup.exe` (Modern Dynamic Inno Setup installer with Windows 10/11 auto dark theme & Johnny Silverhand banner)
  - `DataJackUI-Inno-Setup.zip` (Inno Setup executable in ZIP, automatically attached to Discord announcements)
  - `DataJackUI-Portable.zip` (Zero-install standalone application distribution)
  - Velopack delta updates (`*-full.nupkg`, `*-delta.nupkg`, `assets.win.json`, `releases.win.json`, `RELEASES`) for 300 KB in-app patching.
- **Privacy Guidelines**:
  - Never include creator/owner mentions, specific user IDs, or internal roles in changelogs or release notes.
