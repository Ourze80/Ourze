using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Hdlss.Core.Transactions;
using Xunit;

namespace Hdlss.Core.Tests;

[Collection("Services")]
public class NrConflictTests : IDisposable
{
    private readonly Fixture _f = new();
    private readonly HttpService _http;
    private readonly Services _services;

    public NrConflictTests()
    {
        _http = new HttpService(_f.Paths, Diagnostics.Log.Null);
        var settings = new AppSettings();
        settings.Profile("g1").ApiOverride = GraphicsApi.DX12;
        _services = new Services(_f.Paths, settings, _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
        File.WriteAllBytes(_f.GameFile("dxgi.dll"), FakePe.Build("ReShade", "6.8.0", "LoadFromDllMain"));
    }

    public void Dispose() { _http.Dispose(); _f.Dispose(); }

    private Game MakeGame()
    {
        var exe = _f.GameFile("Game.exe", "MZ-not-really");
        return new Game { Id = "g1", Name = "Test", Root = _f.Game, Exe = exe, Store = GameStore.Manual };
    }

    private GameContext Context() => new(MakeGame(), _services, new GpuInfo("RTX 4080", "600.0", 40));

    private void Nr(string rel, string version = "12.9.9") => Put(rel, FakePe.Build("DLSS5 NR", version));
    private void Old(string rel) => Put(rel, FakePe.Build("RenoDX", "10.8.0", rel));
    private void Other(string rel) => Put(rel, FakePe.Build("Other", "1.0.0", rel));
    private void Put(string rel, byte[] data)
    {
        var path = _f.GameFile(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static string[] Removed(ChangePlan plan) => plan.Files.Where(f => f.IsDelete).Select(f => f.Path).OrderBy(x => x).ToArray();

    private ChangePlan ApplyFix(GameContext ctx)
    {
        var plan = Compatibility.PlanFix(ctx)!;
        Assert.NotNull(plan);
        _f.Engine.Apply(plan);
        return plan;
    }

    private void Install(string component, Action<PlanBuilder> build)
    {
        var b = _f.Builder(component);
        build(b);
        _f.Engine.Apply(b.Build());
    }

    [Fact]
    public void DragonsDogma_layout_old_NR_in_storage_is_found_and_removed_with_undo()
    {
        Nr("dlss5-nr.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-mfgunlock.addon64,dlss5-nr.addon64\n");
        Other("renodx-mfgunlock.addon64"); Other("renodx-dd2-reenhanced.addon64");
        Put("_storage_/dxgi.dll", FakePe.Build("ReShade", "6.8.0", "LoadFromDllMain"));
        Nr("_storage_/dlss5-nr.addon64", "12.9.0");
        Old("_storage_/renodx-dlss5.addon64");
        Old("_storage_/renodx-dlssfix.addon64");
        Other("_storage_/renodx-mfgunlock.addon64");
        var before = Directory.GetFiles(_f.Game, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);

        var ctx = Context();
        var issue = Assert.Single(NrConflicts.Check(ctx));
        Assert.True(issue.Fixable);
        Assert.Contains("_storage_", issue.Text);
        Assert.Contains(Compatibility.Check(ctx), i => i.Fixable);

        var journal = _f.Engine.Apply(Compatibility.PlanFix(ctx)!);
        Assert.Equal(["_storage_/dlss5-nr.addon64", "_storage_/renodx-dlss5.addon64"], journal.Entries.Select(e => e.Path).OrderBy(x => x).ToArray());
        Assert.True(File.Exists(_f.GameFile("dlss5-nr.addon64")));
        Assert.False(File.Exists(_f.GameFile("_storage_/dlss5-nr.addon64")));
        Assert.False(File.Exists(_f.GameFile("_storage_/renodx-dlss5.addon64")));
        Assert.True(File.Exists(_f.GameFile("_storage_/renodx-dlssfix.addon64")));      // other renodx untouched
        Assert.True(File.Exists(_f.GameFile("_storage_/renodx-mfgunlock.addon64")));
        Assert.True(File.Exists(_f.GameFile("renodx-dd2-reenhanced.addon64")));
        Assert.Empty(NrConflicts.Check(Context()));

        _f.Engine.Undo(journal);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(NrConflicts.Check(Context()));
    }

    [Fact]
    public void Onimusha_layout_old_NR_and_feeder_in_storage_are_removed()
    {
        Nr("dlss5-nr.addon64");
        Put("_storage_/ReShade64.dll", FakePe.Build("ReShade", "6.8.0", "LoadFromDllMain"));
        Old("_storage_/renodx-dlss5.addon64"); Old("_storage_/renodx-dlss.addon64"); Old("_storage_/dlss5-feed.addon64");
        Other("_storage_/dlss5-overlay.addon64");
        Put("_storage_/renodx-dlss5.addon64.old", [1, 2, 3]);

        var ctx = Context();
        var issues = NrConflicts.Check(ctx);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.True(i.Fixable));
        var plan = ApplyFix(ctx);
        Assert.Equal(["_storage_/dlss5-feed.addon64", "_storage_/renodx-dlss.addon64", "_storage_/renodx-dlss5.addon64"], Removed(plan));
        Assert.True(File.Exists(_f.GameFile("_storage_/dlss5-overlay.addon64")));
        Assert.True(File.Exists(_f.GameFile("_storage_/renodx-dlss5.addon64.old")));
        Assert.Empty(NrConflicts.Check(Context()));
    }

    [Fact]
    public void Same_folder_duplicate_keeps_12_and_cleans_early_load_list_with_undo()
    {
        Nr("dlss5-nr.addon64");
        Old("renodx-dlss5.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64,dlss5-nr.addon64,renodx-hdr.addon64\n");
        var iniBefore = _f.Read("ReShade.ini");

        var ctx = Context();
        Assert.Single(NrConflicts.Check(ctx));
        var plan = Compatibility.PlanFix(ctx)!;
        Assert.Equal(["renodx-dlss5.addon64"], Removed(plan));
        var journal = _f.Engine.Apply(plan);
        Assert.False(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
        var ini = _f.Read("ReShade.ini");
        Assert.DoesNotContain("renodx-dlss5", ini);
        Assert.Contains("dlss5-nr.addon64,renodx-hdr.addon64", ini); // unrelated entries stay
        _f.Engine.Undo(journal);
        Assert.True(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
        Assert.Equal(iniBefore, _f.Read("ReShade.ini"));
    }

    [Fact]
    public void Original_folder_wins_over_the_mirror_and_the_newest_12_wins_among_equals()
    {
        Nr("dlss5-nr.addon64", "12.9.0"); Nr("_storage_/dlss5-nr.addon64", "12.9.9");
        Assert.Equal(["_storage_/dlss5-nr.addon64"], Removed(Compatibility.PlanFix(Context())!));
        File.Delete(_f.GameFile("_storage_/dlss5-nr.addon64"));

        Nr("b.addon64", "12.9.9");
        Assert.Equal(["dlss5-nr.addon64"], Removed(Compatibility.PlanFix(Context())!));
    }

    [Fact]
    public void Without_12_the_managed_addon_stays()
    {
        Old("renodx-dlss5.addon64"); Old("renodx-dlss5-shortfuse.addon64");
        Install("renodx-nr", b => b.AddFile("renodx-dlss5-shortfuse.addon64", _f.GameFile("renodx-dlss5-shortfuse.addon64")));
        Assert.Equal(["renodx-dlss5.addon64"], Removed(Compatibility.PlanFix(Context())!));
    }

    [Fact]
    public void Managed_old_addon_is_released_from_its_record()
    {
        File.WriteAllBytes(Path.Combine(_f.Payload, "old.addon64"), FakePe.Build("RenoDX", "10.8.0", "old"));
        Install("renodx-nr", b => b.AddFile("renodx-dlss5.addon64", Path.Combine(_f.Payload, "old.addon64")));
        Assert.NotNull(_f.State().Get("renodx-nr"));
        Nr("dlss5-nr.addon64"); // the user dropped 12 next to it by hand

        var ctx = Context();
        Assert.Single(NrConflicts.Check(ctx));
        var journal = _f.Engine.Apply(Compatibility.PlanFix(ctx)!);
        Assert.False(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
        Assert.True(File.Exists(_f.GameFile("dlss5-nr.addon64")));
        Assert.Null(_f.State().Get("renodx-nr")); // no half-owned record left behind
        Assert.Empty(NrConflicts.Check(Context()));
        _f.Engine.Undo(journal);
        Assert.NotNull(_f.State().Get("renodx-nr"));
        Assert.True(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
    }

    [Fact]
    public void Feeder_owned_by_its_component_is_left_with_a_message()
    {
        Nr("dlss5-nr.addon64");
        Other("dlss5-feed.addon64");
        Install("nr-feeder", b => b.AddFile("dlss5-feed.addon64", _f.GameFile("dlss5-feed.addon64")));
        var issue = Assert.Single(NrConflicts.Check(Context()));
        Assert.True(issue.Fixable);                       // the app runs the Feeder card's removal
        Assert.True(NrConflicts.NeedsFeederRemoval(Context()));
        Assert.Null(Compatibility.PlanFix(Context()));    // the cleanup plan never deletes it by itself
        Assert.True(File.Exists(_f.GameFile("dlss5-feed.addon64")));
    }

    [Fact]
    public void Identical_REFramework_copy_in_storage_is_not_a_conflict_but_a_different_one_is()
    {
        Nr("dlss5-nr.addon64");
        Nr("_storage_/dlss5-nr.addon64");                 // same name, size, version
        Assert.Empty(NrConflicts.Check(Context()));
        Assert.Null(Compatibility.PlanFix(Context()));
        Nr("_storage_/dlss5-nr.addon64", "12.9.1");       // other version (same size)
        Assert.Single(NrConflicts.Check(Context()));
        Assert.Contains("REFramework", NrConflicts.Check(Context())[0].Text);
    }

    [Fact]
    public void Real_Dragons_Dogma_2_has_no_issue()
    {
        const string root = @"E:\SteamLibrary\steamapps\common\Dragons Dogma 2";
        if (!File.Exists(Path.Combine(root, "DD2.exe"))) return;
        var game = new Game { Id = "g1", Name = "DD2", Root = root, Exe = Path.Combine(root, "DD2.exe"), Store = GameStore.Manual };
        Assert.Empty(NrConflicts.Check(new GameContext(game, _services, new GpuInfo("RTX 4080", "600.0", 40))));
    }

    [Fact]
    public void Cleanup_forgets_only_the_matching_ini_edit_and_keeps_records_with_edits()
    {
        File.WriteAllBytes(Path.Combine(_f.Payload, "old.addon64"), FakePe.Build("RenoDX", "10.8.0", "old"));
        Nr("dlss5-nr.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64,dlss5-nr.addon64\n");
        Put("_storage_/dxgi.dll", FakePe.Build("ReShade", "6.8.0", "LoadFromDllMain"));
        _f.GameFile("_storage_/ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64\n");
        Install("renodx-nr", b =>
        {
            b.AddFile("renodx-dlss5.addon64", Path.Combine(_f.Payload, "old.addon64"));
            b.IniListAdd("ReShade.ini", "ADDON", "LoadFromDllMain", "renodx-dlss5.addon64");
            b.IniListAdd("_storage_/ReShade.ini", "ADDON", "LoadFromDllMain", "renodx-dlss5.addon64");
        });
        ApplyFix(Context());
        var record = _f.State().Get("renodx-nr");
        Assert.Null(record);                              // no file and no applied edit left would keep it
        Assert.DoesNotContain("renodx-dlss5", _f.Read("ReShade.ini"));
    }

    [Fact]
    public void Extra_NR_file_is_always_deleted_even_when_it_replaced_an_original()
    {
        Old("renodx-dlss5.addon64");                       // pre-existing: Before = this file
        File.WriteAllBytes(Path.Combine(_f.Payload, "x.bin"), FakePe.Build("RenoDX", "10.8.1", "x"));
        Install("renodx-nr", b => b.AddFile("renodx-dlss5.addon64", Path.Combine(_f.Payload, "x.bin")));
        Nr("dlss5-nr.addon64");
        var plan = Compatibility.PlanFix(Context())!;
        Assert.Equal(["renodx-dlss5.addon64"], Removed(plan));
        ApplyFix(Context());
        Assert.False(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
        Assert.Empty(NrConflicts.Check(Context()));
    }

    [Fact]
    public void Stale_early_load_entries_are_cleaned()
    {
        Nr("dlss5-nr.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=renodx-dlss5.addon64,dlss5-nr.addon64,renodx-gone-hdr.addon64\n");
        var ctx = Context();
        var issue = Assert.Single(NrConflicts.Check(ctx));
        Assert.True(issue.Fixable);
        var plan = ApplyFix(ctx);
        Assert.Empty(Removed(plan));
        Assert.Contains("LoadFromDllMain=dlss5-nr.addon64,renodx-gone-hdr.addon64", _f.Read("ReShade.ini")); // only NR / Feeder names are judged
    }

    [Fact]
    public void No_false_positives()
    {
        Nr("dlss5-nr.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=dlss5-nr.addon64\n");
        Assert.Empty(NrConflicts.Check(Context()));
        Other("_storage_/something.dll"); // storage without ReShade or add-ons
        Assert.Empty(NrConflicts.Check(Context()));
        Old("renodx-dlssfix.addon64"); Other("renodx-hdr.addon64"); Other("renodx-dd2.addon64");
        Put("renodx-dlss5.addon64.old", [1]);
        Put("_storage_/dlss5-nr.addon64.old", [1]);
        Assert.Empty(NrConflicts.Check(Context()));
        Assert.Null(Compatibility.PlanFix(Context()));
        // Feeder without NR 12 is not this check's business
        Other("dlss5-feed.addon64");
        File.Delete(_f.GameFile("dlss5-nr.addon64"));
        _f.GameFile("ReShade.ini", "[ADDON]\nLoadFromDllMain=dlss5-feed.addon64\n");
        Assert.Empty(NrConflicts.Check(Context()));
    }

    [Fact]
    public void AddonPath_folder_counts_and_library_summary_warns()
    {
        Nr("dlss5-nr.addon64");
        Old("addons/renodx-dlss5.addon64");
        _f.GameFile("ReShade.ini", "[ADDON]\nAddonPath=.\\addons\n");
        var plan = Compatibility.PlanFix(Context())!;
        Assert.Equal(["addons/renodx-dlss5.addon64"], Removed(plan));
        Assert.Equal(GameBadge.Warning, GameSummary.Compute(MakeGame(), _services).Badge);
        ApplyFix(Context());
        Assert.NotEqual(GameBadge.Warning, GameSummary.Compute(MakeGame(), _services).Badge);
    }
}
