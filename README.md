# Unvault Launcher

A lightweight, fast launcher for installing and managing Unreal Engine and Fab assets and plugins.
You sign in with your Epic account inside the app.

- **Engines**: install, modify and verify Unreal Engine versions. Untick target platforms, debug symbols or templates to save disk space.
- **Library**: install plugins into engines, add asset packs to projects, create projects from complete-project items, and keep downloads in the Vault Cache.
- **Works alongside the Epic Games Launcher**: picks up the engines and plugins it installed, and shares its Vault Cache and project folders.

## Security and privacy

- Unvault Launcher uses Epic's unofficial launcher API, as other third-party launchers do.
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

## Transparency

This is mostly a vibe-coded project. Why? Because I was desperate. The Epic Games Launcher has a lot of issues, and for more than a year they've been directly affecting my work, stopping me from downloading engine versions and the assets and plugins I own, including ones I developed and published myself. I'm tired of trying to fix it. There are other third-party launchers, but they often lack features I need.

## Disclaimer

Not affiliated with or endorsed by Epic Games. Unreal Engine, Epic Games and Fab are trademarks of Epic Games, Inc. 