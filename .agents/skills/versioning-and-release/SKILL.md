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

## 3. GitHub Release Body Formatting
When publishing a release to GitHub, format the release description strictly using this structure:

```text
[+] Added / modified feature or bug fix detail
[+] Additional feature or enhancement
[-] Removed or deprecated feature
```

---

## 4. Release Execution Procedure
When "publish new build" is requested:
1. Update `<Version>` in `src/DataJackUIGui/DataJackUIGui.csproj` and `build_release.ps1`.
2. Execute `./build_release.ps1` to produce published artifacts in `Releases/` and `bin/PublishFrameworkDependent/`.
3. Commit and tag the git repository (`git tag v<Version>`).
4. Publish the tag and release artifacts to GitHub using `gh release create` or the GitHub API.
