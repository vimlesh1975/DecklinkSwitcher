# Project Rules for DecklinkSwitcher

## Build Rule
- Whenever building or compiling this project, ALWAYS use `build.ps1` via PowerShell:
  `powershell -ExecutionPolicy Bypass -File .\build.ps1`
- Do NOT run bare `dotnet build` as the final build step. `build.ps1` handles:
  1. Stopping any running instances of DecklinkSwitcher to prevent file locking errors (`MSB3027` / `MSB3021`).
  2. Compiling the project.
  3. Generating the versioned, timestamped executable in `bin\Debug\net10.0-windows\win-x64\`.

