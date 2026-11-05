using System.Text;
using Hdlss.Core.Components;
using Hdlss.Core.IO;
using Hdlss.Core.Legacy;
using Hdlss.Core.Transactions;
using Xunit;

namespace Hdlss.Core.Tests;

public class PortedModuleTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private static string H(string s) => FileHash.OfBytes(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Ini_edits_in_an_external_root_are_applied_and_reverted()
    {
        var config = Path.Combine(_f.Game, "Proj", "Saved", "Config", "Windows");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "Engine.ini"), "[SystemSettings]\r\nr.Foo=1\r\n");
        var b = _f.Builder("unreal-hdr");
        b.AddRoot("cfg", config, _f.Game);
        b.IniSet("@cfg/Engine.ini", "SystemSettings", "r.HDR.EnableHDROutput", "1");
        b.IniSet("@cfg/GameUserSettings.ini", "/Script/Engine.GameUserSettings", "bUseHDRDisplayOutput", "True");
        _f.Engine.Apply(b.Build());

        Assert.Equal("1", IniDocument.Load(Path.Combine(config, "Engine.ini")).Get("SystemSettings", "r.HDR.EnableHDROutput"));
        Assert.Equal(config, _f.State().Get("unreal-hdr")!.Roots["cfg"]);

        _f.Engine.Apply(_f.Removal("unreal-hdr"));
        var engine = IniDocument.Load(Path.Combine(config, "Engine.ini"));
        Assert.Null(engine.Get("SystemSettings", "r.HDR.EnableHDROutput"));
        Assert.Equal("1", engine.Get("SystemSettings", "r.Foo"));
    }

    [Fact]
    public void External_roots_outside_game_and_localappdata_are_refused()
    {
        var b = _f.Builder();
        Assert.Throws<HdlssException>(() => b.AddRoot("cfg", @"C:\Windows\System32", _f.Game));
        Assert.Throws<HdlssException>(() => b.AddRoot("cfg", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _f.Game));
    }

    [Fact]
    public void The_game_root_itself_is_an_allowed_root()
    {
        // Regression: DLL/Streamline updates register the game install as "@root" (failed on every game).
        _f.GameFile("Engine/Plugins/DLSS/nvngx_dlss.dll", "old");
        var b = _f.Builder("dlss-update");
        b.AddRoot("root", _f.Game, _f.Game);
        b.AddFile("@root/Engine/Plugins/DLSS/nvngx_dlss.dll", _f.PayloadFile("n", "new"));
        _f.Engine.Apply(b.Build());
        Assert.Equal("new", _f.Read("Engine/Plugins/DLSS/nvngx_dlss.dll"));
        _f.Engine.Apply(_f.Removal("dlss-update"));
        Assert.Equal("old", _f.Read("Engine/Plugins/DLSS/nvngx_dlss.dll"));
    }

    [Fact]
    public void Adopting_a_1x_install_keeps_files_and_allows_clean_removal()
    {
        var legacyData = Path.Combine(_f.Root, "old-data");
        var backup = Path.Combine(legacyData, "mfg-backups", "x", "renodx-mfgunlock.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old-addon");
        _f.GameFile("renodx-mfgunlock.addon64", "new-addon");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-mfgunlock.addon64\n");
        var marker = $$"""
            {"Schema":1,"Folder":{{System.Text.Json.JsonSerializer.Serialize(_f.Game)}},"Release":"1.8.0",
             "Files":[{"Name":"renodx-mfgunlock.addon64","Before":"{{H("old-addon")}}","After":"{{H("new-addon")}}","Backup":"mfg-backups/x/renodx-mfgunlock.addon64","Attributes":128},
                      {"Name":"ReShade.ini","Before":null,"After":"whatever","Backup":"mfg-backups/x/ReShade.ini","Attributes":128}]}
            """;
        _f.GameFile("hdlss-mfg-unlock.json", marker);

        var install = Assert.Single(LegacyInstalls.Find(_f.Game, Diagnostics.Log.Null));
        Assert.Equal("mfg-unlock", install.AdoptableAs);
        var plan = LegacyInstalls.PlanAdopt(install, "mfg-unlock", "MFG Unlock", legacyData, _f.Store, _f.State(), _f.States.Stamp(_f.Game), "Test");
        _f.Engine.Apply(plan);

        Assert.Equal("new-addon", _f.Read("renodx-mfgunlock.addon64"));        // game files untouched
        Assert.False(File.Exists(_f.GameFile("hdlss-mfg-unlock.json")));        // marker gone
        var record = _f.State().Get("mfg-unlock")!;
        Assert.Single(record.Files);                                             // INI not owned

        _f.Engine.Apply(_f.Removal("mfg-unlock"));
        Assert.Equal("old-addon", _f.Read("renodx-mfgunlock.addon64"));
    }

    [Fact]
    public void RenoDx_catalog_matches_exact_title_only()
    {
        var md = """
            | Game | Author | Download | Notes |
            |---|---|---|---|
            | Control™ Resonant | someone | [x64](https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-control.addon64) | Working |
            | Control Demo | someone | [x64](https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-controldemo.addon64) | |
            | Other Game | someone | [Nexus](https://www.nexusmods.com/othergame/mods/12) | |
            """;
        var mod = RenoDxHdrComponent.Resolve(md, "CONTROL Resonant");
        Assert.EndsWith("renodx-control.addon64", mod.Url);
        Assert.True(RenoDxHdrComponent.Official(new Uri(mod.Url)));
        var nexus = Assert.Throws<HdlssException>(() => RenoDxHdrComponent.Resolve(md, "Other Game"));
        Assert.Contains("nexusmods.com", nexus.Message);
        Assert.Throws<HdlssException>(() => RenoDxHdrComponent.Resolve(md, "Control"));
    }

    [Theory]
    [InlineData("https://evil.example.com/renodx-x.addon64")]
    [InlineData("https://github.com/a/b/releases/download/t/other.dll")]
    [InlineData("http://github.com/a/b/releases/download/t/renodx-x.addon64")]
    public void RenoDx_rejects_untrusted_sources(string url) =>
        Assert.Throws<HdlssException>(() => RenoDxHdrComponent.ValidateSource(url));

    [Fact]
    public void Reads_png_dimensions_from_header()
    {
        var png = Path.Combine(_f.Payload, "x.png");
        var bytes = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 1, 44, 0, 0, 1, 194 }.CopyTo(bytes, 0);
        File.WriteAllBytes(png, bytes);
        Assert.Equal((300, 450), Games.XboxLibrary.ImageSize(png));
    }

    [Fact]
    public void Every_catalog_component_has_a_unique_id_and_category_name()
    {
        var ids = ComponentCatalog.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var c in ComponentCatalog.All) Assert.False(string.IsNullOrWhiteSpace(ComponentCatalog.CategoryName(c.Category)));
    }
}
