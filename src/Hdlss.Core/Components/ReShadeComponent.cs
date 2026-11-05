using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// ReShade with full add-on support, from reshade.me. DX: proxy DLL (dxgi.dll, or
/// ReShade64.dll chained through OptiScaler). Vulkan: per-game layer, no registry changes.
/// </summary>
public sealed partial class ReShadeComponent : ComponentBase
{
    public const string ComponentId = "reshade";
    public override string Id => ComponentId;
    public override string Name => "ReShade (add-on)";
    public override string Description => "Caricatore per gli add-on RenoDX NR e Feeder. Versione stabile ufficiale con supporto completo agli add-on.";

    [GeneratedRegex(@"ReShade_Setup_(\d+\.\d+\.\d+)_Addon\.exe")] private static partial Regex SetupName();

    protected override ComponentStatus? DetectExternal(GameContext ctx) => ctx.Scan.ReShadeLoaders.Count switch
    {
        0 => null,
        1 => new ComponentStatus(ComponentStatusKind.External,
            $"Installato da altro software ({ctx.Scan.ReShadeLoaders[0]}){(ctx.Scan.ReShadeSupportsAddons ? "" : " · senza supporto add-on")}"),
        _ => new ComponentStatus(ComponentStatusKind.Blocked, "Più copie di ReShade: " + string.Join(", ", ctx.Scan.ReShadeLoaders)),
    };

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        var managed = ctx.State.Get(Id);
        var foreign = ctx.Scan.ReShadeLoaders.Where(l => managed?.FindFile(l) == null).ToList();
        if (foreign.Count > 0) blockers.Add($"ReShade è già installato da un altro programma ({string.Join(", ", foreign)}). Usa «Importa in HDLSS5» per gestirlo da qui.");
        if (ctx.Api == GraphicsApi.Unknown) blockers.Add("API grafica non riconosciuta: impostala in «Eseguibile e API».");
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new("Controllo la versione stabile su reshade.me…"));
        var version = await StableVersionAsync(ctx, cancel) ?? throw new HdlssException("Versione ReShade con add-on non identificabile su reshade.me.");
        var url = $"https://reshade.me/downloads/ReShade_Setup_{version}_Addon.exe";
        var setup = await ctx.Services.Http.DownloadAsync(url, $"ReShade_Setup_{version}_Addon.exe", null, 100L << 20, progress, cancel);

        var temp = Path.Combine(ctx.Services.Paths.Temp, Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = Archives.OpenAppendedZip(setup))
                Archives.ExtractZip(zip, temp, n => n == "ReShade64.dll", 80L << 20, 80L << 20);
            var dll = Path.Combine(temp, "ReShade64.dll");
            if (!FolderScan.IsReShade(dll) || !PeFile.ContainsAscii(dll, "ReShadeRegisterAddon"))
                throw new HdlssException("La DLL estratta non è ReShade x64 con supporto add-on.");
            return PlanFor(ctx, dll, version, url);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task<string?> StableVersionAsync(GameContext ctx, CancellationToken cancel)
    {
        var html = await ctx.Services.Http.GetTextAsync("https://reshade.me/", cancel, TimeSpan.FromHours(6));
        var versions = SetupName().Matches(html).Select(m => m.Groups[1].Value).Distinct().ToList();
        return versions.Count == 1 ? versions[0] : null;
    }

    public override Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        StableVersionAsync(ctx, cancel);

    /// <summary>
    /// Moves the managed ReShade to the loader that fits the folder right now (no download):
    /// ReShade64.dll loaded by OptiScaler when OptiScaler is present, otherwise dxgi.dll.
    /// </summary>
    public ChangePlan PlanRelocate(GameContext ctx)
    {
        var record = ctx.State.Get(Id) ?? throw new HdlssException("ReShade non è gestito da HDLSS5 in questo gioco.");
        var loader = record.Files.FirstOrDefault(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                     ?? throw new HdlssException("Registro di ReShade senza DLL.");
        var dll = ctx.Services.Store.Open(loader.After);
        return PlanFor(ctx, dll, record.Version, record.Meta.GetValueOrDefault("source", ""));
    }

    /// <summary>The loader file ReShade should use in this folder.</summary>
    public static string DesiredLoader(GameContext ctx, string? currentManaged) =>
        ctx.Api == GraphicsApi.Vulkan ? Vulkan.ReShadeDll
        : ctx.Scan.OptiScalerProxy != null ? "ReShade64.dll"
        : currentManaged != null && FolderScan.ProxyNames.Contains(currentManaged, StringComparer.OrdinalIgnoreCase) ? currentManaged : "dxgi.dll";

    private ChangePlan PlanFor(GameContext ctx, string dll, string version, string url)
    {
        {
            if (ctx.Api != GraphicsApi.Vulkan && ctx.Scan.OptiScalerProxies.Count > 1)
                throw new BlockedException("Più copie di OptiScaler nella cartella: sistema prima OptiScaler.");
            var plan = ctx.NewPlan(this, version);
            if (url.Length > 0) plan.Meta("source", url);
            if (ctx.Api == GraphicsApi.Vulkan)
            {
                plan.AddFile(Vulkan.ReShadeDll, dll);
                plan.AddBytes(Vulkan.ReShadeJson, Vulkan.LayerManifest(Vulkan.ReShadeLayer, "ReShade64.dll", reshade: true));
                plan.IniSetIfMissing("ReShade.ini", "ADDON", "AddonPath", ".\\");
                plan.Note("Vulkan: avvia il gioco con il pulsante «Avvia» di HDLSS5 per caricare il layer.");
            }
            else if (ctx.Scan.OptiScalerProxy != null)
            {
                plan.AddFile("ReShade64.dll", dll);
                plan.IniSet("OptiScaler.ini", "Plugins", "LoadReshade", "true");
                plan.Note($"ReShade viene caricato da OptiScaler ({ctx.Scan.OptiScalerProxy} → ReShade64.dll): nessun doppio caricamento.");
            }
            else
            {
                var target = ctx.State.Get(Id)?.Files.Select(f => f.Path).FirstOrDefault(p => FolderScan.ProxyNames.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? "dxgi.dll";
                var full = plan.Full(target);
                if (File.Exists(full) && !FolderScan.IsReShade(full)) throw new BlockedException($"{target} appartiene a un altro componente: nessuna sovrascrittura.");
                plan.AddFile(target, dll);
            }
            return plan.Build();
        }
    }
}
