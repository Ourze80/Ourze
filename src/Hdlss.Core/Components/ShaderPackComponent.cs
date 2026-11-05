using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// A ReShade shader collection from its author's repository, pinned to the commit current at
/// install time, installed in its own folder and registered in ReShade's search paths.
/// </summary>
public sealed class ShaderPackComponent(string key, string name, string repo, string branch, string description) : ComponentBase
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".fx", ".fxh", ".h", ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".tga" };
    private const string CasShader = "lilium__cas_hdr.fx";

    public static readonly ShaderPackComponent Lilium = new("lilium", "Lilium · strumenti HDR", "EndlesslyFlowering/ReShade_HDR_shaders", "master",
        "Analisi della luminanza, regolazione dei neri, tone mapping e conversione SDR → HDR (richiede un'uscita HDR). Include anche CAS.");
    public static readonly ShaderPackComponent Cas = new("cas", "AMD FidelityFX CAS · nitidezza", "EndlesslyFlowering/ReShade_HDR_shaders", "master",
        "Nitidezza adattiva al contrasto, variante Lilium per SDR/HDR. Parti da un'intensità bassa: troppa nitidezza crea aloni.");

    public string Key { get; } = key;
    public override string Id => "fx-" + Key;
    public override string Name => name;
    public override string Description => description;
    public override ComponentCategory Category => ComponentCategory.Shaders;
    public override bool NeedsReShade => true;
    private string Prefix => "reshade-shaders/HDLSS-" + Key;

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        ReShadeAddons.RequireAddonSupport(ctx, blockers);
        if (Key == "cas" && (ctx.Installed(Lilium.Id) || Has1x(ctx, "lilium"))) blockers.Add("CAS è già incluso in Lilium: attiva «Lilium CAS» nel menu ReShade.");
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        (await Commit(ctx, repo, branch, cancel))[..10];

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var http = ctx.Services.Http;
        progress?.Report(new($"Cerco la revisione corrente di {repo}…"));
        var commit = await Commit(ctx, repo, branch, cancel);
        var standard = await Commit(ctx, "crosire/reshade-shaders", "slim", cancel);
        var temp = Releases.TempDir(ctx);
        try
        {
            var headersZip = await http.DownloadAsync($"https://github.com/crosire/reshade-shaders/archive/{standard}.zip", $"reshade-shaders-{standard[..10]}.zip", null, 64_000_000, progress, cancel);
            var packZip = await http.DownloadAsync($"https://github.com/{repo}/archive/{commit}.zip", $"{repo.Replace('/', '-')}-{commit[..10]}.zip", null, 250_000_000, progress, cancel);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (zip, sub) in new[] { (headersZip, "std"), (packZip, "pack") })
            {
                var dir = Path.Combine(temp, sub);
                foreach (var entry in Archives.Extract(zip, dir, Accept, 64_000_000, 250_000_000))
                    files[entry[(entry.IndexOf('/') + 1)..]] = PathGuard.Resolve(dir, entry); // pack overrides std
            }
            bool casInstalled = ctx.Installed(Cas.Id) || Has1x(ctx, "cas");
            var selected = files.Where(f => Include(f.Key, casInstalled)).ToList();
            if (!selected.Any(f => f.Key.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))) throw new HdlssException("Pacchetto senza shader .fx.");

            var plan = ctx.NewPlan(this, commit[..10]);
            plan.Meta("source", $"https://github.com/{repo}/tree/{commit}");
            foreach (var (rel, file) in selected) plan.AddFile($"{Prefix}/{rel}", file);
            var config = ctx.Scan.ReShadeConfig;
            plan.IniListAdd(config, "GENERAL", "EffectSearchPaths", $".\\{Prefix.Replace('/', '\\')}\\Shaders\\**");
            plan.IniListAdd(config, "GENERAL", "TextureSearchPaths", $".\\{Prefix.Replace('/', '\\')}\\Textures\\**");
            plan.Note($"{selected.Count(f => f.Key.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))} effetti disponibili nel menu ReShade (Home): attiva solo quelli che servono.");
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }

    /// <summary>Same pack installed by HDLSS5 1.x (reshade-shaders/HDRUnlock-&lt;key&gt;).</summary>
    private static bool Has1x(GameContext ctx, string key) =>
        Directory.Exists(Path.Combine(ctx.Folder, "reshade-shaders", "HDRUnlock-" + key, "Shaders"));

    private static bool Accept(string entry)
    {
        int cut = entry.IndexOf('/');
        if (cut < 0) return false;
        var rel = entry[(cut + 1)..];
        return (rel.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase))
               && Extensions.Contains(Path.GetExtension(rel));
    }

    private bool Include(string relative, bool casInstalled)
    {
        var file = Path.GetFileName(relative);
        bool fx = file.EndsWith(".fx", StringComparison.OrdinalIgnoreCase);
        if (Key == "cas") return !fx || file.Equals(CasShader, StringComparison.OrdinalIgnoreCase);
        if (Key == "lilium" && casInstalled && file.Equals(CasShader, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static async Task<string> Commit(GameContext ctx, string repo, string branch, CancellationToken cancel)
    {
        using var json = await ctx.Services.Http.GetJsonAsync($"https://api.github.com/repos/{repo}/commits/{branch}", cancel, TimeSpan.FromHours(1));
        var sha = json.RootElement.TryGetProperty("sha", out var s) ? s.GetString() : null;
        if (sha == null || !Regex.IsMatch(sha, "^[a-f0-9]{40}$")) throw new HdlssException($"{repo}: revisione non valida.");
        return sha;
    }
}
