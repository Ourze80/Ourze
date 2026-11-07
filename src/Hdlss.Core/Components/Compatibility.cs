using System.Text.RegularExpressions;
using Hdlss.Core.IO;
using Hdlss.Core.Text;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

public sealed record CompatIssue(string Text, bool Fixable);

/// <summary>
/// Keeps ReShade and OptiScaler working together. The usual breakage with other tools is a
/// double hook: ReShade as dxgi.dll and OptiScaler as another proxy both hooking the swap chain.
/// The supported layout is OptiScaler as the proxy loading ReShade64.dll ([Plugins] LoadReshade=true),
/// so OptiScaler can do DLSS 5 while ReShade carries HDR and the other add-ons.
/// </summary>
public static class Compatibility
{
    public static List<CompatIssue> Check(GameContext ctx)
    {
        var issues = new List<CompatIssue>();
        var s = ctx.Scan;
        var reshade = ctx.State.Get(ReShadeComponent.ComponentId);
        var loader = reshade?.Files.Select(f => f.Path).FirstOrDefault(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        bool opti = s.OptiScalerProxy != null;

        if (s.ReShadeLoaders.Count > 1)
            issues.Add(new(Tx.Get("compat.reshadeTwice", s.ReShadeLoaders.Count, string.Join(", ", s.ReShadeLoaders)), false));

        if (reshade != null && loader != null && ctx.Api != Games.GraphicsApi.Vulkan)
        {
            var desired = ReShadeComponent.DesiredLoader(ctx, loader);
            if (opti && !loader.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Tx.Get("compat.separate", loader, s.OptiScalerProxy), true));
            else if (!opti && loader.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Tx.Get("compat.optiGone"), true));
            else if (opti && !string.Equals(IniDocument.Load(PathGuard.Resolve(ctx.Folder, "OptiScaler.ini")).Get("Plugins", "LoadReshade"), "true", StringComparison.OrdinalIgnoreCase))
                issues.Add(new(Tx.Get("compat.loadReshadeOff"), true));
            else if (!string.Equals(desired, loader, StringComparison.OrdinalIgnoreCase) && !opti)
                issues.Add(new(Tx.Get("compat.shouldUse", desired), true));
        }
        else if (reshade == null && opti && s.ReShadeLoaders.Any(l => FolderScan.ProxyNames.Contains(l, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new(Tx.Get("compat.foreignReshade", s.ReShadeLoaders[0]), false));

        // Two Neural Rendering passes: OptiScaler with NR on plus a ReShade NR add-on.
        if (opti && OptiScalerNrEnabled(ctx) && s.Addons.Any(a => Regex.IsMatch(Path.GetFileName(a), "renodx-dlss|dlss5-feed", RegexOptions.IgnoreCase)))
            issues.Add(new(Tx.Get("compat.twoNr"), false));
        return issues;
    }

    public static bool OptiScalerNrEnabled(GameContext ctx)
    {
        var ini = PathGuard.Resolve(ctx.Folder, "OptiScaler.ini");
        return File.Exists(ini) && string.Equals(IniDocument.Load(ini).Get("DlssNr", "Enabled"), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The plan that resolves the fixable issues (moving ReShade to the right loader), or null.</summary>
    public static ChangePlan? PlanFix(GameContext ctx)
    {
        if (!Check(ctx).Any(i => i.Fixable)) return null;
        var reshade = (ReShadeComponent)ComponentCatalog.Get(ReShadeComponent.ComponentId);
        var plan = reshade.PlanRelocate(ctx);
        plan.Notes.Insert(0, Tx.Get("compat.fixNote"));
        return plan.IsEmpty ? null : plan;
    }
}
