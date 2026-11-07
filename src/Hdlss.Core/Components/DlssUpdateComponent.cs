using Hdlss.Core.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

public sealed record DlssBuild(string Kind, Version Version, string Url, string ZipMd5, string DllMd5);

/// <summary>Newest signed release DLSS builds (SR / FG / RR) from the DLSS Swapper manifest.</summary>
public static class DlssCatalog
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/beeradmoore/dlss-swapper-manifest-builder/main/manifest.json";
    private static readonly Dictionary<string, (string Key, string Label)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = ("dlss", "Super Resolution"),
        ["nvngx_dlssg.dll"] = ("dlss_g", "Frame Generation"),
        ["nvngx_dlssd.dll"] = ("dlss_d", "Ray Reconstruction"),
    };

    public static string Label(string file) => Kinds.TryGetValue(Path.GetFileName(file), out var k) ? k.Label : file;

    public static async Task<Dictionary<string, DlssBuild>> LatestAsync(Net.HttpService http, CancellationToken cancel) =>
        (await AllAsync(http, cancel)).ToDictionary(p => p.Key, p => p.Value[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>Signed release builds per DLL, newest first (at most 25 each).</summary>
    public static async Task<Dictionary<string, List<DlssBuild>>> AllAsync(Net.HttpService http, CancellationToken cancel)
    {
        using var json = await http.GetJsonAsync(ManifestUrl, cancel, TimeSpan.FromHours(12));
        return ParseAll(json.RootElement);
    }

    public static Dictionary<string, DlssBuild> Parse(JsonElement root) =>
        ParseAll(root).ToDictionary(p => p.Key, p => p.Value[0], StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, List<DlssBuild>> ParseAll(JsonElement root)
    {
        var result = new Dictionary<string, List<DlssBuild>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, (key, _)) in Kinds)
        {
            if (!root.TryGetProperty(key, out var list)) continue;
            var builds = new List<DlssBuild>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.GetProperty("is_dev_file").GetBoolean() || !e.GetProperty("is_signature_valid").GetBoolean()) continue;
                if (!Version.TryParse(e.GetProperty("version").GetString(), out var v)) continue;
                var url = e.GetProperty("download_url").GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "dlss-swapper-downloads.beeradmoore.com" || !uri.AbsolutePath.StartsWith($"/{key}/")) continue;
                builds.Add(new DlssBuild(file, v, url, e.GetProperty("zip_md5_hash").GetString() ?? "", e.GetProperty("md5_hash").GetString() ?? ""));
            }
            var sorted = builds.GroupBy(b => b.Version).Select(g => g.First()).OrderByDescending(b => b.Version).Take(25).ToList();
            if (sorted.Count > 0) result[file] = sorted;
        }
        return result;
    }

    public static string Md5(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(MD5.HashData(s));
    }
}

/// <summary>
/// Keeps the game's own DLSS files current. The originals stay in the backup store:
/// "Remove" puts the game's original DLLs back.
/// </summary>
public sealed class DlssUpdateComponent : ComponentBase
{
    public const string ComponentId = "dlss-update";
    public override string Id => ComponentId;
    public override string Name => Tx.Get("dlss.name");
    public override string Description => Tx.Get("dlss.desc");
    /// <summary>Option key: "latest" or an exact version such as "310.9.1.0" (applied per DLL when available).</summary>
    public const string VersionOption = "dlss-version";
    public override ComponentCategory Category => ComponentCategory.Upscaling;
    public override bool IsRelevant(GameContext ctx) => DlssFiles.Find(ctx.Game.Root).Count > 0;

    public sealed record FileState(string FullPath, string Relative, string Label, Version? Current, Version? Latest)
    {
        public bool Outdated => Current != null && Latest != null && Current < Latest;
    }

