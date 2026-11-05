using System.Text;
using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Hdlss.Core.Transactions;
using Xunit;

namespace Hdlss.Core.Tests;

public class CompatibilityTests : IDisposable
{
    private readonly Fixture _f = new();
    private readonly HttpService _http;
    private readonly Services _services;

    public CompatibilityTests()
    {
        _http = new HttpService(_f.Paths, Diagnostics.Log.Null);
        var settings = new AppSettings();
        settings.Profile("g1").ApiOverride = GraphicsApi.DX12;
        _services = new Services(_f.Paths, settings, _f.Store, _f.States, _f.Engine, _http, Diagnostics.Log.Null);
    }

    public void Dispose() { _http.Dispose(); _f.Dispose(); }

    private GameContext Context(GpuInfo? gpu = null)
    {
        var exe = _f.GameFile("Game.exe", "MZ-not-really");
        var game = new Game { Id = "g1", Name = "Test", Root = _f.Game, Exe = exe, Store = GameStore.Manual };
        return new GameContext(game, _services, gpu ?? GpuInfo.Unknown);
    }

    /// <summary>Smallest file FolderScan recognizes as an x64 OptiScaler proxy.</summary>
    private string FakeOptiScaler()
    {
        var bytes = new byte[256];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(64).CopyTo(bytes, 60);
        "PE\0\0"u8.ToArray().CopyTo(bytes, 64);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 68);
        Encoding.ASCII.GetBytes("OptiScaler.ini").CopyTo(bytes, 128);
        var path = Path.Combine(_f.Payload, "opti.dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void Install(string component, Action<PlanBuilder> build)
    {
        var b = _f.Builder(component);
        build(b);
        _f.Engine.Apply(b.Build());
    }

    [Fact]
    public void ReShade_moves_under_OptiScaler_and_back_without_leftovers()
    {
        Install(ReShadeComponent.ComponentId, b => b.AddFile("dxgi.dll", _f.PayloadFile("rs", "reshade-bytes")));
        Install(OptiScalerComponent.ComponentId, b =>
        {
            b.Meta("build", "official");
            b.AddFile("winmm.dll", FakeOptiScaler());
            b.AddBytes("OptiScaler.ini", Encoding.UTF8.GetBytes("[Plugins]\r\nLoadReshade=false\r\n"), keepIfModified: true);
        });

        var ctx = Context();
        Assert.Equal("winmm.dll", ctx.Scan.OptiScalerProxy);
        Assert.Contains(Compatibility.Check(ctx), i => i.Fixable);          // two separate hooks
        _f.Engine.Apply(Compatibility.PlanFix(ctx)!);

        Assert.False(File.Exists(Path.Combine(_f.Game, "dxgi.dll")));
        Assert.Equal("reshade-bytes", _f.Read("ReShade64.dll"));
        Assert.Equal("true", IniDocument.Load(Path.Combine(_f.Game, "OptiScaler.ini")).Get("Plugins", "LoadReshade"));
        ctx.Refresh();
        Assert.Empty(Compatibility.Check(ctx));

        // OptiScaler goes: its ini (edited by ReShade) is still recognized as its own and removed.
        _f.Engine.Apply(_f.Removal(OptiScalerComponent.ComponentId));
        Assert.False(File.Exists(Path.Combine(_f.Game, "OptiScaler.ini")));
        Assert.False(File.Exists(Path.Combine(_f.Game, "winmm.dll")));

        ctx.Refresh();
        Assert.Contains(Compatibility.Check(ctx), i => i.Fixable);          // ReShade64.dll with no loader
        _f.Engine.Apply(Compatibility.PlanFix(ctx)!);
        Assert.Equal("reshade-bytes", _f.Read("dxgi.dll"));
        Assert.False(File.Exists(Path.Combine(_f.Game, "ReShade64.dll")));

        _f.Engine.Apply(_f.Removal(ReShadeComponent.ComponentId));
        Assert.Equal(new[] { "Game.exe" }, Directory.GetFiles(_f.Game).Select(f => Path.GetFileName(f)).ToArray());
    }

    [Fact]
    public void OptiScaler_standard_is_not_a_neural_rendering_path()
    {
        ComponentRecord Opti(string? build)
        {
            var r = new ComponentRecord { Id = OptiScalerComponent.ComponentId };
            if (build != null) r.Meta["build"] = build;
            return r;
        }
        Assert.False(NrRules.DoesNr(Opti("official"), OptiScalerComponent.ComponentId));
        Assert.True(NrRules.DoesNr(Opti("f5"), OptiScalerComponent.ComponentId));
        Assert.True(NrRules.DoesNr(Opti(null), OptiScalerComponent.ComponentId));   // imported: safe side

        Install(OptiScalerComponent.ComponentId, b => { b.Meta("build", "official"); b.AddFile("winmm.dll", FakeOptiScaler()); });
        var ctx = Context();
        Assert.Null(NrAdvisor.Installed(ctx));
        var blockers = new List<string>();
        NrRules.CheckSingleInjector(ctx, RenoDxNrComponent.ComponentId, blockers);
        Assert.Empty(blockers);                                             // RenoDX NR can sit next to it
    }

    [Fact]
    public void RenoDx_NR_takes_over_a_standalone_model_instead_of_blocking()
    {
        Install(NrModelComponent.ComponentId, b => b.AddFile("nvngx_dlssnr.dll", _f.PayloadFile("m", "model")));
        var ctx = Context();
        Assert.Equal(NrModelComponent.ComponentId, NrRules.ModelToRelease(ctx, RenoDxNrComponent.ComponentId));
        Assert.Equal(NrModelComponent.ComponentId, NrRules.ModelToRelease(ctx, FeederComponent.ComponentId));
        Assert.Null(NrRules.ModelToRelease(ctx, OptiScalerComponent.ComponentId));
        Assert.DoesNotContain(ComponentCatalog.Get(RenoDxNrComponent.ComponentId).Blockers(ctx), b => b.Contains("modello", StringComparison.OrdinalIgnoreCase));

        _f.Engine.Apply(_f.Removal(NrModelComponent.ComponentId));               // what the app does first
        ctx.Refresh();
        Assert.Null(NrRules.ModelToRelease(ctx, RenoDxNrComponent.ComponentId));
        Assert.False(File.Exists(Path.Combine(_f.Game, "nvngx_dlssnr.dll")));
    }

    [Fact]
    public async Task Latest_version_uses_the_same_label_as_the_installed_record()
    {
        var ctx = Context(new GpuInfo("NVIDIA GeForce RTX 5080", "620.10", 50));
        var model = ComponentCatalog.Get(NrModelComponent.ComponentId);
        Assert.Equal(NrModels.ForGpu(ctx.Gpu).Label, await model.LatestVersionAsync(ctx, new Dictionary<string, string>(), default));
        var reno = await ComponentCatalog.Get(RenoDxNrComponent.ComponentId).LatestVersionAsync(ctx, new Dictionary<string, string> { ["variant"] = "shortfuse" }, default);
        Assert.Equal($"{RenoDxNrComponent.VariantLabel(RenoDxNrComponent.ShortFuseHash)} · {NrModels.ForGpu(ctx.Gpu).Label}", reno);
        Assert.Null(await ComponentCatalog.Get(UnrealHdrComponent.ComponentId).LatestVersionAsync(ctx, new Dictionary<string, string>(), default));
    }

    [Fact]
    public async Task Update_check_reads_the_files_when_the_recorded_version_is_unknown()
    {
        var gpu = new GpuInfo("NVIDIA GeForce RTX 4090", "620.10", 40);
        var model = ComponentCatalog.Get(NrModelComponent.ComponentId);
        Assert.Null(await model.CheckUpdateAsync(Context(gpu), new Dictionary<string, string>(), default));   // nothing there

        var b = _f.Builder(NrModelComponent.ComponentId, "da 1.x");
        b.AddFile("nvngx_dlssnr.dll", _f.PayloadFile("m", "not-a-real-model"));
        _f.Engine.Apply(b.Build());
        var check = await model.CheckUpdateAsync(Context(gpu), new Dictionary<string, string>(), default);
        Assert.Equal(VersionVerdict.Unknown, check!.Verdict);                 // unreadable file: offer the update
        Assert.Equal(NrModels.ForGpu(gpu).Label, check.Latest);
    }

    [Fact]
    public void Two_neural_rendering_passes_are_reported()
    {
        _f.GameFile("winmm.dll", "");
        File.Copy(FakeOptiScaler(), Path.Combine(_f.Game, "winmm.dll"), overwrite: true);
        _f.GameFile("OptiScaler.ini", "[DlssNr]\r\nEnabled=true\r\n");
        _f.GameFile("renodx-dlss5.addon64", "addon");
        var issue = Assert.Single(Compatibility.Check(Context()));
        Assert.False(issue.Fixable);
        Assert.Contains("due passaggi NR", issue.Text);
    }
}
