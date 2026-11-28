# IceBreaker Release Rules

- **Scope**: Only applies to `C:\Users\Flynn\Projects\IceBreaker` (`NetrunnerGames/IceBreaker`). Never touches DataJackUI or Jack-in. Sole GitHub Release asset is `version.dll`.
- **Trigger**: Do NOT release on every commit. Run only on explicit user request.
- **Execution Procedure (GitHub Actions `v*` Tag Push)**:
  1. Write `[+]` / `[-]` lines to `changelog.txt` and bullets to `highlights.txt` in the IceBreaker root.
  2. Commit changes, create tag `v<Version>`, and push to origin:
     `git add -A && git commit -m "release: v<Version> - <Title>" && git tag -a v<Version> -m "Release v<Version>" && git push origin main --tags`
  3. GitHub Actions (`.github/workflows/release.yml`) triggers on the `v*` tag push (or `workflow_dispatch`), builds `build/Release/version.dll` via CMake, publishes the GitHub Release with `changelog.txt` and `version.dll`, and dispatches the Discord announcement with `highlights.txt` and `IceBreaker-v<Version>.zip`.
