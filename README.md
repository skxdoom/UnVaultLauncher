# UnVault Launcher

A streamlined and lightweight launcher for installing and managing Unreal Engine and Fab assets and plugins.

- **Engines**: install, modify and verify Unreal Engine versions. Untick target platforms, debug symbols or templates to save disk space.
- **Library**: install plugins into engines, add asset packs to projects, create projects from complete-project items, and keep downloads in the Vault Cache.
- **Works alongside the Epic Games Launcher**: picks up the engines and plugins it installed, and shares its Vault Cache and project folders.

## Screenshots

![Engines tab](https://dmkarpukhin.com/assets/img/unvault_a_0.4.0.jpg)

![Library](https://dmkarpukhin.com/assets/img/unvault_c_0.4.0.jpg)

## Download

Get `UnVaultLauncher.exe` from [Releases](https://github.com/skxdoom/UnVaultLauncher/releases/latest). It's a single file with nothing to install, for 64-bit Windows 10 or 11.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish Source/App -p:PublishProfile=win-x64
```

This puts `UnVaultLauncher.exe` in the repository root.

There's also a command-line tool for engines, for scripting and diagnostics. It isn't included in releases; `dotnet publish Source/CLI -p:PublishProfile=win-x64` builds it as a single `UnVaultLauncher-CLI.exe` in the repository root. It can sign in, list the versions you own and the engines installed, install, modify and verify engines, and list your Fab library. Installing Fab content is in the app only. Run it with `--help` for the commands.

## Security and privacy

- UnVault Launcher uses Epic's unofficial launcher API, as other third-party launchers do.
- You sign in on Epic's own page. UnVault Launcher never sees your password, only a one-time code it exchanges with Epic.
- Your session is stored encrypted for your Windows account and sent only to Epic. Signing out also ends it on Epic's side.
- Downloads go over HTTPS, and engine and Fab files are checked against the hashes in Epic's manifests.
- No telemetry, no admin rights, no background service. It connects to Epic's and Fab's servers, and at startup asks GitHub whether a newer release is out (Settings can turn that off).
- Release builds aren't code-signed, so Windows may warn about an unknown publisher on first run.

## Transparency

This is mostly a vibe-coded project. Why? Because I was desperate. The Epic Games Launcher has a lot of issues, and for more than a year they've been directly affecting my work, blocking me from downloading engine versions and the assets and plugins I own, including ones I developed and published myself. I'm tired of trying to fix it. There are other third-party launchers, but they often lack features I need.

## Thanks

Epic's launcher API isn't publicly documented. UnVault Launcher builds on what these projects worked out:

- [Legendary](https://github.com/legendary-gl/legendary): sign-in, the launcher's download API and the manifest format
- [egs-api-rs](https://github.com/AchetaGames/egs-api-rs): the Fab library and its downloads

## Disclaimer

Not affiliated with or endorsed by Epic Games. Unreal Engine, Epic Games and Fab are trademarks of Epic Games, Inc. 
