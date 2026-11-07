using Hdlss.Core.Text;
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
    public override string Description => Tx.Get("rs.desc");
    public override IReadOnlyList<ComponentOption> Options => [MenuKeys.Option(MenuKeys.ReShadeDefault)];

    [GeneratedRegex(@"ReShade_Setup_(\d+\.\d+\.\d+)_Addon\.exe")] private static partial Regex SetupName();

    protected override ComponentStatus? DetectExternal(GameContext ctx) => ctx.Scan.ReShadeLoaders.Count switch
    {
        0 => null,
        1 => new ComponentStatus(ComponentStatusKind.External,
            Tx.Get(ctx.Scan.ReShadeSupportsAddons ? "rs.status.external" : "rs.status.noAddons", ctx.Scan.ReShadeLoaders[0])),
        _ => new ComponentStatus(ComponentStatusKind.Blocked, Tx.Get("rs.status.many", string.Join(", ", ctx.Scan.ReShadeLoaders))),
    };

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        var managed = ctx.State.Get(Id);
        var foreign = ctx.Scan.ReShadeLoaders.Where(l => managed?.FindFile(l) == null).ToList();
        if (foreign.Count > 0) blockers.Add(Tx.Get("rs.block.foreign", string.Join(", ", foreign)));
        if (ctx.Api == GraphicsApi.Unknown) blockers.Add(Tx.Get("rs.block.api"));
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new(Tx.Get("rs.progress.check")));
        var version = await StableVersionAsync(ctx, cancel) ?? throw new HdlssException(Tx.Get("rs.err.version"));
        var url = $"https://reshade.me/downloads/ReShade_Setup_{version}_Addon.exe";
        var setup = await ctx.Services.Http.DownloadAsync(url, $"ReShade_Setup_{version}_Addon.exe", null, 100L << 20, progress, cancel);

        var temp = Path.Combine(ctx.Services.Paths.Temp, Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = Archives.OpenAppendedZip(setup))
                Archives.ExtractZip(zip, temp, n => n == "ReShade64.dll", 80L << 20, 80L << 20);
            var dll = Path.Combine(temp, "ReShade64.dll");
            if (!FolderScan.IsReShade(dll) || !PeFile.ContainsAscii(dll, "ReShadeRegisterAddon"))
                throw new HdlssException(Tx.Get("rs.err.dll"));
            return PlanFor(ctx, dll, version, url, Option(options, MenuKeys.OptionKey));
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
        var record = ctx.State.Get(Id) ?? throw new HdlssException(Tx.Get("rs.err.notManaged"));
        var loader = record.Files.FirstOrDefault(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                     ?? throw new HdlssException(Tx.Get("rs.err.noDll"));
        var dll = ctx.Services.Store.Open(loader.After);
        return PlanFor(ctx, dll, record.Version, record.Meta.GetValueOrDefault("source", ""), record.Meta.GetValueOrDefault(MenuKeys.OptionKey, MenuKeys.Default));
    }

    /// <summary>The loader file ReShade should use in this folder.</summary>
    public static string DesiredLoader(GameContext ctx, string? currentManaged) =>
        ctx.Api == GraphicsApi.Vulkan ? Vulkan.ReShadeDll
        : ctx.Scan.OptiScalerProxy != null ? "ReShade64.dll"
        : currentManaged != null && FolderScan.ProxyNames.Contains(currentManaged, StringComparer.OrdinalIgnoreCase) ? currentManaged : "dxgi.dll";

    private ChangePlan PlanFor(GameContext ctx, string dll, string version, string url, string menuKey)
    {
        {
            if (ctx.Api != GraphicsApi.Vulkan && ctx.Scan.OptiScalerProxies.Count > 1)
                throw new BlockedException(Tx.Get("rs.err.manyOpti"));
            var key = MenuKeys.Parse(menuKey) ?? MenuKeys.ReShadeKey(ctx);
            if (ctx.Api != GraphicsApi.Vulkan && ctx.Scan.OptiScalerProxy != null && key == MenuKeys.OptiScalerKey(ctx))
                throw new BlockedException(MenuKeys.ConflictMessage(key));
            var plan = ctx.NewPlan(this, version);
            if (url.Length > 0) plan.Meta("source", url);
            plan.Meta(MenuKeys.OptionKey, menuKey);
            MenuKeys.Apply(plan, ctx.State.Get(Id), ctx.Scan.ReShadeConfig, "INPUT", "KeyOverlay", menuKey, MenuKeys.ReShadeDefault, MenuKeys.ReShadeFormat);
            if (ctx.Api == GraphicsApi.Vulkan)
            {
                plan.AddFile(Vulkan.ReShadeDll, dll);
                plan.AddBytes(Vulkan.ReShadeJson, Vulkan.LayerManifest(Vulkan.ReShadeLayer, "ReShade64.dll", reshade: true));
                plan.IniSetIfMissing("ReShade.ini", "ADDON", "AddonPath", ".\\");
                plan.Note(Tx.Get("rs.note.vulkan"));
            }
            else if (ctx.Scan.OptiScalerProxy != null)
            {
                plan.AddFile("ReShade64.dll", dll);
                plan.IniSet("OptiScaler.ini", "Plugins", "LoadReshade", "true");
                plan.Note(Tx.Get("rs.note.opti", ctx.Scan.OptiScalerProxy));
            }
            else
            {
                var target = ctx.State.Get(Id)?.Files.Select(f => f.Path).FirstOrDefault(p => FolderScan.ProxyNames.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? "dxgi.dll";
                var full = plan.Full(target);
                if (File.Exists(full) && !FolderScan.IsReShade(full)) throw new BlockedException(Tx.Get("rs.err.otherOwner", target));
                plan.AddFile(target, dll);
            }
            return plan.Build();
        }
    }
}
