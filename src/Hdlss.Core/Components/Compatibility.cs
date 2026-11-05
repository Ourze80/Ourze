using System.Text.RegularExpressions;
using Hdlss.Core.IO;
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
            issues.Add(new($"ReShade è presente {s.ReShadeLoaders.Count} volte ({string.Join(", ", s.ReShadeLoaders)}): verrebbe caricato due volte.", false));

        if (reshade != null && loader != null && ctx.Api != Games.GraphicsApi.Vulkan)
        {
            var desired = ReShadeComponent.DesiredLoader(ctx, loader);
            if (opti && !loader.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
                issues.Add(new($"ReShade ({loader}) e OptiScaler ({s.OptiScalerProxy}) si caricano separatamente: rischio di doppio aggancio e crash. Va caricato da OptiScaler.", true));
            else if (!opti && loader.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("ReShade è configurato per essere caricato da OptiScaler, che non c'è più: così non parte.", true));
            else if (opti && !string.Equals(IniDocument.Load(PathGuard.Resolve(ctx.Folder, "OptiScaler.ini")).Get("Plugins", "LoadReshade"), "true", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("OptiScaler non è impostato per caricare ReShade (LoadReshade=false).", true));
            else if (!string.Equals(desired, loader, StringComparison.OrdinalIgnoreCase) && !opti)
                issues.Add(new($"ReShade dovrebbe usare {desired}.", true));
        }
        else if (reshade == null && opti && s.ReShadeLoaders.Any(l => FolderScan.ProxyNames.Contains(l, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new($"ReShade di un altro programma ({s.ReShadeLoaders[0]}) e OptiScaler si caricano separatamente. Importa ReShade in HDLSS5 e poi «Risolvi».", false));

        // Two Neural Rendering passes: OptiScaler with NR on plus a ReShade NR add-on.
        if (opti && OptiScalerNrEnabled(ctx) && s.Addons.Any(a => Regex.IsMatch(Path.GetFileName(a), "renodx-dlss|dlss5-feed", RegexOptions.IgnoreCase)))
            issues.Add(new("DLSS 5 è attivo sia in OptiScaler sia come add-on ReShade: due passaggi NR insieme non funzionano. Togline uno (OptiScaler Standard + RenoDX NR, oppure OptiScaler F5 senza add-on NR).", false));
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
        plan.Notes.Insert(0, "Adatto ReShade a OptiScaler per evitare il doppio caricamento. Add-on e impostazioni di ReShade restano.");
        return plan.IsEmpty ? null : plan;
    }
}
