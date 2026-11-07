using Hdlss.Core.Text;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>Rules shared by every Neural Rendering path: only one NR injector per game.</summary>
public static class NrRules
{
    public static readonly string[] Injectors = [RenoDxNrComponent.ComponentId, FeederComponent.ComponentId, OptiScalerComponent.ComponentId];

    public static void CheckSingleInjector(GameContext ctx, string self, List<string> blockers)
    {
        foreach (var other in Injectors.Where(i => i != self && DoesNr(ctx, i)))
            blockers.Add(Tx.Get("nrr.otherInstalled", ComponentCatalog.NameOf(other)));
        var owned = ctx.State.Components.Values.SelectMany(c => c.Files).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in ctx.Scan.NrAddons().Where(a => !owned.Contains(a)))
            blockers.Add(Tx.Get("nrr.foreignAddon", addon));
    }

    /// <summary>
    /// True when the component is installed and runs a Neural Rendering pass. OptiScaler Standard
    /// is only an upscaler: it can sit next to ReShade + RenoDX NR without a second NR pass.
    /// </summary>
    public static bool DoesNr(GameContext ctx, string id) =>
        ctx.State.Get(id) is { } r && (id != OptiScalerComponent.ComponentId || OptiScalerComponent.IsNrBuild(r));

    public static bool DoesNr(Transactions.ComponentRecord? r, string id) =>
        r != null && (id != OptiScalerComponent.ComponentId || OptiScalerComponent.IsNrBuild(r));

    /// <summary>
    /// The standalone "Modello NR" is only a model file: RenoDX NR and Feeder ship the same model,
    /// so they take it over (remove the standalone one, then install with the model included).
    /// </summary>
    public static bool CanTakeOverModel(string? owner) => owner == NrModelComponent.ComponentId;

    /// <summary>Component that must be removed before <paramref name="componentId"/> installs, or null.</summary>
    public static string? ModelToRelease(GameContext ctx, string componentId) =>
        componentId is RenoDxNrComponent.ComponentId or FeederComponent.ComponentId
        && CanTakeOverModel(ctx.State.OwnerOf("nvngx_dlssnr.dll")) ? NrModelComponent.ComponentId : null;

    public static void CheckGpu(GameContext ctx, List<string> blockers)
    {
        if (!ctx.Gpu.SupportsNr) blockers.Add(Tx.Get("nrr.noGpu"));
        else if (ctx.Gpu.DriverTooOld) blockers.Add(Tx.Get("nrr.oldDriver", ctx.Gpu.Driver, GpuInfo.MinimumDriver));
    }

    public static string ModelFile(GameContext ctx, out NrModel model)
    {
        model = NrModels.ForGpu(ctx.Gpu);
        return NrModels.Locate(model, ctx.Services.Paths.NrPackageCandidates(ctx.Services.Settings.LegacyAppDir));
    }

    /// <summary>Path (relative to the game folder) where ReShade looks for add-ons.</summary>
    public static string AddonPath(GameContext ctx, string fileName)
    {
        if (ctx.Scan.AddonFolder == "?") throw new BlockedException(AddonPathOutside(ctx));
        return ctx.Scan.AddonFolder.Length == 0 ? fileName : ctx.Scan.AddonFolder + "/" + fileName;
    }

    public static string AddonPathOutside(GameContext ctx) =>
        Tx.Get("nrr.addonOutside", ctx.Scan.AddonPathValue, ctx.Scan.ReShadeConfig);
}

/// <summary>Only the NR model DLL, for loaders that bring their own NR pass (OptiScaler F5).</summary>
public sealed class NrModelComponent : ComponentBase
{
    public const string ComponentId = "nr-model";
    public override string Id => ComponentId;
    public override string Name => Tx.Get("nrm.name");
    public override string Description => Tx.Get("nrm.desc");
    public override ComponentCategory Category => ComponentCategory.Upscaling;

    public override Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        Task.FromResult<string?>(NrModels.ForGpu(ctx.Gpu).Label);

    public override bool ChecksGameFiles => true;

    protected override string? PresentVersion(GameContext ctx)
    {
        var file = PathGuard.Resolve(ctx.Folder, "nvngx_dlssnr.dll");
        if (!File.Exists(file)) return null;
        if (NrModels.Identify(file) is { } known) return known.Label;
        var v = PeFile.FileVersion(file);
        return v == null || v.Major == 0 ? null : v.ToString();
    }

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var file = PathGuard.Resolve(ctx.Folder, "nvngx_dlssnr.dll");
        if (!File.Exists(file)) return null;
        var owner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
        if (owner != null) return new ComponentStatus(ComponentStatusKind.External, Tx.Get("nrm.status.included", ComponentCatalog.NameOf(owner)));
        var model = NrModels.Identify(file);
        return new ComponentStatus(ComponentStatusKind.External, model != null ? Tx.Get("nrm.status.present", model.Label) : Tx.Get("nrm.status.presentVer", PeFile.FileVersion(file)));
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        NrRules.CheckGpu(ctx, blockers);
        var owner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
        if (owner != null && owner != Id) blockers.Add(Tx.Get("nrm.block.together", ComponentCatalog.NameOf(owner)));
    }

    public override Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var file = NrRules.ModelFile(ctx, out var model);
        var plan = ctx.NewPlan(this, model.Label);
        plan.AddFile("nvngx_dlssnr.dll", file);
        return Task.FromResult(plan.Build());
    }
}

