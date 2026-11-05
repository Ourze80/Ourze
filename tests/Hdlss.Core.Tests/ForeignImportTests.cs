using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Xunit;

namespace Hdlss.Core.Tests;

public class ForeignImportTests : IDisposable
{
    private readonly Fixture _f = new();
    private readonly HttpService _http;
    private readonly Services _services;

    public ForeignImportTests()
    {
        _http = new HttpService(_f.Paths, Diagnostics.Log.Null);
        _services = new Services(_f.Paths, new AppSettings(), _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
    }

    public void Dispose() { _http.Dispose(); _f.Dispose(); }

    private GameContext Context()
    {
        var exe = _f.GameFile("Game.exe", "MZ-not-really");
        var game = new Game { Id = "g1", Name = "Test", Root = _f.Game, Exe = exe, Store = GameStore.Manual };
        return new GameContext(game, _services, GpuInfo.Unknown);
    }

    [Fact]
    public void Detects_and_imports_foreign_addons_restoring_backups_on_removal()
    {
        _f.GameFile("renodx-mfgunlock.addon64", "mfg-by-autopilot");
        _f.GameFile("renodx-mfgunlock.addon64.dlss5-autopilot-backup", "mfg-original");
        _f.GameFile("renodx-mygame.addon64", "hdr-addon");
        _f.GameFile("nvngx_dlss.dll", "game-dlss");            // the game's own: never imported
        _f.GameFile("dlss5-overlay.addon64", "unknown-tool");    // not a known component

        var ctx = Context();
        var found = ForeignInstalls.Detect(ctx, new HashSet<string>());
        var mfg = Assert.Single(found, f => f.ComponentId == MfgUnlockComponent.ComponentId);
        Assert.NotNull(mfg.Files[0].Backup);
        var hdr = Assert.Single(found, f => f.ComponentId == RenoDxHdrComponent.ComponentId);
        Assert.DoesNotContain(found.SelectMany(f => f.Files), f => f.Path == "nvngx_dlss.dll");
        Assert.Contains(ForeignInstalls.Unknown(ctx, found, []), u => u.Path == "dlss5-overlay.addon64");

        _f.Engine.Apply(ForeignInstalls.PlanImport(ctx, mfg));
        ctx.Refresh();
        _f.Engine.Apply(ForeignInstalls.PlanImport(ctx, hdr));
        Assert.Equal("mfg-by-autopilot", _f.Read("renodx-mfgunlock.addon64"));   // import writes nothing
        Assert.NotNull(_f.State().Get(MfgUnlockComponent.ComponentId));

        _f.Engine.Apply(_f.Removal(MfgUnlockComponent.ComponentId));
        Assert.Equal("mfg-original", _f.Read("renodx-mfgunlock.addon64"));       // original from the other tool's backup
        _f.Engine.Apply(_f.Removal(RenoDxHdrComponent.ComponentId));
        Assert.False(File.Exists(_f.GameFile("renodx-mygame.addon64")));         // no backup: added file removed
        Assert.Equal("game-dlss", _f.Read("nvngx_dlss.dll"));                     // untouched
    }

    [Fact]
    public void Already_managed_or_skipped_components_are_not_offered()
    {
        _f.GameFile("renodx-mfgunlock.addon64", "mfg");
        var ctx = Context();
        Assert.Empty(ForeignInstalls.Detect(ctx, new HashSet<string> { MfgUnlockComponent.ComponentId }));
    }

    [Fact]
    public void Finds_originals_in_dlss5_backup_folder()
    {
        _f.GameFile("nvngx_dlssnr.dll", "nr-by-tool");
        _f.GameFile("_DLSS5_Backup/originals/abc/nvngx_dlssnr.dll", "nr-original");
        var ctx = Context();
        var model = Assert.Single(ForeignInstalls.Detect(ctx, new HashSet<string>()), f => f.ComponentId == NrModelComponent.ComponentId);
        Assert.EndsWith(Path.Combine("abc", "nvngx_dlssnr.dll"), model.Files[0].Backup);
    }
}
