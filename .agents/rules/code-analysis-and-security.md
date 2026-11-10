# Code Analysis, Security & Quality Rule

This rule defines static analysis, security scanning, styling, and verification policies across both **C# / WPF (`DataJackUI`)** and **C/C++ (`IceBreaker`)** repositories.

---

## 1. C# & WPF Guidelines (`DataJackUI.Net`)

1. **Roslyn Code Analysis & Analyzers**:
   - Native .NET SDK Roslyn analyzers run during builds and in CI (`.github/workflows/code-quality.yml`).
   - Configured via `.editorconfig` (performance, nullability, CodeFactor style enforcement like `SA1507`).

2. **UI & XAML Styling (XamlStyler / `xstyler`)**:
   - Formats WPF XAML layouts cleanly:
     ```powershell
     xstyler -d src/DataJackUIGui -r
     ```
   - Verified passively in CI: `xstyler -d src/DataJackUIGui -r -p`.

3. **Secrets & Keys Scanning (Gitleaks)**:
   - Run locally before releases:
     ```powershell
     gitleaks detect --no-banner
     ```
   - Repository-specific exceptions configured in `.gitleaks.toml`.

4. **Semgrep (Full Repository SAST Vulnerability Scanning)**:
   - Scans all tracked code across the repo:
     ```powershell
     semgrep scan --config "p/default" --config "p/csharp" --metrics=off --error
     ```
   - Checks against OWASP and CWE vulnerability patterns (e.g. ReDoS timeouts, path traversal).
   - Must pass with 0 blocking security findings prior to release packaging.

5. **C# Formatting (dotnet format)**:
   - Run `dotnet format whitespace src/DataJackUIGui/DataJackUIGui.csproj` to maintain clean diffs and uniform line endings.

6. **Automated Release Pipeline**:
   - `build_release.ps1` runs these verification steps (`dotnet format`, `xstyler`, `gitleaks detect`, and `semgrep scan`) automatically prior to publishing and packaging installer binaries.

---

## 2. C / C++ Guidelines (`IceBreaker`)

1. **Cppcheck (Static Analysis & Bug Detection)**:
   - Primary static analysis tool for C/C++ memory safety, uninitialized variables, out-of-bounds access, and resource leaks.
   - Run locally across `src/`:
     ```powershell
     & "C:\Program Files\Cppcheck\cppcheck.exe" -D_M_X64 --enable=warning,performance,portability --inline-suppr --suppress=missingIncludeSystem --error-exitcode=1 -I src -I vendor/minhook/include src
     ```
   - Must pass with 0 errors/warnings before tagging or publishing a release.

2. **Clang-Format (C/C++ Formatting & Code Styling)**:
   - Enforces uniform styling across all `.cpp`, `.c`, and `.h` files based on `.clang-format`:
     ```powershell
     Get-ChildItem -Path src -Recurse -Include *.cpp, *.h, *.c | ForEach-Object {
         & "C:\Program Files\LLVM\bin\clang-format.exe" -i $_.FullName
     }
     ```
   - Check formatting without modifying:
     ```powershell
     Get-ChildItem -Path src -Recurse -Include *.cpp, *.h, *.c | ForEach-Object {
         & "C:\Program Files\LLVM\bin\clang-format.exe" --dry-run --Werror $_.FullName
     }
     ```

3. **Semgrep (C/C++ SAST Security Scanning)**:
   - Scans C/C++ source code for unsafe memory operations, buffer overflows, format string vulnerabilities, and command injections:
     ```powershell
     semgrep scan --config "p/c" --config "p/default" --metrics=off --error src
     ```

4. **Secrets & Keys Scanning (Gitleaks)**:
   - Scans commits and repository contents for accidental secret exposure:
     ```powershell
     gitleaks detect --no-banner
     ```

5. **Release Verification**:
   - Before building `cmake --build build --config Release`, run the static analysis (`cppcheck`), style enforcement (`clang-format`), security scan (`semgrep`), and secret scan (`gitleaks`).
