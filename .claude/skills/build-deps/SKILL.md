---
name: build-deps
description: Install or repair the build prerequisites for this repo (.NET 10 SDK, Node.js/npm, Windows 10 SDK, Git, WebView2). Use when `build.ps1` reports "issue(s) found", when a build fails with a missing SDK/toolchain, or when setting up a fresh Windows checkout.
---

# Build dependencies

This repo already ships the tooling. Do not write new detection or install logic.

- `scripts\setup-dev.ps1` detects and installs prerequisites via winget, refreshes PATH, and trusts the checkout for GitVersion.
- `build.ps1 -CheckOnly` reports prerequisites only. This is what prints `N issue(s) found`.

## Procedure

1. **Diagnose** (never installs, never touches git config):

   ```powershell
   .\scripts\setup-dev.ps1 -CheckOnly
   ```

2. **Install what is missing.** Report the missing list to the user and confirm before installing, since winget changes machine state and may prompt for elevation.

   ```powershell
   .\scripts\setup-dev.ps1
   ```

   Run this in an elevated PowerShell if winget reports it needs admin. If winget itself is missing, tell the user to install "App Installer" from the Microsoft Store, then rerun.

3. **Refresh PATH, then re-verify.** Newly installed tools are not on the PATH of any shell that was already open, including this session's shell. Pull the current machine and user PATH into the process before re-checking:

   ```powershell
   $env:Path = @(
       [Environment]::GetEnvironmentVariable("Path", "Machine"),
       [Environment]::GetEnvironmentVariable("Path", "User")
   ) -join ";"
   .\scripts\setup-dev.ps1 -CheckOnly
   ```

   If a tool still is not found after this, the install needs a fresh terminal (or a reboot for the Windows SDK). Say so rather than looping on retries.

4. **Confirm the build works** once the check is clean:

   ```powershell
   .\build.ps1
   ```

## Package IDs (for reference and manual fallback)

| Missing item | winget id |
| --- | --- |
| .NET SDK / .NET 10 SDK | `Microsoft.DotNet.SDK.10` |
| Node.js (and npm) | `OpenJS.NodeJS.LTS` |
| Windows 10 SDK | `Microsoft.WindowsSDK.10.0.26100` |
| Git | `Git.Git` |
| WebView2 Runtime | `Microsoft.EdgeWebView2Runtime` |

Manual install: `winget install --id <id> -e`.

## Notes

- The Windows SDK can also come from the Visual Studio Installer ("Desktop development with C++" or the standalone SDK component). Detection just looks for a versioned directory under `%ProgramFiles(x86)%\Windows Kits\10\Include`.
- Node.js is required even for the WinUI build: it runs `npm ci` to restore `@microsoft/mxc-sdk` and copy `wxc-exec.exe` into the output.
- Git is required at *build* time, not just for version control, because GitVersion reads repository metadata. `setup-dev.ps1` adds the checkout to `git config --global safe.directory`; pass `-NoTrustRepository` to skip that.
- The .NET version floor is pinned in `global.json` (`10.0.100`, `rollForward: latestFeature`).
- `setup-dev.ps1 -RunValidation` additionally runs the full build plus the shared and tray test projects required by `AGENTS.md` closeout.
- Keep this file free of em dashes: `scripts\validate-docs.ps1` runs during `build.ps1` and fails the build on them.
