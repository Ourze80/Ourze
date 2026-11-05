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
            blockers.Add($"{ComponentCatalog.NameOf(other)} è già installato: usa un solo percorso Neural Rendering alla volta.");
        var owned = ctx.State.Components.Values.SelectMany(c => c.Files).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in ctx.Scan.NrAddons().Where(a => !owned.Contains(a)))
            blockers.Add($"Add-on NR di un altro programma presente: {addon}. Importalo con «Importa in HDLSS5» oppure rimuovilo: due passaggi NR insieme non funzionano.");
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
        if (!ctx.Gpu.SupportsNr) blockers.Add("GPU NVIDIA RTX 4000/5000 non rilevata.");
        else if (ctx.Gpu.DriverTooOld) blockers.Add($"Driver {ctx.Gpu.Driver}: il modello NR richiede almeno {GpuInfo.MinimumDriver}.");
    }

    public static string ModelFile(GameContext ctx, out NrModel model)
    {
        model = NrModels.ForGpu(ctx.Gpu);
        return NrModels.Locate(model, ctx.Services.Paths.NrPackageCandidates(ctx.Services.Settings.LegacyAppDir));
    }

    /// <summary>Path (relative to the game folder) where ReShade looks for add-ons.</summary>
    public static string AddonPath(GameContext ctx, string fileName)
    {
        if (ctx.Scan.AddonFolder == "?") throw new BlockedException("AddonPath di ReShade punta fuori dalla cartella del gioco: impostalo a .\\ e riprova.");
        return ctx.Scan.AddonFolder.Length == 0 ? fileName : ctx.Scan.AddonFolder + "/" + fileName;
    }
}

/// <summary>Only the NR model DLL, for loaders that bring their own NR pass (OptiScaler F5).</summary>
public sealed class NrModelComponent : ComponentBase
{
    public const string ComponentId = "nr-model";
    public override string Id => ComponentId;
    public override string Name => "Modello NR 310.9.16.0";
    public override string Description => "nvngx_dlssnr.dll, il modello DLSS 5 usato sia da ReShade (RenoDX NR, Feeder) sia da OptiScaler F5, per la GPU rilevata (RTX 4000 Compat FP16 o RTX 5000). RenoDX NR e Feeder lo installano già da soli: serve a parte solo per aggiornarlo o per OptiScaler.";
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
        if (owner != null) return new ComponentStatus(ComponentStatusKind.External, $"Incluso in {ComponentCatalog.NameOf(owner)}");
        var model = NrModels.Identify(file);
        return new ComponentStatus(ComponentStatusKind.External, model != null ? $"Presente · {model.Label}" : $"Presente · versione {PeFile.FileVersion(file)}");
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        NrRules.CheckGpu(ctx, blockers);
        var owner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
        if (owner != null && owner != Id) blockers.Add($"Già installato insieme a {ComponentCatalog.NameOf(owner)}: si aggiorna e si rimuove con lui.");
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
    public override string Name => "RenoDX NR (DLSS nativo)";
    public override string Description => "Neural Rendering come add-on ReShade per giochi con DLSS nativo in DirectX 12. Include il modello NR per la GPU.";
    public override IReadOnlyList<string> Requires => [ReShadeComponent.ComponentId];
    public override IReadOnlyList<ComponentOption> Options =>
    [
        new("variant", "Versione add-on", [("lecram", "Lecram 10.8 (consigliata)"), ("shortfuse", "ShortFuse 2026-10-16")], "lecram"),
    ];

    public static string VariantLabel(string? hash) => hash?.ToLowerInvariant() switch
    {
        LecramHash => "Lecram 10.8",
        ShortFuseHash => "ShortFuse 2026-10-16",
        Legacy85Hash => "8.5-model (precedente)",
        _ => "versione sconosciuta",
    };

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var addon = ctx.Scan.Addons.FirstOrDefault(a => Path.GetFileName(a).StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase));
        return addon == null ? null
            : new ComponentStatus(ComponentStatusKind.External, $"{addon} · {VariantLabel(FileHash.OfFile(PathGuard.Resolve(ctx.Folder, addon)))} · installato da altro software");
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        NrRules.CheckGpu(ctx, blockers);
        NrRules.CheckSingleInjector(ctx, Id, blockers);
        if (ctx.Api != GraphicsApi.DX12 && ctx.Api != GraphicsApi.Vulkan)
            blockers.Add("RenoDX NR richiede DirectX 12 (o DLSS Vulkan nativo). Per DX11 usa «Feeder (senza DLSS)».");
        if (ctx.Installed(ReShadeComponent.ComponentId) && !ctx.Scan.ReShadeSupportsAddons && ctx.Api != GraphicsApi.Vulkan)
            blockers.Add("La copia di ReShade non supporta il caricamento anticipato degli add-on: reinstallala.");
        var owner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
        if (owner != null && owner != Id && !NrRules.CanTakeOverModel(owner)) blockers.Add($"Il modello NR è gestito da {ComponentCatalog.NameOf(owner)}: rimuovilo prima.");
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
        plan.Note("Avvia il gioco, apri ReShade (Home) e attiva Neural Rendering nella scheda dell'add-on.");
        return Task.FromResult(plan.Build());
    }

    internal static string Bundled(GameContext ctx, string file, string hash)
    {
        var path = Path.Combine(ctx.Services.Paths.Bundled, file);
        if (!File.Exists(path)) throw new HdlssException($"File incluso mancante: bundled/{file}. Reinstalla HDLSS5.");
        if (!FileHash.Equal(FileHash.OfFile(path), hash) || !PeFile.IsPe64(path)) throw new HdlssException($"bundled/{file} non corrisponde all'hash atteso.");
        return path;
    }
}
