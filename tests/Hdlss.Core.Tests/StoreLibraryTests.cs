using Xunit;
using System.Text.Json;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Settings;

namespace Hdlss.Core.Tests;

public sealed class StoreLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdlss-tests", Guid.NewGuid().ToString("N"));

    public StoreLibraryTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(string rel) { var p = Path.Combine(_root, rel); Directory.CreateDirectory(p); return p; }
    private static string Exe(string path, string name = "Game") { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, FakePe.Build(name, "1.0.0.0")); return path; }

    private static string Manifest(string appName, string location, string display = "Cool Game", string exe = "Game.exe",
                                   string? main = null, bool incomplete = false, string[]? categories = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["FormatVersion"] = 0, ["bIsIncompleteInstall"] = incomplete, ["LaunchExecutable"] = exe,
            ["DisplayName"] = display, ["InstallLocation"] = location, ["AppName"] = appName,
            ["MainGameAppName"] = main ?? appName, ["AppCategories"] = categories ?? ["public", "games", "applications"],
        });

    [Fact]
    public void Epic_manifest_gives_game_with_declared_exe()
    {
        var root = Dir("Epic/CoolGame");
        var exe = Exe(Path.Combine(root, "Bin", "Game.exe"));
        var game = EpicLibrary.ParseManifest(Manifest("Pelican", root, exe: "Bin/Game.exe"));
        Assert.NotNull(game);
        Assert.Equal("epic-Pelican", game.Id);
        Assert.Equal("Cool Game", game.Name);
        Assert.Equal(GameStore.Epic, game.Store);
        Assert.Equal("EPIC", game.StoreLabel);
        Assert.Equal(Path.GetFullPath(root), game.Root);
        Assert.Equal(Path.GetFullPath(exe), game.DeclaredExe);
        Assert.Equal("com.epicgames.launcher://apps/Pelican?action=launch&silent=true", EpicLibrary.LaunchUri(game));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"AppName\":\"x\"}")]                       // no install location
    [InlineData("{\"AppName\":\"x\",\"InstallLocation\":\"relative\\\\path\"}")]
    [InlineData("{\"AppName\":\"x\",\"InstallLocation\":\"C:\\\\does-not-exist-hdlss\\\\x\"}")]
    [InlineData("{\"AppName\":42,\"InstallLocation\":null}")]
    public void Epic_broken_manifests_are_skipped(string json) => Assert.Null(EpicLibrary.ParseManifest(json));

    [Fact]
    public void Epic_skips_dlc_engines_and_partial_installs()
    {
        var root = Dir("Epic/Base");
        Assert.Null(EpicLibrary.ParseManifest(Manifest("Dlc1", root, main: "Base")));
        Assert.Null(EpicLibrary.ParseManifest(Manifest("Base", root, incomplete: true)));
        Assert.Null(EpicLibrary.ParseManifest(Manifest("UE_5.4", root, categories: ["engines"])));
        Assert.NotNull(EpicLibrary.ParseManifest(Manifest("Base", root)));
    }

    [Fact]
    public void Epic_exe_outside_install_is_ignored()
    {
        var root = Dir("Epic/Escape");
        Exe(Path.Combine(_root, "Epic", "evil.exe"));
        var game = EpicLibrary.ParseManifest(Manifest("Escape", root, exe: "../evil.exe"));
        Assert.NotNull(game);
        Assert.Null(game.DeclaredExe);
    }

    [Fact]
    public void Epic_scan_reads_folder_survives_bad_files_and_uses_launcher_fallback()
    {
        var manifests = Dir("Manifests");
        var a = Dir("Games/A");
        var b = Dir("Games/B Folder");
        File.WriteAllText(Path.Combine(manifests, "1.item"), Manifest("AppA", a, "Game A"));
        File.WriteAllText(Path.Combine(manifests, "2.item"), "garbage {");
        File.WriteAllText(Path.Combine(manifests, "3.item"), Manifest("DlcA", a, main: "AppA"));
        var dat = Path.Combine(_root, "LauncherInstalled.dat");
        File.WriteAllText(dat, JsonSerializer.Serialize(new
        {
            InstallationList = new object[]
            {
                new { InstallLocation = a, AppName = "AppA" },             // already from manifest
                new { InstallLocation = b, AppName = "AppB" },             // only in the fallback
                new { InstallLocation = a, AppName = "UE_5.3" },           // engine
            },
        }));

        var games = EpicLibrary.Scan([manifests, Path.Combine(_root, "missing")], dat, Log.Null);

        Assert.Equal(["B Folder", "Game A"], games.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("epic-AppB", games.Single(g => g.Name == "B Folder").Id);
    }

    [Fact]
    public void Epic_scan_with_no_launcher_returns_nothing() =>
        Assert.Empty(EpicLibrary.Scan([Path.Combine(_root, "nope")], Path.Combine(_root, "nope.dat"), Log.Null));

    [Fact]
    public void Epic_bad_launcher_installed_returns_nothing() =>
        Assert.Empty(EpicLibrary.ParseLauncherInstalled("{\"InstallationList\": 5}"));

    private static Func<string, string?> Reg(Dictionary<string, string> values) => n => values.GetValueOrDefault(n);

    [Fact]
    public void Gog_registry_entry_gives_game_with_exe()
    {
        var root = Dir("GOG Games/Witcher");
        var exe = Exe(Path.Combine(root, "bin", "x64", "witcher.exe"));
        var game = GogLibrary.Parse("1207664663", Reg(new()
        {
            ["gameID"] = "1207664663", ["gameName"] = "The Witcher", ["path"] = root + "\\", ["exe"] = exe,
        }));
        Assert.NotNull(game);
        Assert.Equal("gog-1207664663", game.Id);
        Assert.Equal("The Witcher", game.Name);
        Assert.Equal(GameStore.Gog, game.Store);
        Assert.Equal("GOG", game.StoreLabel);
        Assert.Equal(Path.GetFullPath(root).TrimEnd('\\'), game.Root);
        Assert.Equal(Path.GetFullPath(exe), game.DeclaredExe);
        Assert.Null(EpicLibrary.LaunchUri(game));
    }

    [Fact]
    public void Gog_relative_exefile_and_folder_name_fallback()
    {
        var root = Dir("GOG Games/Some Game");
        var exe = Exe(Path.Combine(root, "game.exe"));
        var game = GogLibrary.Parse("42", Reg(new() { ["path"] = root, ["exeFile"] = "game.exe" }));
        Assert.NotNull(game);
        Assert.Equal("gog-42", game.Id);
        Assert.Equal("Some Game", game.Name);
        Assert.Equal(Path.GetFullPath(exe), game.DeclaredExe);
    }

    [Fact]
    public void Gog_skips_dlc_missing_folder_and_bad_ids()
    {
        var root = Dir("GOG Games/X");
        Assert.Null(GogLibrary.Parse("1", Reg(new() { ["path"] = root, ["dependsOn"] = "2" })));
        Assert.Null(GogLibrary.Parse("1", Reg(new() { ["path"] = Path.Combine(_root, "gone") })));
        Assert.Null(GogLibrary.Parse("1", Reg(new())));
        Assert.Null(GogLibrary.Parse("not-a-number", Reg(new() { ["path"] = root })));
    }

    [Fact]
    public void Resolve_uses_declared_exe_when_detection_is_a_guess()
    {
        var root = Dir("GOG Games/Two");
        var real = Exe(Path.Combine(root, "Real.exe"), "Real");
        Exe(Path.Combine(root, "Other.exe"), "Other");
        var game = GogLibrary.Parse("7", Reg(new() { ["path"] = root, ["exe"] = real }))!;
        new GameLibrary(new AppSettings(), Log.Null).Resolve(game);
        Assert.Equal(Path.GetFullPath(real), game.Exe);
        Assert.DoesNotContain(game.Warnings, w => w.Length > 0);
    }

    [Fact]
    public void Resolve_without_declared_exe_still_detects()
    {
        var root = Dir("Epic/One");
        var only = Exe(Path.Combine(root, "Only.exe"));
        var game = EpicLibrary.ParseManifest(Manifest("One", root, exe: "missing.exe"))!;
        Assert.Null(game.DeclaredExe);
        new GameLibrary(new AppSettings(), Log.Null).Resolve(game);
        Assert.Equal(Path.GetFullPath(only), game.Exe);
    }
}
