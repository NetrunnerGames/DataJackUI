# Search and Tooling Rules

- **Do NOT create temporary Python scripts** for searching, replacing text, or manipulating files.
- **Git Repositories**:
  - Use `git grep` exclusively within git repositories.
- **Local / Non-Git Files & Directories**:
  - Use local installed search CLI tools:
    - **dnGrep**: `"C:\Program Files\dnGrep\dnGrep.exe"`
    - **grepWin**: `"$env:LOCALAPPDATA\Apps\grepWin\grepWin.exe"`
- **Code & Syntax Queries**:
  - Use grep.app or [PublicWWW](https://publicwww.com/docs/query-syntax/) query conventions for search patterns.
