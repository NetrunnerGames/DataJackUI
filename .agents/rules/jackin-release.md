# Jack-in Release Rules

- **Scope**: Only applies to `C:\Users\Flynn\Projects\Jack-in` (`NetrunnerGames/Jack-in`). Never touches DataJackUI or IceBreaker.
- **Trigger**: Do NOT release on every commit. Run only on explicit user request.
- **Execution Procedure**:
  1. Write `[+]` / `[-]` lines to `changelog.txt` and bullets to `highlights.txt` in the Jack-in root.
  2. Update `"version"` in `plugin.json` to `<Version>`.
  3. Package `plugin.zip`:
     ```powershell
     Remove-Item plugin.zip -Force -ErrorAction SilentlyContinue
     Compress-Archive -Path plugin.json, backend, public -DestinationPath plugin.zip
     ```
  4. Calculate SHA-256 and update `installed.json`:
     `{"Tag":"v<Version>","DllShas":{},"ZipSha":"<hash>","DisabledMillenniumEntries":null}`
  5. Sync to `%AppData%\DataJackUIGui\plugin\`.
  6. Git commit `release: v<Version> - <Title>`, tag `v<Version>`, and push to origin.
  7. Publish GitHub release with `plugin.zip`.
