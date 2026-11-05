using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;

namespace Hdlss.Core.Components;

/// <summary>Checks shared by every ReShade add-on / shader component.</summary>
public static class ReShadeAddons
{
    /// <summary>Any single ReShade with add-on support works, managed by HDLSS or not.</summary>
    public static void RequireAddonSupport(GameContext ctx, List<string> blockers)
    {
        var loaders = ctx.Scan.ReShadeLoaders;
        if (loaders.Count == 0) blockers.Add("Installa prima ReShade (add-on).");
        else if (loaders.Count > 1) blockers.Add("Più copie di ReShade: risolvi il doppione prima.");
        else if (!loaders[0].StartsWith(Vulkan.Dir) && !ctx.Scan.ReShadeSupportsAddons)
            blockers.Add("La copia di ReShade non supporta gli add-on: reinstallala da HDLSS5.");
        if (ctx.Scan.AddonFolder == "?") blockers.Add(NrRules.AddonPathOutside(ctx));
    }

    public static string AddonTarget(GameContext ctx, string fileName) => NrRules.AddonPath(ctx, fileName);

    public static bool AddonPresent(GameContext ctx, string pattern) =>
        ctx.Scan.Addons.Any(a => Regex.IsMatch(Path.GetFileName(a), pattern, RegexOptions.IgnoreCase));

    /// <summary>Add-ons matching <paramref name="pattern"/> not managed by HDLSS.</summary>
    public static IEnumerable<string> ForeignAddons(GameContext ctx, string pattern)
    {
        var owned = ctx.State.Components.Values.SelectMany(c => c.Files).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ctx.Scan.Addons.Where(a => !owned.Contains(a) && Regex.IsMatch(Path.GetFileName(a), pattern, RegexOptions.IgnoreCase));
    }
}

/// <summary>"Latest release → single asset → verified download" used by most components.</summary>
public static class Releases
{
    public static async Task<(GitHubRelease Release, ReleaseAsset Asset, string File)> LatestAsset(
        GameContext ctx, string repo, Func<ReleaseAsset, bool> match, long maxBytes, bool requireDigest,
        IProgress<OperationProgress>? progress, CancellationToken cancel, Func<string, bool>? tagFilter = null)
    {
        progress?.Report(new($"Cerco l'ultima release di {repo}…"));
        using var json = await ctx.Services.Http.GetJsonAsync(GitHub.LatestUrl(repo), cancel, TimeSpan.FromMinutes(30));
        var release = GitHubRelease.Parse(json.RootElement);
        if (release.Draft || release.Prerelease || (tagFilter != null && !tagFilter(release.Tag)))
            throw new HdlssException($"{repo}: nessuna release stabile disponibile.");
        var assets = release.Assets.Where(match).ToList();
        if (assets.Count != 1) throw new HdlssException($"{repo}: pacchetto della release non riconosciuto ({assets.Count} corrispondenze).");
        var asset = assets[0];
        if (!GitHubRelease.FromRepo(asset, repo, release.Tag)) throw new HdlssException($"{repo}: origine del download non valida.");
        if (asset.Size > maxBytes) throw new HdlssException($"{asset.Name}: dimensione oltre il limite previsto.");
        if (requireDigest && asset.Sha256 == null) throw new HdlssException($"{asset.Name}: la release non pubblica lo SHA-256, download non verificabile.");
        if (asset.Sha256 == null) ctx.Services.Log.Warn($"{asset.Name}: nessuno SHA-256 pubblicato, verifica solo tramite HTTPS.");
        var file = await ctx.Services.Http.DownloadAsync(asset.Url, asset.Name, asset.Sha256, maxBytes, progress, cancel);
        return (release, asset, file);
    }

    /// <summary>Tag of the latest stable release (no download; cached for 30 minutes).</summary>
    public static async Task<string?> LatestTagAsync(GameContext ctx, string repo, CancellationToken cancel, Func<string, bool>? tagFilter = null)
    {
        using var json = await ctx.Services.Http.GetJsonAsync(GitHub.LatestUrl(repo), cancel, TimeSpan.FromMinutes(30));
        var release = GitHubRelease.Parse(json.RootElement);
        return release.Draft || release.Prerelease || (tagFilter != null && !tagFilter(release.Tag)) ? null : release.Tag;
    }

    public static string TempDir(GameContext ctx) => Path.Combine(ctx.Services.Paths.Temp, Guid.NewGuid().ToString("N"));

    public static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* temp is cleared at next start */ }
    }
}
