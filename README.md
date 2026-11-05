<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/brand-logo.png">
  <img src="assets/brand-logo-light.png" alt="HDLSS5" height="64">
</picture>

# HDLSS5 2.0

**DLSS 5 Neural Rendering, up-to-date DLSS and HDR in your PC games — one click, always reversible.**

HDLSS5 finds the games installed on your PC, works out how each one is built (engine, graphics API, DLSS, anti-cheat) and installs the right mods in the right place: ReShade with RenoDX NR, OptiScaler, the NR model for your GPU, the latest DLSS and Streamline DLLs, HDR mods and mod loaders. Every file it replaces is saved first, so you can always go back.

**[⬇ Download HDLSS5 2.0](https://github.com/giorgiobascialla-hub/Ourze/releases/latest)**

![Game library](assets/screenshot-library.png)

## What it does

- **Automatic library**: finds Steam and Xbox / Game Pass games; others can be added manually. Each cover shows the game's status in one line (Neural Rendering active, mods to import, ready to configure…).
- **One button to start**: the game page explains the situation in plain language and offers a single main action, such as "Enable Neural Rendering".
- **"Did it work?"**: after a few minutes of play, it reads the game logs and tells you whether Neural Rendering actually started.
- **Always reversible**: every change goes into the history; "Remove" puts the game's original files back.
- **Clear status**: "Install" only when something is missing, "Update" only when a newer version really exists. If a mod doesn't exist for a game, the app says so.

![Game page](assets/screenshot-game.png)

## Components

| Tab | Contents |
|---|---|
| **ReShade** | ReShade (with a choice of menu key), RenoDX NR (Lecram 10.8 or ShortFuse), Feeder, RenoDX HDR mods, MFG Unlock (RTX 4000), Lilium and CAS shaders |
| **OptiScaler** | OptiScaler Standard and F5 (with a choice of menu key), with ReShade loaded by OptiScaler without conflicts |
| **DLL** | NR model 310.9.16.0 for RTX 4000/5000, DLSS (Super Resolution, Frame Generation, Ray Reconstruction) and Streamline |
| **Other** | Unreal HDR (Engine.ini), UE4SS with in-game HDR reader, Ultimate ASI Loader, REFramework |

## DLL page

Every NVIDIA DLL in your games at a glance, compared with the latest releases. For each game, pick the latest version, an earlier one, or the game's original.

![DLL page](assets/screenshot-dll.png)

## Requirements

- Windows 10/11 64-bit, no runtime to install.
- NVIDIA RTX GPU; Neural Rendering needs an RTX 4000 or RTX 5000 with an up-to-date driver.
- The interface is currently in Italian.

## Installation

1. Download the `HDLSS5-Portable` zip from the [latest release](https://github.com/giorgiobascialla-hub/Ourze/releases/latest).
2. Extract it to a writable folder (not "Program Files") and run `HDLSS5.exe`.
3. Always close the game before installing or removing anything.

The app is not digitally signed, so Windows SmartScreen may ask for confirmation on first launch ("More info" › "Run anyway"). You can verify the zip with the `.sha256` file attached to the release.

**Coming from 1.x?** Version 2.0 recognizes installations made with HDLSS5 1.x and can take them over without touching the game files, or restore the originals from the 1.x backups. Version 1.5.1 is still available among the [previous releases](https://github.com/giorgiobascialla-hub/Ourze/releases).

Installation is blocked in games with anti-cheat (EasyAntiCheat, BattlEye). HDLSS5 is an independent project, not affiliated with NVIDIA; components are downloaded from their authors' official repositories and verified.

## For developers

A from-scratch rewrite of 1.5 in **.NET 8 + WPF (MVVM)**.

```
src/Hdlss.Core     UI-free logic (testable)
  IO/              PathGuard, FileHash, PeFile, IniDocument, Archives
  Storage/         AppPaths, ContentStore (content-addressed backups), JsonFile
  Transactions/    PlanBuilder, RemovalPlanner, TransactionEngine
  Games/           GameLibrary (Steam, Xbox, manual), ExecutableDetector
  Net/             HttpService (ETag cache, verified downloads)
  Components/      ReShade, RenoDX, OptiScaler, NR model, DLLs, HDR, loaders
  Legacy/          reading and restoring 1.x installations
src/Hdlss.App      WPF: AppHost, ViewModels, Views
tests/             xUnit
bundled/           bundled RenoDX NR add-ons (verified by SHA-256)
```

```bash
dotnet build HDLSS5.sln -c Release
```

```bash
dotnet test tests/Hdlss.Core.Tests
```

Full portable package (including the NR models): `.\build-portable.ps1`.
