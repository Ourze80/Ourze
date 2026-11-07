using Hdlss.Core.Text;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

public sealed record StreamlineRelease(string Tag, Version Version, ReleaseAsset Asset);

/// <summary>Official NVIDIA Streamline SDK releases (github.com/NVIDIA-RTX/Streamline), 2.x only.</summary>
public static partial class StreamlineCatalog
{
    public const string Repo = "NVIDIA-RTX/Streamline";
    [GeneratedRegex(@"^v2\.\d+\.\d+$")] private static partial Regex TagPattern();

    public static async Task<List<StreamlineRelease>> AllAsync(HttpService http, CancellationToken cancel)
    {
        using var json = await http.GetJsonAsync(GitHub.ReleasesUrl(Repo, 30), cancel, TimeSpan.FromHours(6));
        var list = new List<StreamlineRelease>();
        foreach (var r in GitHubRelease.ParseList(json.RootElement).Where(r => !r.Draft && !r.Prerelease && TagPattern().IsMatch(r.Tag)))
        {
            var asset = r.Assets.SingleOrDefault(a => a.Name == $"streamline-sdk-{r.Tag}.zip");
            if (asset?.Sha256 == null || !GitHubRelease.FromRepo(asset, Repo, r.Tag)) continue;
            list.Add(new StreamlineRelease(r.Tag, new Version(r.Tag[1..]), asset));
        }
        return list.OrderByDescending(r => r.Version).ToList();
    }
}

/// <summary>
/// Updates the game's own NVIDIA Streamline 2.x integration (sl.*.dll) folder by folder, only
/// replacing components the game already uses. Originals stay in the store: "Remove" puts them back.
/// </summary>
public sealed class StreamlineComponent : ComponentBase
{
    public const string ComponentId = "streamline-update";
    public const string VersionOption = "streamline-version";
    public static readonly string[] Names =
        ["sl.interposer.dll", "sl.common.dll", "sl.dlss.dll", "sl.dlss_d.dll", "sl.dlss_g.dll", "sl.reflex.dll", "sl.pcl.dll", "sl.nis.dll", "sl.deepdvc.dll", "sl.directsr.dll", "sl.nvperf.dll"];

    public override string Id => ComponentId;
    public override string Name => Tx.Get("sl.name");
    public override string Description => Tx.Get("sl.desc");
    public override ComponentCategory Category => ComponentCategory.Upscaling;
    public override bool IsRelevant(GameContext ctx) => Folders(ctx).Count > 0;

    /// <summary>A Streamline folder; <see cref="Relative"/> is "" when the DLLs sit in the game root itself.</summary>
    public sealed record FolderState(string Relative, Version? Current, IReadOnlyList<string> Files)
    {
        public string Display => Relative.Length == 0 ? Tx.Get("sl.display.root") : Relative;
        public string PathOf(string name) => PathGuard.Normalize(Relative.Length == 0 ? $"@root/{name}" : $"@root/{Relative}/{name}");
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var releases = await StreamlineCatalog.AllAsync(ctx.Services.Http, cancel);
        if (releases.Count == 0) return null;
        var release = options.TryGetValue(VersionOption, out var tag) && releases.FirstOrDefault(r => r.Tag == tag) is { } chosen ? chosen : releases[0];
        return $"Streamline {release.Version}";
    }

    public override bool ChecksGameFiles => true;

