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
  - Execute release ONLY on explicit request (e.g. "publish release", "release build").
- **Release Execution**:
  1. Write `[+]` / `[-]` lines to `changelog.txt`.
  2. Write bullet points to `highlights.txt`.
  3. Run as background task:
     `powershell -ExecutionPolicy Bypass -File .\publish_release.ps1 -Bump <patch|minor|major> -Title "<Title>"`
- **Privacy Guidelines**:
  - Never include creator/owner mentions, specific user IDs, or internal roles in changelogs or release notes.
