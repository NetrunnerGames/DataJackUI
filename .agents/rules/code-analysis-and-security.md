# Code Analysis, Security & Quality Rule

Integrate and enforce static analysis, security scanning, and formatting:

1. **Roslyn Code Analysis & Analyzers**:
   - Native .NET SDK Roslyn analyzers are active during builds, configured via `.editorconfig`.
   - Checks for performance, reliability, nullability, and StyleCop/CodeFactor rules (such as `SA1507` for multiple blank lines, `CA1835` for memory-based stream operations).
   - Automated via `.github/workflows/code-quality.yml`.

2. **UI & XAML Styling (XamlStyler / xstyler)**:
   - Formats and aligns all WPF XAML layouts cleanly (`xstyler -d src/DataJackUIGui -r`).
   - Passive check mode (`xstyler -d src/DataJackUIGui -r -p`) verifies styling integrity in CI.
   - Integrated into `build_release.ps1` and `.github/workflows/code-quality.yml`.

3. **Secrets & Keys Scanning (Gitleaks)**:
   - Local CLI tool: scans commits and repository contents for leaked API keys, tokens, and credentials (`gitleaks detect --no-banner`).
   - Exceptions configured in `.gitleaks.toml`.
   - Integrated into local release verification via `build_release.ps1`.

4. **Semgrep (Full Repository SAST Vulnerability Scanning)**:
   - Local CLI tool: runs `semgrep scan --config "p/default" --config "p/csharp" --metrics=off --error`.
   - Scans all tracked code across the repository (C# code, CI/CD GitHub Actions workflows, scripts, JSON/YAML configurations).
   - Checks against OWASP and CWE vulnerability patterns (e.g. ReDoS timeouts, path traversal, injection).
   - Must pass with 0 blocking security findings prior to release packaging.

5. **dotnet format (Whitespace & Style Enforcement)**:
   - Run `dotnet format whitespace src/DataJackUIGui/DataJackUIGui.csproj` to maintain clean diffs and prevent blank line violations, improper indentation, and formatting inconsistencies.
   - Run before compiling releases and verified in PR checks (`--verify-no-changes`).

6. **Automated Release Pipeline**:
   - `build_release.ps1` runs these verification steps (`dotnet format`, `xstyler`, `gitleaks`, full repo `semgrep` scan, and standard Roslyn build checks) locally prior to publishing and packaging installer binaries.
