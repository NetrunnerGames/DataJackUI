---
name: versioning-and-release
description: >-
  Rules and procedures for tracking code revisions, determining Semantic Versioning (major/minor/patch)
  bumps across DataJackUI, Jack-in, and IceBreaker, and publishing GitHub releases using structured [+] / [-]
  changelog formatting when explicitly requested ("publish new build").
---

# Versioning & Release Workflow

## 1. Versioning Rules (Semantic Versioning)
Determine the appropriate version increment based on the nature of the changes:

* **Major Bump (`X.0.0`)**: Breaking architectural changes, major UI rewrites, or API protocol shifts (e.g. `2.0.0`).
* **Minor / Sub Bump (`x.Y.0`)**: New features, new pages, new settings, or new provider integrations (e.g. `2.1.0`).
* **Patch Bump (`x.y.Z`)**: Bug fixes, minor UI polish, edge-case fixes, or diagnostic adjustments (e.g. `2.0.5`).

### Component Cadence
* **DataJackUI Main App**: Versioned in `src/DataJackUIGui/DataJackUIGui.csproj`, `README.md`, and `build_release.ps1`.
* **Jack-in Plugin**: Versioned in `NetrunnerGames/Jack-in` releases (updated ONLY if plugin frontend code in `plugin/` is changed).
* **IceBreaker DLL**: Versioned in `NetrunnerGames/IceBreaker` releases (updated ONLY if native C++ proxy DLL source is changed).

---

## 2. Revision Tracking & Trigger
- Maintain a running log of all unreleased changes (`[+] added/changed`, `[-] removed`) during the session.
- **Do NOT publish releases automatically on every commit**.
- Execute the release build and GitHub publish **ONLY when the user explicitly requests "publish new build"**.

---

## 3. GitHub Release Title & Body Formatting
When publishing a release to GitHub, generate a dynamic, descriptive release title based on the primary focus of the changes, followed by the structured `[+]` / `[-]` changelog body:

### Release Title Format
`Release v<Version>: <Dynamic Category / Focus Summary>`

Examples:
- `Release v2.00.5: Bugfixes & UI Thread Safety Improvements`
- `Release v2.00.6: Hotfix for CloudRedirect Detection`
- `Release v2.1.0: New Provider Integration`

### Release Body Format
```text
[+] Added / modified feature or bug fix detail
[+] Additional feature or enhancement
[-] Removed or deprecated feature
```

---

## 4. Release Execution Procedure
When "publish new build" or `/versioning-and-release` is requested:
Execute `./publish_release.ps1 -Bump <patch|minor|major> -Title "<Title>" -ChangelogText "<[+] changelog entries>" -HighlightsText "<bullet points>"`

This single consolidated script handles all release steps automatically in sequence:
1. **Version Bumping & Build**: Invokes `./build_release.ps1 -Bump <patch|minor|major>` to update `DataJackUIGui.csproj`, compile the application, generate Velopack installer artifacts, and package `DataJackUI-win-Setup.zip`.
2. **Git Version Control**: Automatically stages all changes, creates git commit `release: v<Version> - <Title>`, creates tag `v<Version>`, and pushes commit + tags to GitHub.
3. **GitHub Release**: Publishes release tag `v<Version>` with release notes and binary installer assets via `gh release create`.
4. **Discord Announcement**: Invokes `./package_and_announce.ps1` to dispatch the Discord Components V2 release announcement (ANSI colored changelog, custom emotes, repo link button, and `DataJackUI-win-Setup.zip` attachment).
