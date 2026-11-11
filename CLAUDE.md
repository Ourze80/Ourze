# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

HDLSS5 2.x: a portable Windows (x64) WPF app, .NET 8, that installs and manages DLSS 5 / Neural Rendering mods on PC games. Supported paths: ReShade + RenoDX NR, Feeder, OptiScaler. It also manages HDR mods, loaders (ASI Loader, UE4SS, REFramework) and NVIDIA DLL updates (DLSS, Streamline, NR model). The interface is Italian, with an English option in Settings; texts on GitHub (README, release notes, `README.txt` shipped in the zip) are English.

Branches: work happens on `v2`; `main` on GitHub (`Ourze80/Ourze`) is a merge of `v2`. Releases are `vX.Y.Z` tags on `main` with the portable zip and its `.sha256` attached.

## Commands

```powershell
dotnet build HDLSS5.sln -v q -nologo
dotnet test tests\Hdlss.Core.Tests                                         # xUnit, Core only
dotnet test tests\Hdlss.Core.Tests --filter "FullyQualifiedName~MenuKeyTests"   # one class or test
src\Hdlss.App\bin\Debug\net8.0-windows\win-x64\HDLSS5.exe                  # dev build (data-v2 next to it)
.\build-portable.ps1 [-NrPackages <folder>] [-NoZip] [-SkipTests]
```

- `build-portable.ps1` runs the tests, then publishes a self-contained single-file build to `dist\HDLSS5-Portable-<version>\`. It adds `bundled\`, `nr-packages\` (the RTX 4000/5000 NR models, by default from `..\nr-packages`, hash-checked) and `README.txt`, then writes `.zip` + `.sha256`.
- The version lives in `Directory.Build.props` (`<Version>`) and `src/Hdlss.App/app.manifest` (`assemblyIdentity version`). Bump both.
- Only one instance per data folder may run (named mutex). Closing the dev build before rebuilding is enough; a portable copy elsewhere is a different instance.
- Windows PowerShell 5: `Get-Content | Set-Content` re-encodes files (mojibake, BOM). Edit source files with an editor or tool, not PS pipelines.

## Architecture

Two projects: `src/Hdlss.Core` (all logic, no UI) and `src/Hdlss.App` (WPF, MVVM with CommunityToolkit.Mvvm; the assembly is `HDLSS5`). `tests/Hdlss.Core.Tests` tests Core only (`InternalsVisibleTo`).

**Composition.** `AppHost` builds every service once (`Services`: paths, settings, content store, state store, transaction engine, HTTP, log). There are no static singletons in Core. `AppPaths` is portable: data lives in `data-v2\` next to the exe, unless `data-location.txt` beside it redirects it.

**Components** (`Core/Components`). Every installable thing is an `IComponent`, usually a `ComponentBase`, registered in `ComponentCatalog`. A component is planned against a `GameContext`. That is a snapshot of one game: detected API (`ExecutableDetector.DetectApi`, PE imports plus Agility SDK evidence; the user can override it per game), engine, GPU, a `FolderScan` (ReShade loaders/config/AddonPath, OptiScaler proxies, add-ons, anti-cheat) and the recorded `GameState`.
- `Detect` → status; `Blockers` → reasons it can't install now.
- `PlanInstallAsync` → a `ChangePlan`, a description of file writes and INI edits. It writes nothing.
- `LatestVersionAsync` / `CheckUpdateAsync` drive the Install / Update / "Applica le modifiche" button.
- `Options` (`ComponentOption`) are the per-card drop-downs. Their values are stored in the component record's `Meta`, and a change switches the card to "options changed". `OptionRange` lets an option also accept a typed number.
- `NrAdvisor` picks the recommended DLSS 5 path from API + native DLSS presence. `Compatibility` / `NrRules` enforce one injector, one copy of each loader, and menu-key conflicts (`MenuKeys`).

**Transactions** (`Core/Transactions`). This is the only place game files are written.
- `PlanBuilder` (via `ctx.NewPlan`) collects file adds and INI edits. It refuses to overwrite files owned by another component or modified by someone else since install, unless told otherwise: `keepIfModified`, `builtOnCurrent`, `sameKind`, and `AdoptGameChanges` for the game's own DLLs.
- `ComponentService` runs plan → preview (`PlanDialog`) → apply.
- `TransactionEngine` re-checks expected hashes, snapshots the originals into `ContentStore`, writes atomically, verifies, rolls back on error and journals everything. `ContentStore` is content-addressed and deduplicated by SHA-256.
- `StateStore` persists one `GameState` per game folder. Each `ComponentRecord` holds files with `Before`/`After` hashes, INI edits with their previous values, `Meta` and version.
- `RemovalPlanner` builds the inverse plan: it restores originals and reverts INI keys. All paths go through `PathGuard`, which rejects escapes, reparse points and device names; extra roots, like the Unreal config folder, are declared with `AddRoot`.

**Other Core areas.**
- `GameLibrary` / `XboxLibrary`: Steam and Xbox discovery plus manual games.
- `ForeignInstalls` and `Legacy`: import of mods installed by other tools or by HDLSS5 1.x. 1.x data is read-only.
- `Net`: `HttpService` with cache and in-flight de-duplication; GitHub releases for latest versions.
- `Maintenance`: pruning of journals, blobs and caches at startup.

**App.**
- `MainWindow.xaml` holds the library, the game page (component cards in tabs ReShade / OptiScaler / DLL / Altro) and the DLL page. Dialogs live in `Views/`.
- ViewModels map Core statuses to UI state. Status brushes are looked up by resource key ("Accent", "Warn", "Danger", "Success").
- Strings: Italian and English. App text goes through `Loc` (`i18n/it.json` / `en.json`, XAML `{Binding [key], Source={x:Static app:Loc.Instance}}`, `Loc.T` / `Loc.F`); Core text through `Tx.Get` (`src/Hdlss.Core/Text/it.json` / `en.json`). The language is chosen in Settings: `Loc.Language` also sets `Tx.Selected`. Add every new key to both files (`TxTests` checks Core parity and placeholders). Log lines need no translation.

**Theme and brand.**
- `Themes/Dark.xaml` holds the palette and styles. It includes custom templates, for example a ComboBox template with an editable text box.
- The accent is user-selectable: `Views/AccentTheme` replaces `Accent`, `AccentHover`, `AccentTint`, `OnAccent` and `LogoImage` in `Application.Resources` at startup and live from Settings. Use `{DynamicResource Accent|AccentHover|AccentTint|OnAccent}` for anything that should follow it, never hard-coded accent hex values.
- The sidebar logo is drawn as vectors (`AccentTheme.Mark`). The default accent is the logo cyan `#3DDBFE`. Green (`Success`) means active / up to date; orange (`Warn`) means warnings.
- `app.ico` and `assets/brand-*.png` are the fixed cyan brand images.
