# IceBreaker Release Rules

- **Scope**: Only applies to `C:\Users\Flynn\Projects\IceBreaker` (`NetrunnerGames/IceBreaker`). Never touches DataJackUI or Jack-in. Sole release asset is `version.dll`.
- **Trigger**: Do NOT release on every commit. Run only on explicit user request.
- **Execution Procedure**:
  1. Write `[+]` / `[-]` lines to `changelog.txt` and bullets to `highlights.txt` in the IceBreaker root.
  2. Build in Release mode:
     `cmake --build build --config Release`
  3. Git commit `release: v<Version> - <Title>`, tag `v<Version>`, and push to origin.
  4. Publish GitHub release attaching only `build/Release/version.dll`.