    public override async Task<UpdateCheck?> CheckUpdateAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var latest = await LatestVersionAsync(ctx, options, cancel);
        var current = Folders(ctx).Select(f => f.Current).Where(v => v != null).Min();
        if (latest == null || current == null) return null;
        var present = $"Streamline {current}";
        bool chosen = options.ContainsKey(VersionOption) && options[VersionOption] != "latest";
        var verdict = chosen ? (string.Equals(present, latest, StringComparison.OrdinalIgnoreCase) ? VersionVerdict.UpToDate : VersionVerdict.Newer)
                             : VersionCompare.Compare(present, latest);
        return new(present, latest, verdict);
    }

    private static string RelativeDir(string root, string dir)
    {
        var rel = Path.GetRelativePath(root, dir).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    /// <summary>Folders (relative to the game root) holding a complete Streamline 2.x integration.</summary>
    public static List<FolderState> Folders(GameContext ctx)
    {
        var result = new List<FolderState>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var interposer in FindInterposers(ctx.Game.Root))
        {
            var dir = Path.GetDirectoryName(interposer)!;
            if (!seen.Add(dir) || !File.Exists(Path.Combine(dir, "sl.common.dll"))) continue;
            var version = PeFile.FileVersion(interposer);
            if (version == null || version.Major != 2) continue;
            var files = Names.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
            result.Add(new FolderState(RelativeDir(ctx.Game.Root, dir),new Version(version.Major, version.Minor, version.Build), files));
        }
        return result;
    }

    private static IEnumerable<string> FindInterposers(string root)
    {
        var queue = new Queue<(string, int)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var file = Path.Combine(dir, "sl.interposer.dll");
            if (File.Exists(file)) yield return file;
            if (depth >= 9) continue;
            foreach (var sub in ExecutableDetector.SafeDirs(dir))
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith('.') || n.Contains("backup", StringComparison.OrdinalIgnoreCase) || n is "Paks" or "Movies" or "Content" or "Saved" or "Localization" or "OptiScaler" or "reshade-shaders") continue;
                queue.Enqueue((sub, depth + 1));
            }
        }
    }

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var folders = Folders(ctx);
        return folders.Count == 0 ? null : new ComponentStatus(ComponentStatusKind.External, Tx.Get("sl.status.originals", string.Join(" · ", folders.Select(f => f.Current).Distinct())));
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (Folders(ctx).Count == 0) blockers.Add(Tx.Get("sl.block.absent"));
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new(Tx.Get("sl.progress.check")));
        var releases = await StreamlineCatalog.AllAsync(ctx.Services.Http, cancel);
        if (releases.Count == 0) throw new HdlssException(Tx.Get("sl.err.noRelease"));
        var release = options.TryGetValue(VersionOption, out var tag) && releases.FirstOrDefault(r => r.Tag == tag) is { } chosen ? chosen : releases[0];

        var folders = Folders(ctx);
        var plan = ctx.NewPlan(this, $"Streamline {release.Version}");
        plan.AdoptGameChanges = true; // the game's own files: an update or file check may replace them
        plan.AddRoot("root", ctx.Game.Root, ctx.Game.Root);
        var owned = ctx.State.Components.Values.Where(c => c.Id != Id).SelectMany(c => c.Files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var temp = Releases.TempDir(ctx);
        try
        {
            var zip = await ctx.Services.Http.DownloadAsync(release.Asset.Url, release.Asset.Name, release.Asset.Sha256, 1L << 30, progress, cancel);
            progress?.Report(new(Tx.Get("sl.progress.extract")));
            var sdk = Path.Combine(temp, "sdk");
            var extracted = Archives.Extract(zip, sdk, n => Regex.IsMatch(n, @"^(?:[^/]+/)?bin/x64/sl\.[a-z0-9_]+\.dll$", RegexOptions.IgnoreCase), 64L << 20, 1L << 30);
            var package = extracted.ToDictionary(p => Path.GetFileName(p), p => PathGuard.Resolve(sdk, p), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, file) in package.Where(p => Names.Contains(p.Key, StringComparer.OrdinalIgnoreCase))) Validate(file, name, release.Version);

            foreach (var folder in folders)
            {
                var wanted = folder.Files.ToList();
                // Reflex and DLSS-G need PCL in recent SDKs.
                if ((wanted.Contains("sl.reflex.dll") || wanted.Contains("sl.dlss_g.dll")) && !wanted.Contains("sl.pcl.dll")) wanted.Add("sl.pcl.dll");
                int changed = 0;
                foreach (var name in wanted)
                {
                    var rel = folder.PathOf(name);
                    if (owned.Contains(rel)) continue;
                    if (!package.TryGetValue(name, out var source))
                    {
                        plan.Note(Tx.Get("sl.note.missing", folder.Display, name, release.Tag));
                        plan.Want(rel);
                        continue;
                    }
                    plan.AddFile(rel, source);
                    changed++;
                }
                plan.Note(Tx.Get("sl.note.folder", folder.Display, folder.Current, release.Version, changed));
            }
            plan.Note(Tx.Get("sl.note.source"));
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }

    /// <summary>Production NVIDIA module with the expected internal name, signature and SDK version.</summary>
    private static void Validate(string file, string name, Version sdk)
    {
        if (!PeFile.IsPe64(file)) throw new HdlssException(Tx.Get("sl.err.notDll", name));
        var info = FileVersionInfo.GetVersionInfo(file);
        var expected = name.Equals("sl.dlss_d.dll", StringComparison.OrdinalIgnoreCase) ? "sl.dlss_rr" : Path.GetFileNameWithoutExtension(name);
        if (!string.Equals(info.InternalName, expected, StringComparison.OrdinalIgnoreCase)
            || (info.ProductName ?? "").IndexOf("STREAMLINE PRODUCTION", StringComparison.OrdinalIgnoreCase) < 0)
            throw new HdlssException(Tx.Get("sl.err.notProd", name));
        if (info.FileMajorPart != sdk.Major || info.FileMinorPart != sdk.Minor || info.FileBuildPart != sdk.Build)
            throw new HdlssException(Tx.Get("sl.err.version", name, info.FileVersion, sdk));
        try
        {
#pragma warning disable SYSLIB0057
            var signer = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            if (!signer.Subject.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) throw new HdlssException(Tx.Get("sl.err.notNvidia", name));
        }
        catch (System.Security.Cryptography.CryptographicException) { throw new HdlssException(Tx.Get("sl.err.noSig", name)); }
    }
}
