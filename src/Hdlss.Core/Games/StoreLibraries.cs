using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Microsoft.Win32;

namespace Hdlss.Core.Games;

/// <summary>
/// Epic Games Store discovery: the launcher's per-install manifests (*.item, JSON) and, as a
/// fallback, LauncherInstalled.dat. Read-only; a missing launcher or broken manifest is skipped.
/// </summary>
public static partial class EpicLibrary
{
    [GeneratedRegex(@"^[A-Za-z0-9._\-]+$")] private static partial Regex AppNamePattern();

    /// <summary>Categories that are never a playable game (engine installs, plugins, DLC).</summary>
    private static readonly HashSet<string> NonGameCategories = new(StringComparer.OrdinalIgnoreCase)
        { "engines", "plugins", "addons", "digitalextras", "dlc" };

    public static List<Game> Scan(Log log) => Scan(ManifestFolders(log), LauncherInstalledFile(), log);

    internal static List<Game> Scan(IEnumerable<string> manifestFolders, string? launcherInstalled, Log log)
    {
        var games = new List<Game>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in manifestFolders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in ExecutableDetector.SafeFiles(folder, "*.item"))
            {
                try
                {
                    var game = ParseManifest(File.ReadAllText(file));
                    if (game != null && roots.Add(game.Root)) games.Add(game);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Manifest Epic non leggibile {Path.GetFileName(file)}", ex);
                }
            }
        if (launcherInstalled != null && File.Exists(launcherInstalled))
        {
            try
            {
                foreach (var game in ParseLauncherInstalled(File.ReadAllText(launcherInstalled)))
                    if (games.All(g => g.Id != game.Id) && roots.Add(game.Root)) games.Add(game);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn("Epic: LauncherInstalled.dat non leggibile", ex);
            }
        }
        return games;
    }

    /// <summary>One *.item manifest → game, or null for DLC, engines, partial installs, missing folders or bad JSON.</summary>
    internal static Game? ParseManifest(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) return null;
            if (Bool(m, "bIsIncompleteInstall")) return null;
            var appName = Str(m, "AppName");
            var main = Str(m, "MainGameAppName");
            if (appName.Length == 0 || (main.Length > 0 && !main.Equals(appName, StringComparison.OrdinalIgnoreCase))) return null;
            if (m.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            {
                var list = cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList();
                if (!list.Contains("games", StringComparer.OrdinalIgnoreCase) && list.Any(NonGameCategories.Contains)) return null;
            }
            var root = FullDir(Str(m, "InstallLocation"));
            if (root == null) return null;
            var name = Str(m, "DisplayName");
            var game = new Game
            {
                Id = "epic-" + appName,
                Name = name.Length > 0 ? name : Path.GetFileName(root),
                Root = root,
                Store = GameStore.Epic,
                LaunchId = AppNamePattern().IsMatch(appName) ? appName : null,
            };
            game.DeclaredExe = DeclaredExe(root, Str(m, "LaunchExecutable"));
            return game;
        }
    }

    /// <summary>LauncherInstalled.dat: {"InstallationList":[{"InstallLocation","AppName",...}]}. No display names.</summary>
    internal static List<Game> ParseLauncherInstalled(string json)
    {
        var result = new List<Game>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var appName = Str(item, "AppName");
                if (appName.Length == 0 || appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue; // Unreal Engine installs
                var root = FullDir(Str(item, "InstallLocation"));
                if (root == null) continue;
                result.Add(new Game
                {
                    Id = "epic-" + appName, Name = Path.GetFileName(root), Root = root, Store = GameStore.Epic,
                    LaunchId = AppNamePattern().IsMatch(appName) ? appName : null,
                });
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>com.epicgames.launcher URI that starts the game through the launcher (needed for Epic ownership checks).</summary>
    public static string? LaunchUri(Game game) =>
        game.Store == GameStore.Epic && game.LaunchId is { } id && AppNamePattern().IsMatch(id)
            ? $"com.epicgames.launcher://apps/{id}?action=launch&silent=true" : null;

    private static IEnumerable<string> ManifestFolders(Log log)
    {
        var result = new List<string>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                       .OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
            if (key?.GetValue("AppDataPath") is string data && data.Length > 0) result.Add(Path.Combine(data, "Manifests"));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            log.Warn("Epic: registro del launcher non leggibile", ex);
        }
        result.Add(Path.Combine(ProgramData(), "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        return result;
    }

    private static string LauncherInstalledFile() =>
        Path.Combine(ProgramData(), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    private static string ProgramData() => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Full path of an existing install folder, or null (empty, relative, invalid or missing).</summary>
    internal static string? FullDir(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The launcher-declared executable (relative or absolute), when it is an existing file inside the install.</summary>
    internal static string? DeclaredExe(string root, string exe)
    {
        if (exe.Length == 0) return null;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(exe) ? exe : Path.Combine(root, exe));
            return PathGuard.IsUnder(full, root) && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}

/// <summary>
/// GOG discovery (GOG Galaxy and offline installers): HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt;
/// with gameName, path and exe. DLC entries (dependsOn) are skipped. Read-only.
/// </summary>
public static partial class GogLibrary
{
    [GeneratedRegex(@"^[0-9]+$")] private static partial Regex IdPattern();

    public static List<Game> Scan(Log log)
    {
        var games = new List<Game>();
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                       .OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (key == null) return games;
            foreach (var id in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(id);
                    if (sub == null) continue;
                    var game = Parse(id, n => sub.GetValue(n) as string);
                    if (game != null && games.All(g => g.Id != game.Id && !g.Root.Equals(game.Root, StringComparison.OrdinalIgnoreCase)))
                        games.Add(game);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    log.Warn($"GOG: voce di registro {id} non leggibile", ex);
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn("GOG: registro non leggibile", ex);
        }
        return games;
    }

    /// <summary>One registry entry (values read through <paramref name="value"/>) → game, or null for DLC / missing folders.</summary>
    internal static Game? Parse(string keyName, Func<string, string?> value)
    {
        var id = (value("gameID") ?? "").Trim();
        if (!IdPattern().IsMatch(id)) id = keyName.Trim();
        if (!IdPattern().IsMatch(id)) return null;
        if (!string.IsNullOrWhiteSpace(value("dependsOn"))) return null; // DLC of another game
        var root = EpicLibrary.FullDir((value("path") ?? "").Trim());
        if (root == null) return null;
        var name = (value("gameName") ?? value("startMenu") ?? "").Trim();
        var exe = (value("exe") ?? "").Trim();
        if (exe.Length == 0 && value("exeFile") is { Length: > 0 } file) exe = file.Trim();
        return new Game
        {
            Id = "gog-" + id,
            Name = name.Length > 0 ? name : Path.GetFileName(root),
            Root = root,
            Store = GameStore.Gog,
            DeclaredExe = EpicLibrary.DeclaredExe(root, exe),
        };
    }
}
