using System.IO.Compression;
using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Hdlss.Core.Transactions;
using Xunit;

namespace Hdlss.Core.Tests;

[Collection("Services")]
public class NrAddonTests : IDisposable
{
    private const string RealFolder = @"D:\Win11\Dlss5\DLSS5 NR Add-on v12.9.9";
    private readonly Fixture _f = new();
    private readonly HttpService _http;
    private readonly Services _services;

    public NrAddonTests()
    {
        _http = new HttpService(_f.Paths, Diagnostics.Log.Null);
        var settings = new AppSettings();
        settings.Profile("g1").ApiOverride = GraphicsApi.DX12;
        _services = new Services(_f.Paths, settings, _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
    }

    public void Dispose() { _http.Dispose(); _f.Dispose(); }

    private GameContext Context()
    {
        var exe = _f.GameFile("Game.exe", "MZ-not-really");
        var game = new Game { Id = "g1", Name = "Test", Root = _f.Game, Exe = exe, Store = GameStore.Manual };
        return new GameContext(game, _services, new GpuInfo("RTX 4080", "600.0", 40));
    }

    /// <summary>A complete fake DLSS5 NR package (the paid files are never in the repo).</summary>
    private string Package(string version, string? folder = null, bool host = true, string product = "DLSS5 NR")
    {
        folder ??= Path.Combine(_f.Root, "pkg-" + version);
        var host64 = Path.Combine(folder, "dlss5-nr-host");
        Directory.CreateDirectory(host64);
        var addon = FakePe.Build(product, version);
        File.WriteAllBytes(Path.Combine(folder, "dlss5-nr.addon64"), addon);
        File.WriteAllBytes(Path.Combine(folder, "dlss5-nr.addon32"), FakePe.Build(product, version));
        File.WriteAllText(Path.Combine(folder, "INSTALL.txt"), "how to");
        if (host)
        {
            File.WriteAllBytes(Path.Combine(host64, "dlss5-nr.addon64"), addon);
            File.WriteAllText(Path.Combine(host64, "ReShade64.dll"), "host-reshade");
            File.WriteAllText(Path.Combine(host64, "nr-host64.exe"), "host-exe");
        }
        return folder;
    }

    private string FakeReShade(string version)
    {
        var path = Path.Combine(_f.Game, "dxgi.dll");
        File.WriteAllBytes(path, FakePe.Build("ReShade", version, "LoadFromDllMain"));
        return path;
    }

    private void Install(string component, Action<PlanBuilder> build, string version = "1")
    {
        var b = _f.Builder(component, version);
        build(b);
        _f.Engine.Apply(b.Build());
    }

    // ---- recognition

    [Fact]
    public void Identify_reads_the_product_and_version_not_the_name()
    {
        var path = Path.Combine(_f.Payload, "whatever-name.addon64");
        File.WriteAllBytes(path, FakePe.Build("DLSS5 NR", "12.9.9"));
        Assert.Equal("Lecram 12.9.9", NrAddonInfo.Identify(path));
        File.WriteAllBytes(path, FakePe.Build("RenoDX", "1.0.0"));
        Assert.Null(NrAddonInfo.Identify(path));
        Assert.Null(NrAddonInfo.Identify(Path.Combine(_f.Payload, "missing.addon64")));
        File.WriteAllText(path, "not a pe");
        Assert.Null(NrAddonInfo.Identify(path));
    }

    [Fact]
    public void Scan_treats_a_renamed_NR_addon_as_NR()
    {
        File.WriteAllBytes(_f.GameFile("my-addon.addon64"), FakePe.Build("DLSS5 NR", "12.9.9"));
        var scan = Context().Scan;
        Assert.Contains("my-addon.addon64", scan.NrAddons());
    }

    [Fact]
    public void Real_package_is_recognized_when_present()
    {
        if (!Directory.Exists(RealFolder)) return;   // opt-in: the paid add-on is not part of the repo
        Assert.Equal("Lecram 12.9.9", NrAddonInfo.Identify(Path.Combine(RealFolder, "dlss5-nr.addon64")));
        var package = _services.NrAddons.Import(RealFolder);
        Assert.Equal("Lecram 12.9.9", package.Label);
        Assert.Equal(new Version(12, 9, 9), package.Version);
    }

    // ---- import

    [Fact]
    public void Import_folder_keeps_a_private_copy_and_a_second_import_is_a_noop()
    {
        var source = Package("12.9.9");
        var package = _services.NrAddons.Import(source);
        Assert.Equal("Lecram 12.9.9", package.Label);
        Assert.StartsWith(Path.Combine(_f.Paths.Data, "nr-addons"), package.Folder);
        Assert.True(File.Exists(Path.Combine(package.Folder, "dlss5-nr-host", "nr-host64.exe")));
        Assert.True(File.Exists(Path.Combine(package.Folder, "dlss5-nr.addon32")));

        var again = _services.NrAddons.Import(source);
        Assert.Equal(package.Folder, again.Folder);
        Assert.Single(_services.NrAddons.Packages);
    }

    [Fact]
    public void Newest_is_the_highest_version()
    {
        _services.NrAddons.Import(Package("12.9.9"));
        _services.NrAddons.Import(Package("12.9.8"));
        _services.NrAddons.Import(Package("12.10.0"));
        Assert.Equal(["12.10.0", "12.9.9", "12.9.8"], _services.NrAddons.Packages.Select(p => p.Version.ToString()));
        Assert.Equal("Lecram 12.10.0", _services.NrAddons.Newest!.Label);
    }

    [Fact]
    public void Import_zip_with_a_top_folder()
    {
        var top = Path.Combine(_f.Root, "zipsrc");
        Package("12.9.9", Path.Combine(top, "DLSS5 NR Add-on v12.9.9"));
        var zip = Path.Combine(_f.Root, "nr.zip");
        ZipFile.CreateFromDirectory(top, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        var package = _services.NrAddons.Import(zip);
        Assert.Equal("Lecram 12.9.9", package.Label);
        Assert.True(File.Exists(Path.Combine(package.Folder, "dlss5-nr-host", "ReShade64.dll")));
        Assert.Empty(Directory.EnumerateDirectories(_f.Paths.Temp));   // extraction folder cleaned up
    }

    [Fact]
    public void Import_zip_with_files_at_the_root()
    {
        var src = Package("12.9.9", Path.Combine(_f.Root, "flat"));
        var zip = Path.Combine(_f.Root, "flat.zip");
        ZipFile.CreateFromDirectory(src, zip);
        Assert.Equal("Lecram 12.9.9", _services.NrAddons.Import(zip).Label);
    }

    [Fact]
    public void Import_without_the_host_folder_is_refused()
    {
        var source = Package("12.9.9", host: false);
        var ex = Assert.Throws<HdlssException>(() => _services.NrAddons.Import(source));
        Assert.Contains("dlss5-nr-host", ex.Message);
        Assert.Empty(_services.NrAddons.Packages);
    }

    [Fact]
    public void Import_of_another_product_is_refused()
    {
        var source = Package("1.0.0", product: "Something Else");
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(source));
        Assert.Empty(_services.NrAddons.Packages);
    }

    [Fact]
    public void Import_of_junk_is_refused()
    {
        var empty = Path.Combine(_f.Root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(empty));
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(Path.Combine(_f.Root, "nope")));
        var txt = Path.Combine(_f.Root, "a.txt");
        File.WriteAllText(txt, "x");
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(txt));
        var bad = Path.Combine(_f.Root, "bad.zip");
        File.WriteAllText(bad, "not a zip");
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(bad));
        // a 32-bit-only / non-PE add-on named like the real one
        var fake = Path.Combine(_f.Root, "fake");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "dlss5-nr.addon64"), "text");
        Assert.Throws<HdlssException>(() => _services.NrAddons.Import(fake));
    }

    // ---- options

    [Fact]
    public void Variant_option_offers_the_newest_imported()
    {
        _services.NrAddons.Import(Package("12.9.9"));
        var variants = ComponentCatalog.Get(RenoDxNrComponent.ComponentId).Options.Single(o => o.Key == "variant").Choices;
        Assert.Contains(variants, c => c.Value == "lecram12" && c.Label == "Lecram 12.9.9");
        Assert.Contains(variants, c => c.Value == "lecram");
        Assert.Contains(variants, c => c.Value == "shortfuse");
    }

    // ---- plan

    [Fact]
    public void Install_12_writes_the_package_and_replaces_the_old_addon()
    {
        _services.NrAddons.Import(Package("12.9.9"));
        FakeReShade("6.8.0");
        // 10.8 installed earlier by HDLSS under its own name
        Install(RenoDxNrComponent.ComponentId, b =>
        {
            b.AddFile("renodx-dlss5.addon64", _f.PayloadFile("old", "renodx-10.8"));
            b.IniListAdd("ReShade.ini", "ADDON", "LoadFromDllMain", "renodx-dlss5.addon64");
        }, "Lecram 10.8 · model");

        var ctx = Context();
        var model = _f.PayloadFile("nvngx_dlssnr.dll", "model-bytes");
        var plan = ((RenoDxNrComponent)ComponentCatalog.Get(RenoDxNrComponent.ComponentId)).Plan12(ctx, model, NrModels.Rtx40);

        var paths = plan.Files.Select(f => f.Path).ToList();
        Assert.Contains("dlss5-nr.addon64", paths);
        Assert.Contains("dlss5-nr.addon32", paths);
        Assert.Contains("dlss5-nr-host/ReShade64.dll", paths);
        Assert.Contains("dlss5-nr-host/dlss5-nr.addon64", paths);
        Assert.Contains("dlss5-nr-host/nr-host64.exe", paths);
        Assert.Contains("nvngx_dlssnr.dll", paths);
        var old = Assert.Single(plan.Files, f => f.Path == "renodx-dlss5.addon64");
        Assert.True(old.Release);
        Assert.True(old.IsDelete);
        Assert.StartsWith("Lecram 12.9.9", plan.Version);

        _f.Engine.Apply(plan);
        Assert.False(File.Exists(Path.Combine(_f.Game, "renodx-dlss5.addon64")));
        Assert.Equal("Lecram 12.9.9", NrAddonInfo.Identify(Path.Combine(_f.Game, "dlss5-nr.addon64")));
        Assert.Equal("host-exe", _f.Read("dlss5-nr-host/nr-host64.exe"));
        var list = IniDocument.Load(Path.Combine(_f.Game, "ReShade.ini")).Get("ADDON", "LoadFromDllMain");
        Assert.Contains("dlss5-nr.addon64", list);
        Assert.DoesNotContain("renodx-dlss5.addon64", list);

        // removing it leaves the game as it was (only ReShade is left)
        _f.Engine.Apply(_f.Removal(RenoDxNrComponent.ComponentId));
        Assert.False(File.Exists(Path.Combine(_f.Game, "dlss5-nr.addon64")));
        Assert.False(Directory.Exists(Path.Combine(_f.Game, "dlss5-nr-host")) && Directory.EnumerateFileSystemEntries(Path.Combine(_f.Game, "dlss5-nr-host")).Any());
    }

    [Fact]
    public async Task Plan_12_without_an_import_is_refused()
    {
        FakeReShade("6.8.0");
        var comp = ComponentCatalog.Get(RenoDxNrComponent.ComponentId);
        await Assert.ThrowsAsync<HdlssException>(() => comp.PlanInstallAsync(Context(), new Dictionary<string, string> { ["variant"] = "lecram12" }, null, default));
    }

    [Fact]
    public async Task Legacy_variants_keep_the_DX12_Vulkan_rule()
    {
        var comp = ComponentCatalog.Get(RenoDxNrComponent.ComponentId);
        var settings = new AppSettings();
        settings.Profile("g1").ApiOverride = GraphicsApi.DX11;
        var services = new Services(_f.Paths, settings, _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
        var exe = _f.GameFile("Game.exe", "MZ");
        var ctx = new GameContext(new Game { Id = "g1", Name = "T", Root = _f.Game, Exe = exe, Store = GameStore.Manual }, services, new GpuInfo("RTX", "600.0", 40));
        await Assert.ThrowsAsync<BlockedException>(() => comp.PlanInstallAsync(ctx, new Dictionary<string, string> { ["variant"] = "lecram" }, null, default));
        Assert.Contains(comp.Blockers(ctx), b => b.Contains("DirectX 12", StringComparison.OrdinalIgnoreCase) || b.Contains("DX12", StringComparison.OrdinalIgnoreCase));
        services.NrAddons.Import(Package("12.9.9"));
        Assert.DoesNotContain(comp.Blockers(ctx), b => b.Contains("DirectX 12", StringComparison.OrdinalIgnoreCase) || b.Contains("DX12", StringComparison.OrdinalIgnoreCase));
    }

    // ---- update

    [Fact]
    public async Task Update_verdict_compares_installed_with_newest_imported()
    {
        var comp = ComponentCatalog.Get(RenoDxNrComponent.ComponentId);
        var options = new Dictionary<string, string> { ["variant"] = "lecram12" };
        var ctx = Context();
        Assert.Null(await comp.LatestVersionAsync(ctx, options, default));           // nothing imported: no claim

        _services.NrAddons.Import(Package("12.9.9"));
        var latest = await comp.LatestVersionAsync(ctx, options, default);
        Assert.StartsWith("Lecram 12.9.9 · ", latest);
        var model = latest![(latest.IndexOf(" · ", StringComparison.Ordinal) + 3)..];
        Assert.Equal(VersionVerdict.Newer, VersionCompare.Compare("Lecram 12.9.8 · " + model, latest));
        Assert.Equal(VersionVerdict.UpToDate, VersionCompare.Compare("Lecram 12.9.9 · " + model, latest));
        Assert.Equal(VersionVerdict.UpToDate, VersionCompare.Compare("Lecram 12.10.0 · " + model, latest));
        Assert.Equal("12.9.8 → 12.9.9", "12.9.8 → 12.9.9"); // the card shows "<present> → <latest>"; the numbers above drive it
    }

    // ---- rules

    [Fact]
    public void Feeder_installed_blocks_12()
    {
        FakeReShade("6.8.0");
        Install(FeederComponent.ComponentId, b => b.AddFile("dlss5-feed.addon64", _f.PayloadFile("feed", "feeder")));
        var blockers = RenoDxNrComponent.Lecram12Blockers(Context());
        Assert.Single(blockers);
        Assert.Contains("Feeder", blockers[0]);
    }

    [Fact]
    public void A_foreign_feed_addon_also_blocks_12()
    {
        FakeReShade("6.8.0");
        _f.GameFile("dlss5-feed.addon64", "feeder-by-hand");
        Assert.Single(RenoDxNrComponent.Lecram12Blockers(Context()));
    }

    [Fact]
    public void Old_ReShade_blocks_12_and_6_8_is_enough()
    {
        FakeReShade("6.7.1");
        Assert.Contains(RenoDxNrComponent.Lecram12Blockers(Context()), b => b.Contains("6.8"));
        FakeReShade("6.8.0");
        Assert.Empty(RenoDxNrComponent.Lecram12Blockers(Context()));
        FakeReShade("6.9.2");
        Assert.Empty(RenoDxNrComponent.Lecram12Blockers(Context()));
    }

    // ---- adoption of a manual replacement

    [Fact]
    public void Manual_replacement_is_adopted_and_removal_still_restores_the_original()
    {
        _services.NrAddons.Import(Package("12.9.9"));
        FakeReShade("6.8.0");
        _f.GameFile("renodx-dlss5.addon64", "pre-hdlss-original");
        Install(RenoDxNrComponent.ComponentId, b => b.AddFile("renodx-dlss5.addon64", _f.PayloadFile("old", "renodx-10.8")),
            "Lecram 10.8 · 310.9.16.0 · RTX 4000");

        // the user copies DLSS5 NR 12.9.9 over HDLSS's file
        File.WriteAllBytes(_f.GameFile("renodx-dlss5.addon64"), FakePe.Build("DLSS5 NR", "12.9.9"));
        var after = FileHash.OfFile(_f.GameFile("renodx-dlss5.addon64"));

        var ctx = Context();                                  // adoption happens while loading the state
        var record = ctx.State.Get(RenoDxNrComponent.ComponentId)!;
        Assert.Equal(after, record.FindFile("renodx-dlss5.addon64")!.After);
        Assert.StartsWith("Lecram 12.9.9 · ", record.Version);
        Assert.Equal("lecram12", record.Meta["variant"]);
        Assert.Equal(after, _f.State().Get(RenoDxNrComponent.ComponentId)!.FindFile("renodx-dlss5.addon64")!.After);   // persisted
        Assert.Equal("pre-hdlss-original", Assert.IsType<string>(ReadStore(record.FindFile("renodx-dlss5.addon64")!.Before!)));
        Assert.Null(((ComponentBase)ComponentCatalog.Get(RenoDxNrComponent.ComponentId)).TakenOverBy(ctx));

        // removal does not complain and puts the pre-HDLSS file back
        var removal = _f.Removal(RenoDxNrComponent.ComponentId);
        _f.Engine.Apply(removal);
        Assert.Equal("pre-hdlss-original", _f.Read("renodx-dlss5.addon64"));
    }

    [Fact]
    public void Replacing_with_something_else_is_still_modified_by_another_program()
    {
        _f.GameFile("renodx-dlss5.addon64", "orig");
        Install(RenoDxNrComponent.ComponentId, b => b.AddFile("renodx-dlss5.addon64", _f.PayloadFile("old", "renodx-10.8")));
        File.WriteAllText(_f.GameFile("renodx-dlss5.addon64"), "some other build");
        Context();
        Assert.Throws<ModifiedByOthersException>(() => _f.Removal(RenoDxNrComponent.ComponentId));
    }

    // ---- fix round 1

    private GameContext Dx11Context(out Services services)
    {
        var settings = new AppSettings();
        settings.Profile("g1").ApiOverride = GraphicsApi.DX11;
        services = new Services(_f.Paths, settings, _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
        var exe = _f.GameFile("Game.exe", "MZ");
        return new GameContext(new Game { Id = "g1", Name = "T", Root = _f.Game, Exe = exe, Store = GameStore.Manual }, services, new GpuInfo("RTX", "600.0", 40));
    }

    [Fact]
    public void Default_variant_is_12_only_when_one_is_imported()
    {
        var comp = ComponentCatalog.Get(RenoDxNrComponent.ComponentId);
        Assert.Equal("lecram", comp.Options.Single().Default);
        _services.NrAddons.Import(Package("12.9.9"));
        Assert.Equal("lecram12", comp.Options.Single().Default);
    }

    [Fact]
    public void Adoption_without_import_still_records_12_and_the_dropdown_can_show_it()
    {
        FakeReShade("6.8.0");
        _f.GameFile("renodx-dlss5.addon64", "orig");
        Install(RenoDxNrComponent.ComponentId, b => b.AddFile("renodx-dlss5.addon64", _f.PayloadFile("old", "renodx-10.8")),
            "Lecram 10.8 · 310.9.16.0 · RTX 4000");
        File.WriteAllBytes(_f.GameFile("renodx-dlss5.addon64"), FakePe.Build("DLSS5 NR", "12.9.8"));
        Context();
        Assert.Equal("lecram12", _f.State().Get(RenoDxNrComponent.ComponentId)!.Meta["variant"]);
        var option = ComponentCatalog.Get(RenoDxNrComponent.ComponentId).Options.Single();
        Assert.Contains(option.Choices, c => c.Value == "lecram12" && c.Label == "Lecram 12.9.8");
        Assert.Equal("lecram", option.Default);   // nothing imported: default unchanged, stored Meta wins
    }

    [Fact]
    public async Task Legacy_variant_on_DX11_points_to_Lecram_12_when_imported()
    {
        var ctx = Dx11Context(out var services);
        var comp = ComponentCatalog.Get(RenoDxNrComponent.ComponentId);
        var plain = await Assert.ThrowsAsync<BlockedException>(() => comp.PlanInstallAsync(ctx, new Dictionary<string, string> { ["variant"] = "lecram" }, null, default));
        Assert.DoesNotContain("Lecram 12.x", plain.Message);
        services.NrAddons.Import(Package("12.9.9"));
        var withImport = await Assert.ThrowsAsync<BlockedException>(() => comp.PlanInstallAsync(ctx, new Dictionary<string, string> { ["variant"] = "shortfuse" }, null, default));
        Assert.Contains("Lecram 12.x", withImport.Message);
        Assert.DoesNotContain("Feeder", withImport.Message);
    }

    [Fact]
    public void Advisor_recommends_RenoDX_on_DX11_without_DLSS_when_12_is_imported()
    {
        var ctx = Dx11Context(out var services);
        Assert.Equal(NrPath.Feeder, NrAdvisor.Recommend(ctx).Path);
        services.NrAddons.Import(Package("12.9.9"));
        Assert.Equal(NrPath.Native, NrAdvisor.Recommend(ctx).Path);
    }

    [Fact]
    public void Import_accepts_the_host_folder_and_picks_its_parent()
    {
        var source = Package("12.9.9");
        var package = _services.NrAddons.Import(Path.Combine(source, "dlss5-nr-host"));
        Assert.Equal("Lecram 12.9.9", package.Label);
        Assert.True(File.Exists(Path.Combine(package.Folder, "dlss5-nr.addon32")));
        Assert.Equal(package.Folder, _services.NrAddons.Import(Path.Combine(source, "dlss5-nr-host") + Path.DirectorySeparatorChar).Folder);
    }

    [Fact]
    public void A_12_copied_by_hand_is_imported_whole_and_recorded_as_lecram12()
    {
        FakeReShade("6.8.0");
        Package("12.9.9", _f.Game);   // add-ons and host folder straight in the game folder, nothing imported in the app
        var ctx = Context();
        var found = ForeignInstalls.Detect(ctx, new HashSet<string>());
        var nr = Assert.Single(found, f => f.ComponentId == RenoDxNrComponent.ComponentId);
        var paths = nr.Files.Select(f => f.Path).ToList();
        Assert.Contains("dlss5-nr.addon64", paths);
        Assert.Contains("dlss5-nr.addon32", paths);
        Assert.Contains("dlss5-nr-host/nr-host64.exe", paths);
        Assert.Contains("dlss5-nr-host/ReShade64.dll", paths);
        Assert.DoesNotContain(ForeignInstalls.Unknown(ctx, found, []), u => u.Path.Contains("dlss5-nr", StringComparison.OrdinalIgnoreCase));

        _f.Engine.Apply(ForeignInstalls.PlanImport(ctx, nr));
        var record = _f.State().Get(RenoDxNrComponent.ComponentId)!;
        Assert.Equal("lecram12", record.Meta["variant"]);
        Assert.StartsWith("Lecram 12.9.9", record.Version);
        Assert.True(File.Exists(Path.Combine(_f.Game, "dlss5-nr.addon64")));   // import writes no game file
        Assert.DoesNotContain(ForeignInstalls.Detect(Context(), new HashSet<string>()), f => f.ComponentId == RenoDxNrComponent.ComponentId);
    }

    private string ReadStore(string hash) => File.ReadAllText(Directory.EnumerateFiles(_f.Paths.Store, "*", SearchOption.AllDirectories)
        .First(p => Path.GetFileName(p).StartsWith(hash[..16], StringComparison.OrdinalIgnoreCase) || p.Contains(hash, StringComparison.OrdinalIgnoreCase)));
}