/// <summary>
/// RenoDX NR add-on for ReShade on games with native DLSS (DX12, or native Vulkan DLSS).
/// Installs the add-on, the NR model for the GPU and registers early loading.
/// </summary>
public sealed class RenoDxNrComponent : ComponentBase
{
    public const string ComponentId = "renodx-nr";
    public const string AddonName = "renodx-dlss5.addon64";
    public const string LecramHash = "79871879324053d49931a9f8c1865fcb83e8146df4a56b146d7a8a14cc7e70fe";
    public const string ShortFuseHash = "a8fd34b531fd22c3dbd008ed8fd3cbfcca2fb451729e19cd1fc2e4c85b5068d4";
    public const string Legacy85Hash = "ee99295d60426c9e05165ac4cb44a9ce773ff05b39bd7ae4a3c0f61e53b6c8a0";

    public override string Id => ComponentId;
    public override string Name => Tx.Get("rdx.name");
    public override string Description => Tx.Get("rdx.desc");
    public override IReadOnlyList<string> Requires => [ReShadeComponent.ComponentId];
    public override IReadOnlyList<ComponentOption> Options =>
    [
        new("variant", Tx.Get("rdx.opt.variant"), [("lecram", Tx.Get("rdx.opt.lecram")), ("shortfuse", "ShortFuse 2026-10-16")], "lecram"),
    ];

    public static string VariantLabel(string? hash) => hash?.ToLowerInvariant() switch
    {
        LecramHash => "Lecram 10.8",
        ShortFuseHash => "ShortFuse 2026-10-16",
        Legacy85Hash => Tx.Get("rdx.variant.legacy"),
        _ => Tx.Get("rdx.variant.unknown"),
    };

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var addon = ctx.Scan.Addons.FirstOrDefault(a => Path.GetFileName(a).StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase));
        return addon == null ? null
            : new ComponentStatus(ComponentStatusKind.External, Tx.Get("rdx.status.external", addon, VariantLabel(FileHash.OfFile(PathGuard.Resolve(ctx.Folder, addon)))));
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        NrRules.CheckGpu(ctx, blockers);
        NrRules.CheckSingleInjector(ctx, Id, blockers);
        if (ctx.Api != GraphicsApi.DX12 && ctx.Api != GraphicsApi.Vulkan)
            blockers.Add(Tx.Get("rdx.block.dx"));
        if (ctx.Installed(ReShadeComponent.ComponentId) && !ctx.Scan.ReShadeSupportsAddons && ctx.Api != GraphicsApi.Vulkan)
            blockers.Add(Tx.Get("rdx.block.noEarly"));
        var owner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
        if (owner != null && owner != Id && !NrRules.CanTakeOverModel(owner)) blockers.Add(Tx.Get("rdx.block.model", ComponentCatalog.NameOf(owner)));
    }

    public override Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        Task.FromResult<string?>($"{VariantLabel(Option(options, "variant") == "shortfuse" ? ShortFuseHash : LecramHash)} · {NrModels.ForGpu(ctx.Gpu).Label}");

    /// <summary>Add-on variant and NR model read from the files in the game folder.</summary>
    protected override string? PresentVersion(GameContext ctx)
    {
        var addon = ctx.Scan.Addons.FirstOrDefault(a => Path.GetFileName(a).StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase));
        var model = PathGuard.Resolve(ctx.Folder, "nvngx_dlssnr.dll");
        if (addon == null || !File.Exists(model)) return null;
        var hash = FileHash.OfFile(PathGuard.Resolve(ctx.Folder, addon))?.ToLowerInvariant();
        if (hash is not (LecramHash or ShortFuseHash) || NrModels.Identify(model) is not { } known) return null;
        return $"{VariantLabel(hash)} · {known.Label}";
    }

    public override Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var variant = Option(options, "variant");
        var (file, hash) = variant == "shortfuse"
            ? ("renodx-dlss5-shortfuse.addon64", ShortFuseHash)
            : ("renodx-dlss5-lecram-10.8.addon64", LecramHash);
        var source = Bundled(ctx, file, hash);
        var model = NrRules.ModelFile(ctx, out var nr);

        var plan = ctx.NewPlan(this, $"{VariantLabel(hash)} · {nr.Label}");
        plan.AddFile(NrRules.AddonPath(ctx, AddonName), source);
        plan.AddFile("nvngx_dlssnr.dll", model);
        plan.IniListAdd(ctx.Scan.ReShadeConfig, "ADDON", "LoadFromDllMain", AddonName);
        plan.Note(Tx.Get("rdx.note"));
        return Task.FromResult(plan.Build());
    }

    internal static string Bundled(GameContext ctx, string file, string hash)
    {
        var path = Path.Combine(ctx.Services.Paths.Bundled, file);
        if (!File.Exists(path)) throw new HdlssException(Tx.Get("rdx.err.bundledMissing", file));
        if (!FileHash.Equal(FileHash.OfFile(path), hash) || !PeFile.IsPe64(path)) throw new HdlssException(Tx.Get("rdx.err.bundledHash", file));
        return path;
    }
}
