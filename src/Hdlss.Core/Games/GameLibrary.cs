using Hdlss.Core.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Settings;
using Microsoft.Win32;

namespace Hdlss.Core.Games;

/// <summary>Steam libraries plus manually added executables.</summary>
public sealed partial class GameLibrary(AppSettings settings, Log log)
{
    [GeneratedRegex("\"path\"\\s+\"((?:\\\\.|[^\"])*)\"")] private static partial Regex VdfPath();

    private static readonly HashSet<string> IgnoredAppIds = ["228980", "1070560", "1391110", "1628350", "1493710"]; // redist & runtimes

    public static string SteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
    }

    public List<string> SteamLibraries()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steam = SteamRoot();
        if (Directory.Exists(steam)) result.Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(p)) result.Add(Path.GetFullPath(p));
            }
        foreach (var p in settings.ExtraSteamLibraries.Where(Directory.Exists)) result.Add(p);
        return [.. result];
    }

    public List<Game> Scan(CancellationToken cancel = default)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>();
        var steam = SteamRoot();
        foreach (var lib in SteamLibraries())
        {
            foreach (var manifest in ExecutableDetector.SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var text = File.ReadAllText(manifest);
                    var id = Field(text, "appid");
                    var dir = Field(text, "installdir");
                    if (!Regex.IsMatch(id, "^[0-9]+$") || IgnoredAppIds.Contains(id) || !seen.Add(id) || dir.Length == 0) continue;
                    var common = Path.Combine(lib, "steamapps", "common");
                    var root = Path.GetFullPath(Path.Combine(common, dir));
                    if (!PathGuard.IsUnder(root, common) || !Directory.Exists(root)) continue;
                    var game = new Game { Id = id, Name = Field(text, "name"), Root = root, Store = GameStore.Steam };
                    game.Cover = Artwork(steam, id, "library_600x900.jpg") ?? Artwork(steam, id, "library_capsule.jpg");
                    game.Hero = Artwork(steam, id, "library_hero.jpg") ?? Artwork(steam, id, "library_header.jpg");
                    games.Add(game);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Manifest Steam non leggibile {Path.GetFileName(manifest)}", ex);
                }
            }
        }
        try { games.AddRange(XboxLibrary.Scan(settings.ExtraXboxLibraries, log).Where(x => !seen.Contains(x.Id))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn("Libreria Xbox", ex); }
        foreach (var entry in settings.ManualGames)
        {
            try { games.Add(Manual(entry.Exe, entry.Name)); }
            catch (HdlssException ex) { log.Warn($"Gioco manuale {entry.Name}: {ex.Message}"); }
        }
        return games.Where(g => !settings.HiddenGames.Contains(g.Id))
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Resolves the executable (saved choice → auto-detection) lazily, since it is slow.</summary>
    /// <summary>
    /// True when HDLSS already manages something in that folder. Set by the app: an automatic mod
    /// folder must never hide components installed in the executable's folder.
    /// </summary>
    public Func<string, bool>? HasManagedState { get; set; }

    public void Resolve(Game game)
    {
        var profile = settings.Profile(game.Id);
        if (game.HasExe && game.Store == GameStore.Manual) { ResolveModFolder(game, profile); DetectEngine(game); return; }
        if (game.ProtectedReason != null) return; // WindowsApps: not readable, nothing to resolve
        Func<string, bool> accept = f => PeFile.IsPe64(f) || XboxLibrary.IsDeclaredX64(game, f);
        if (!string.IsNullOrEmpty(profile.Target))
        {
            try
            {
                var full = PathGuard.Resolve(game.Root, profile.Target);
                if (File.Exists(full) && accept(full))
                {
                    game.Exe = full;
                    MigrateToolTarget(game, profile, accept);
                }
                else game.Warnings.Add(Tx.Get("game.warn.exeMissing"));
            }
            catch (UnsafePathException ex) { game.Warnings.Add(ex.Message); }
        }
        if (!game.HasExe)
        {
            var (exe, certain) = ExecutableDetector.Pick(game.Root, accept);
            game.Exe = exe;
            if (exe.Length == 0) game.Warnings.Add(Tx.Get("game.warn.noExe"));
            else if (!certain) game.Warnings.Add(Tx.Get("game.warn.exeBySize"));
        }
        if (game.HasExe)
        {
            ResolveModFolder(game, profile);
            DetectEngine(game);
        }
    }

    /// <summary>
    /// 1.x/early 2.0 workaround: a helper tool (e.g. 8za_v2.exe) picked only because it sits in the
    /// folder the game loads DLLs from. When the real game exe resolves to that same mod folder,
    /// switch to it silently: same folder, same managed state, working launch. Otherwise the user's
    /// choice is kept as is.
    /// </summary>
    private void MigrateToolTarget(Game game, GameProfile profile, Func<string, bool> accept)
    {
        if (!ExecutableDetector.IsServiceExecutable(game.Exe)) return;
        var (pick, _) = ExecutableDetector.Pick(game.Root, accept);
        if (pick.Length == 0 || string.Equals(pick, game.Exe, StringComparison.OrdinalIgnoreCase)) return;
        var toolFolder = Path.GetDirectoryName(game.Exe)!;
        var pickFolder = ExecutableDetector.ModFolderFor(game.Root, pick) ?? Path.GetDirectoryName(pick)!;
        if (!string.Equals(Path.GetFullPath(pickFolder), Path.GetFullPath(toolFolder), StringComparison.OrdinalIgnoreCase)) return;
        log.Info($"{game.Name}: eseguibile {Path.GetFileName(game.Exe)} (strumento) sostituito da {Path.GetFileName(pick)}, stessa cartella delle mod.");
        game.Exe = pick;
        profile.Target = PathGuard.RelativeTo(game.Root, pick);
    }

    /// <summary>Saved mod folder (relative to Root) → Unreal Binaries\Win64 detection → exe folder.</summary>
    private void ResolveModFolder(Game game, GameProfile profile)
    {
        game.ModFolder = null; game.ModFolderAuto = false;
        if (!string.IsNullOrEmpty(profile.ModFolder))
        {
            try
            {
                var full = profile.ModFolder == "." ? Path.GetFullPath(game.Root) : PathGuard.Resolve(game.Root, profile.ModFolder);
                // Saved when the root was still the exe folder (bin layouts): the same relative path from there.
                if (!Directory.Exists(full) && profile.ModFolder != "." && Directory.Exists(PathGuard.Resolve(game.ExeFolder, profile.ModFolder)))
                    full = PathGuard.Resolve(game.ExeFolder, profile.ModFolder);
                if (Directory.Exists(full)) { game.ModFolder = full; return; }
                game.Warnings.Add(Tx.Get("game.warn.modFolderMissing"));
            }
            catch (UnsafePathException ex) { game.Warnings.Add(ex.Message); }
        }
        if (ExecutableDetector.ModFolderFor(game.Root, game.Exe) is not { } auto)
        {
            // Earlier versions redirected such games to <Project>\Binaries\Win64 even when the exe
            // outside was the game itself. Keep managing what was installed there, so it can be removed.
            if (ExecutableDetector.UnrealBinaries(game.Root) is { } old && !Path.GetDirectoryName(Path.GetFullPath(game.Exe))!.Equals(old, StringComparison.OrdinalIgnoreCase)
                && HasManagedState?.Invoke(old) == true && HasManagedState?.Invoke(game.ExeFolder) != true)
            {
                game.ModFolder = old; game.ModFolderAuto = true;
                game.Warnings.Add(Tx.Get("game.warn.modsWrongFolder", PathGuard.RelativeTo(game.Root, old)));
            }
            return;
        }
        if (HasManagedState?.Invoke(game.ExeFolder) == true)
        {
            game.Warnings.Add(Tx.Get("game.warn.wrongModFolder", PathGuard.RelativeTo(game.Root, auto)));
            return;
        }
        game.ModFolder = auto; game.ModFolderAuto = true;
    }

    private static void DetectEngine(Game game)
    {
        var binariesLayout = game.Folder.EndsWith(@"\Binaries\Win64", StringComparison.OrdinalIgnoreCase) || game.Exe.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase);
        // Game exe outside Binaries (Phantom Blade Zero: platform\steam\PBZ.exe) in an install
        // with an Engine folder and one <Project>\Content\Paks: still an Unreal layout.
        var project = binariesLayout || !Directory.Exists(Path.Combine(game.Root, "Engine")) ? null : ExecutableDetector.UnrealProject(game.Root);
        game.Engine = ExecutableDetector.DetectEngine(game.Exe, binariesLayout || project != null);
        if (game.Engine == EngineKind.Unknown) return;
        var bin = Directory.GetParent(game.Folder);
        if (bin?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true) game.Project = bin.Parent?.Name;
        else if (project != null) game.Project = Path.GetFileName(project);
    }

    /// <summary>
    /// Unreal "Saved/Config/&lt;platform&gt;" folder holding GameUserSettings.ini: under
    /// %LOCALAPPDATA%\&lt;Project&gt; or inside the install. Null unless exactly one match.
    /// </summary>
    public string? UnrealConfigFolder(Game game)
    {
        if (settings.Profile(game.Id).ConfigFolder is { } saved && File.Exists(Path.Combine(saved, "GameUserSettings.ini"))) return saved;
        if (string.IsNullOrEmpty(game.Project)) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { game.Project, game.Project + "_Demo", game.Project + "Demo", Path.GetFileName(game.Root) };
        var platforms = new[] { "Windows", "WindowsNoEditor", "WindowsClient", "WinGDK" };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
            foreach (var p in platforms)
            {
                var dir = Path.Combine(local, name, "Saved", "Config", p);
                if (File.Exists(Path.Combine(dir, "GameUserSettings.ini"))) matches.Add(dir);
            }
        foreach (var p in platforms)
        {
            var dir = Path.Combine(game.Root, game.Project, "Saved", "Config", p);
            if (File.Exists(Path.Combine(dir, "GameUserSettings.ini"))) matches.Add(dir);
        }
        return matches.Count == 1 ? matches.First() : null;
    }

    public static Game Manual(string exe, string? name = null)
    {
        var path = Path.GetFullPath(exe);
        if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new HdlssException(Tx.Get("game.chooseExe"));
        if (!PeFile.IsPe64(path)) throw new HdlssException(Tx.Get("game.notX64"));
        var id = "manual-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..20].ToLowerInvariant();
        return new Game
        {
            Id = id, Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name,
            Root = ManualRoot(path), Exe = path, Store = GameStore.Manual,
            ProtectedReason = path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? Tx.Get("game.windowsApps") : null,
        };
    }

    /// <summary>For Unreal layouts (…/Project/Binaries/Win64/x.exe) the root is the install folder.</summary>
    private static readonly HashSet<string> BinFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "bin64", "x64", "binaries" };

    public static string ManualRoot(string exe)
    {
        var local = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        // <game>/bin/game.exe with the game's own DLLs beside "bin" (e.g. <game>/ngx): the game is the folder above.
        // Never a drive root, where the folder above would hold other games.
        if (BinFolders.Contains(local.Name) && local.Parent is { Parent: not null } above) return above.FullName;
        if (local.Name is not ("Win64" or "WinGDK") || local.Parent?.Name != "Binaries" || local.Parent.Parent == null) return local.FullName;
        var project = local.Parent.Parent;
        var install = project.Parent;
        if (install != null && Directory.Exists(Path.Combine(install.FullName, "Engine"))) return install.FullName;
        return project.FullName;
    }

    private static string Field(string text, string name)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(name) + "\"\\s+\"((?:\\\\.|[^\"])*)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"") : "";
    }

    private static string? Artwork(string steam, string id, string name)
    {
        var root = Path.Combine(steam, "appcache", "librarycache");
        var legacy = Path.Combine(root, $"{id}_{name}");
        if (File.Exists(legacy)) return legacy;
        var per = Path.Combine(root, id);
        var direct = Path.Combine(per, name);
        if (File.Exists(direct)) return direct;
        return ExecutableDetector.SafeDirs(per).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }
}