    public static List<FileState> Inspect(GameContext ctx, IReadOnlyDictionary<string, DlssBuild>? latest)
    {
        var owned = OwnedByOthers(ctx);
        return DlssFiles.Find(ctx.Game.Root)
            .Where(f => !owned.Contains(Path.GetFullPath(f)))
            .Select(f => new FileState(f, Path.GetRelativePath(ctx.Game.Root, f).Replace('\\', '/'), DlssCatalog.Label(f),
                DlssFiles.VersionOf(f), latest != null && latest.TryGetValue(Path.GetFileName(f), out var b) ? b.Version : null))
            .ToList();
    }

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var files = Inspect(ctx, null);
        return files.Count == 0 ? null
            : new ComponentStatus(ComponentStatusKind.External, Tx.Get("dlss.status.originals", string.Join(" · ", files.GroupBy(f => f.Label).Select(g => $"{g.Key} {g.Min(f => f.Current)}"))));
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (Inspect(ctx, null).Count == 0) blockers.Add(Tx.Get("dlss.block.none"));
    }

    private static Dictionary<string, DlssBuild> Choose(Dictionary<string, List<DlssBuild>> all, Version? wanted)
    {
        var latest = new Dictionary<string, DlssBuild>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, builds) in all)
            latest[file] = (wanted != null ? builds.FirstOrDefault(b => b.Version == wanted) : null) ?? builds[0];
        return latest;
    }

    private static string VersionLabel(Dictionary<string, DlssBuild> chosen) =>
        string.Join(" · ", chosen.Values.Select(b => $"{DlssCatalog.Label(b.Kind)} {b.Version}"));

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var wanted = options.TryGetValue(VersionOption, out var w) && Version.TryParse(w, out var wv) ? wv : null;
        return VersionLabel(Choose(await DlssCatalog.AllAsync(ctx.Services.Http, cancel), wanted));
    }

    public override bool ChecksGameFiles => true;

    /// <summary>Compares the DLSS DLLs actually in the game with the chosen (or latest) NVIDIA build.</summary>
    public override async Task<UpdateCheck?> CheckUpdateAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var wanted = options.TryGetValue(VersionOption, out var w) && Version.TryParse(w, out var wv) ? wv : null;
        var chosen = Choose(await DlssCatalog.AllAsync(ctx.Services.Http, cancel), wanted);
        // Include the files this component manages (Inspect skips only other components' files).
        var files = Inspect(ctx, chosen).Where(f => f.Current != null && f.Latest != null).ToList();
        if (files.Count == 0) return null;
        string Kinds(Func<FileState, Version?> v) =>
            string.Join(" · ", files.GroupBy(f => f.Label).Select(g => $"{g.Key} {g.Select(v).Min()}"));
        bool outdated = files.Any(f => wanted != null ? f.Current != f.Latest : f.Current < f.Latest);
        return new(Kinds(f => f.Current), Kinds(f => f.Latest), outdated ? VersionVerdict.Newer : VersionVerdict.UpToDate);
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var notesOnTop = new List<string>();
        progress?.Report(new(Tx.Get("dlss.progress")));
        var all = await DlssCatalog.AllAsync(ctx.Services.Http, cancel);
        var wanted = options.TryGetValue(VersionOption, out var w) && Version.TryParse(w, out var wv) ? wv : null;
        var latest = Choose(all, wanted);
        var files = Inspect(ctx, latest);
        if (wanted != null)
            foreach (var kind in latest.Where(p => p.Value.Version != wanted && files.Any(f => Path.GetFileName(f.FullPath).Equals(p.Key, StringComparison.OrdinalIgnoreCase))))
                notesOnTop.Add(Tx.Get("dlss.note.noVersion", DlssCatalog.Label(kind.Key), wanted, kind.Value.Version));
        var plan = ctx.NewPlan(this, VersionLabel(latest));
        plan.AdoptGameChanges = true; // the game's own files: an update or file check may replace them
        foreach (var n in notesOnTop) plan.Note(n);
        plan.AddRoot("root", ctx.Game.Root, ctx.Game.Root);
        var temp = Releases.TempDir(ctx);
        try
        {
            var downloaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var name = Path.GetFileName(f.FullPath);
                if (!latest.TryGetValue(name, out var build)) continue;
                // Same version already in place: nothing to do (a chosen older version is a deliberate downgrade).
                if (f.Current == build.Version)
                {
                    plan.Want(PathGuard.Normalize("@root/" + f.Relative)); // keep it managed, don't release it
                    plan.Note(Tx.Get("dlss.note.same", f.Relative, build.Version));
                    continue;
                }
                if (!downloaded.TryGetValue(name, out var dll))
                {
                    var zip = await ctx.Services.Http.DownloadAsync(build.Url, Path.GetFileName(new Uri(build.Url).AbsolutePath), null, 250_000_000, progress, cancel);
                    if (!DlssCatalog.Md5(zip).Equals(build.ZipMd5, StringComparison.OrdinalIgnoreCase)) throw new HdlssException(Tx.Get("dlss.err.zip", name));
                    var dir = Path.Combine(temp, name);
                    Archives.Extract(zip, dir, n => n.Equals(name, StringComparison.OrdinalIgnoreCase), 250_000_000);
                    dll = Path.Combine(dir, name);
                    if (!File.Exists(dll) || !DlssCatalog.Md5(dll).Equals(build.DllMd5, StringComparison.OrdinalIgnoreCase) || !PeFile.IsPe64(dll))
                        throw new HdlssException(Tx.Get("dlss.err.file", name));
                    downloaded[name] = dll;
                }
                plan.AddFile("@root/" + f.Relative, dll);
                plan.Note($"{f.Relative}: {f.Current} → {build.Version}");
            }
            plan.Note(Tx.Get("dlss.note.source"));
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }

    private static HashSet<string> OwnedByOthers(GameContext ctx)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.State.Components.Values.Where(c => c.Id != ComponentId))
            foreach (var f in c.Files)
            {
                try { set.Add(Path.GetFullPath(Transactions.Locations.Resolve(ctx.Folder, c.Roots, f.Path))); }
                catch (HdlssException) { }
            }
        return set;
    }
}
