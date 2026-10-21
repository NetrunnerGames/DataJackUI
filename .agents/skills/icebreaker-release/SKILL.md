---
name: icebreaker-release
description: >-
  Build-and-release workflow for the IceBreaker native version.dll only (repo
  NetrunnerGames/IceBreaker): CMake Release build, structured [+] / [-] changelog
  formatting, git commit + tag, and GitHub release upload of version.dll — used
  when the user explicitly requests a new IceBreaker build/release.
---

# IceBreaker Release Workflow

## 0. Scope

This workflow applies **only** to the IceBreaker repository (`C:\Users\Flynn\Projects\IceBreaker`,
remote `NetrunnerGames/IceBreaker`). It never touches DataJackUI version files
(`DataJackUIGui.csproj`, `build_release.ps1`), never uploads DataJackUI files
(`DataJackUI-win-Setup.zip`, Velopack artifacts, Lua/plugin files) into an IceBreaker
release, and has no Velopack or Discord step. The sole release artifact is
`build/Release/version.dll`.

## 1. Versioning Rules (Semantic Versioning)

Determine the appropriate version increment based on the nature of the changes:

* **Major Bump (`X.0.0`)**: Breaking architectural changes or interface/protocol shifts (e.g. `2.0.0`).
* **Minor / Sub Bump (`x.Y.0`)**: New hooks, new scanner capabilities, new engine features (e.g. `2.1.0`).
* **Patch Bump (`x.y.Z`)**: Bug fixes, pattern refreshes, edge-case or crash fixes (e.g. `2.1.1`).

### Component Cadence
* **IceBreaker DLL**: Versioned exclusively via `NetrunnerGames/IceBreaker` release tags
  (`v<Version>`). There is no in-repo version constant to bump; the release tag is the version.
  Keep the README architecture note in sync only when the architecture itself changes.

## 2. Revision Tracking & Trigger
- Maintain a running log of all unreleased changes (`[+] added/changed`, `[-] removed`) during the session.
- **Do NOT publish releases automatically on every commit.**
- Execute the release build and GitHub publish **ONLY when the user explicitly requests "publish new build"** (or a specific IceBreaker release).

## 3. GitHub Release Title & Body Formatting
Generate a dynamic, descriptive title based on the primary focus of the changes,
followed by the structured `[+]` / `[-]` changelog body:

### Release Title Format
`Release v<Version>: <Dynamic Category / Focus Summary>`

Examples:
- `Release v2.1: Dynamic Interface Interception & Manifest Hot-Reload`
- `Release v2.0.1: Pattern Refresh for Latest Steam Client`

### Release Body Format
```text
[+] Added / modified feature or bug fix detail
[+] Additional feature or enhancement
[-] Removed or deprecated feature
```

### Sensitive Information & Privacy Guidelines
- **Never include internal administrative roles, creator/owner mentions, specific user IDs, or sensitive authentication details** in commit messages, release titles, or release notes.
- Use professional, general wording such as "ownership verification flow" or "library visibility handling".

## 4. Release Execution Procedure

1. **Changelog files** (IceBreaker repo root, plain text):
   - `changelog.txt`: the `[+]` / `[-]` lines for this release.
   - `highlights.txt`: bullet-point highlights.
2. **Build** (from `C:\Users\Flynn\Projects\IceBreaker`):
   ```bat
   cmake --build build --config Release
   ```
   Output: `build\Release\version.dll`. Verify with
   `dumpbin /exports build\Release\version.dll` if unsure — all six forwarded
   version.dll exports must be present.
3. **Git** (stage repo source only; leave `vendor/` reference checkouts, `build/`, and other
   untracked references out of the release commit):
   ```bat
   git add CMakeLists.txt src
   git commit -m "release: v<Version> - <Title>"
   git tag -a v<Version> -m "Release v<Version>: <Title>"
   git push origin main v<Version>
   ```
4. **GitHub Release** (attach **only** `build\Release\version.dll`):
   ```bat
   gh release create v<Version> build/Release/version.dll --repo NetrunnerGames/IceBreaker --title "Release v<Version>: <Title>" --notes-file changelog.txt
   ```
   If `gh` is not authenticated, publish via the git credential manager token
   (`git credential fill` for `github.com`) against the GitHub REST API:
   `POST /repos/NetrunnerGames/IceBreaker/releases`, then upload the DLL to the returned
   `uploads.../assets?name=version.dll` URL. See `upload_github_release.ps1` in the
   DataJackUI repo for the credential-retrieval pattern.
5. **Verification**: confirm the release lists exactly one asset, `version.dll`, matching
   the freshly built file size, and that the tag points at the release commit.
