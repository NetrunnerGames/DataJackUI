# GitHub CLI (`gh`) Rule

All GitHub, git, and repository operations must use `gh` CLI commands and credentials rather than manual or non-authenticated git operations.

## Guidelines
1. **Authentication & Status**:
   - Check authentication: `gh auth status`
   - Active account: `ExpectedNinja`
   - Git credential helper configured via: `gh auth setup-git`

2. **Repository Sync & Inspection**:
   - Sync branches: `gh repo sync`
   - View repository info: `gh repo view`

3. **Releases & Tags**:
   - List releases / tags: `gh release list --limit <N> --json tagName`
   - View release: `gh release view <tag>`
   - Create release: `gh release create <tag> <assets...> --title "<title>" --notes "<notes>"`
   - Upload / replace release assets: `gh release upload <tag> <assets...> --clobber`

4. **Issues & Pull Requests**:
   - View issue: `gh issue view <number>`
   - Close issue: `gh issue close <number> --comment "<comment>"`
   - Create PR: `gh pr create --title "<title>" --body "<body>"`
   - Merge PR: `gh pr merge <number> --squash`

5. **Script Standards**:
   - All automated automation scripts (`build_release.ps1`, `upload_github_release.ps1`, `publish_release.ps1`) must query repository release and tag state via `gh` commands (`gh release list`, `gh release view`, `gh release create`, `gh release upload`).
