# Versioning & Release Rules

- **SemVer Rules**:
  - **Major (`X.0.0`)**: Breaking architectural changes, protocol rewrites.
  - **Minor (`x.Y.0`)**: New features, pages, settings, provider integrations.
  - **Patch (`x.y.Z`)**: Bug fixes, minor UI polish, edge-case fixes.
- **Component Scope**:
  - App: `src/DataJackUIGui/DataJackUIGui.csproj`.
  - Jack-in: `NetrunnerGames/Jack-in` (only if `plugin/` code changes).
  - IceBreaker: `NetrunnerGames/IceBreaker` (only if C++ proxy DLL changes).
- **Trigger & Version Resolution**:
  - Do NOT publish on every commit.
  - Execute release ONLY on explicit request (e.g. "publish release", "release datajack", "release X.Y.Z datajack").
  - **Implicit Versioning**: If the user does not specify an explicit version number (e.g., just says "release datajack"), automatically increment based on the previous highest version (patch bump by default, capping patch at 0-9 before rolling minor). Cross-check Git tags and `src/DataJackUIGui/DataJackUIGui.csproj` to find the true latest version and increment from there.
- **Release Execution (GitHub Actions `v*` Tag Push)**:
  1. Write `[+]` / `[-]` lines to `changelog.txt`.
  2. Write bullet points to `highlights.txt`.
  3. Bump `<Version>` in `src/DataJackUIGui/DataJackUIGui.csproj` to `X.Y.Z`.
  4. Commit changes, create tag `vX.Y.Z`, and push to GitHub:
     `git add -A && git commit -m "release: vX.Y.Z - <Title>" && git tag -a vX.Y.Z -m "Release vX.Y.Z" && git push origin main --tags`
     *(The `.github/workflows/release.yml` workflow triggers on `v*` tag push or `workflow_dispatch`, runs `dotnet test -c Release`, publishes the app, compiles Inno Setup 7, packages `DataJackUI-Inno-Setup.zip`, `DataJackUI-Update.zip`, and `DataJackUI-Portable.zip`, creates the GitHub Release with `changelog.txt`, and dispatches the Discord announcement with `highlights.txt` and `DataJackUI-Inno-Setup.zip`.)*
  - **Local Fallback**:
    `powershell -ExecutionPolicy Bypass -File .\publish_release.ps1 -Bump <patch|minor|major|none> -Title "<Title>"`
    *(Handles local version bump, framework-dependent build, Inno Setup compilation, Update & Portable & Setup ZIP packaging, git commit/tag/push, GitHub Release asset upload, and Discord release announcement dispatch).*
- **Release Packages Published**:
  - `DataJackUI-Inno-Setup.exe` (Modern Dynamic Inno Setup installer with Windows 10/11 auto dark theme & Johnny Silverhand banner)
  - `DataJackUI-Inno-Setup.zip` (Inno Setup executable in ZIP, automatically attached to Discord announcements)
  - `DataJackUI-Update.zip` (Direct in-app update archive containing published application binaries and assets)
  - `DataJackUI-Portable.zip` (Zero-install standalone application distribution)
- **Privacy Guidelines**:
  - Never include creator/owner mentions, specific user IDs, or internal roles in changelogs or release notes.
