# Unvault Launcher

A small, fast stand-in for the Epic Games Launcher for Unreal Engine work on Windows.
Windows 10/11 (64-bit), one self-contained exe. You sign in with your Epic account inside the app.

- **Engines**: install, modify and verify Unreal Engine versions. Untick target platforms, debug symbols or templates to save disk space.
- **Library**: install plugins into engines, add asset packs to projects, create projects from complete-project items, and keep downloads in the Vault Cache.
- **Works alongside the Epic Games Launcher**: picks up the engines and plugins it installed, and shares its Vault Cache and project folders.

## Security and privacy

- You sign in on Epic's own page. Unvault Launcher never sees your password, only a one-time code it exchanges with Epic.
- Your session is stored encrypted for your Windows account and sent only to Epic. Signing out also ends it on Epic's side.
- Downloads go over HTTPS, and engine and Fab files are checked against the hashes in Epic's manifests.
- No telemetry, no admin rights, no background service. It only connects to Epic's and Fab's servers.
- Release builds aren't code-signed, so Windows may warn about an unknown publisher on first run.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish Source/App -p:PublishProfile=win-x64
```

This puts `UnvaultLauncher.exe` in the repository root. `Source/CLI` builds `UnvaultLauncher-CLI.exe`, the same features on the command line.

## Disclaimer

Not affiliated with or endorsed by Epic Games. Unreal Engine, Epic Games and Fab are trademarks of Epic Games, Inc. Unvault Launcher uses Epic's unofficial launcher API, as other third party launchers do. It may stop working at any time, and you use it at your own risk.
