using Hdlss.Core.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// RenoDX HDR mod for the selected game, matched by exact title in the official RenoDX
/// mods catalog (wiki), or imported from a file the user downloaded (e.g. Nexus Mods).
/// </summary>
public sealed partial class RenoDxHdrComponent : ComponentBase, ILocalImport
{
    public const string ComponentId = "renodx-hdr";
    public const string CatalogUrl = "https://raw.githubusercontent.com/wiki/clshortfuse/renodx/Mods.md";

    public override string Id => ComponentId;
    public override string Name => "RenoDX HDR";
    public override string Description => Tx.Get("hdr.desc");
    public override ComponentCategory Category => ComponentCategory.Hdr;
    public override bool NeedsReShade => true;
    public string ImportLabel => Tx.Get("hdr.importLabel");
    public string ImportFilter => Tx.Get("hdr.importFilter");

    [GeneratedRegex(@"^renodx-[a-zA-Z0-9_.-]+\.addon64$", RegexOptions.IgnoreCase)] private static partial Regex AddonName();
    [GeneratedRegex("dlss|neural|mfg|framegen", RegexOptions.IgnoreCase)] private static partial Regex NotHdr();
    private const string HdrPattern = @"^renodx-(?!dlss|mfg).*\.addon64$";

    public sealed record CatalogMod(string Title, string Url, string Notes, bool EngineRecipe);

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var addon = ReShadeAddons.ForeignAddons(ctx, HdrPattern).FirstOrDefault();
        return addon == null ? null : new ComponentStatus(ComponentStatusKind.External, Tx.Get("hdr.status.external", addon));
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        ReShadeAddons.RequireAddonSupport(ctx, blockers);
        if (ReShadeAddons.AddonPresent(ctx, "^AutoHDR\\.addon64$"))
            blockers.Add(Tx.Get("hdr.block.autoHdr"));
        var foreign = ReShadeAddons.ForeignAddons(ctx, HdrPattern).ToList();
        if (foreign.Count > 0) blockers.Add(Tx.Get("hdr.block.foreign", string.Join(", ", foreign)));
    }

    public override bool ChecksAvailability => true;

    /// <summary>Looks the game up in the catalog: the reason when there is no direct download for it.</summary>
    public override async Task<string?> UnavailableReasonAsync(GameContext ctx, CancellationToken cancel)
    {
        var markdown = await ctx.Services.Http.GetTextAsync(CatalogUrl, cancel, TimeSpan.FromHours(6));
        try { Resolve(markdown, ctx.Game.Name); return null; }
        catch (HdlssException ex) { return ex.Message; }
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new(Tx.Get("hdr.progress.catalog")));
        var markdown = await ctx.Services.Http.GetTextAsync(CatalogUrl, cancel, TimeSpan.FromHours(6));
        var mod = Resolve(markdown, ctx.Game.Name);
        var uri = ValidateSource(mod.Url);
        var name = Path.GetFileName(uri.AbsolutePath);
        string url = mod.Url; string? sha = null; DateTime? published = null;
        if (uri.Host == "github.com")
        {
            var (asset, when) = await FindGitHubAssetAsync(ctx, uri, name, cancel)
                ?? throw new HdlssException(Tx.Get("hdr.err.noRelease"));
            url = asset.Url; sha = asset.Sha256; published = when;
        }
        var file = await ctx.Services.Http.DownloadAsync(url, name, sha, 64_000_000, progress, cancel);
        if (!PeFile.IsPe64(file)) throw new HdlssException(Tx.Get("hdr.err.notX64"));

        var plan = ctx.NewPlan(this, $"{mod.Title} · {name}");
        plan.Meta("source", url);
        if (published is { } when0 && when0.Year > 2000) plan.Meta(PublishedMeta, when0.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        if (!Official(new Uri(url))) plan.Note(Tx.Get("hdr.note.unofficial", new Uri(url).Host, new Uri(url).AbsolutePath));
        plan.AddFile(ReShadeAddons.AddonTarget(ctx, name), file);
        if (mod.Notes.Length > 0) plan.Note(Tx.Get("hdr.note.catalog", mod.Notes));
        if (mod.EngineRecipe) plan.Note(Tx.Get("hdr.note.engine"));
        plan.Note(Tx.Get("hdr.note.run"));
        return plan.Build();
    }

    public const string PublishedMeta = "published";

    /// <summary>Newest non-draft release asset of the repository with this file name (null when none or not from the repo).</summary>
    private static async Task<(ReleaseAsset Asset, DateTime Published)?> FindGitHubAssetAsync(GameContext ctx, Uri uri, string name, CancellationToken cancel)
    {
        var parts = uri.AbsolutePath.Split('/');
        var repo = parts[1] + "/" + parts[2];
        using var json = await ctx.Services.Http.GetJsonAsync(GitHub.ReleasesUrl(repo, 20), cancel, TimeSpan.FromMinutes(30));
        var found = GitHubRelease.ParseList(json.RootElement).Where(r => !r.Draft)
            .SelectMany(r => r.Assets.Select(a => (r, a)))
            .Where(x => string.Equals(x.a.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.r.Published).Select(x => ((ReleaseAsset, DateTime)?)(x.a, x.r.Published)).FirstOrDefault();
        if (found == null) return null;
        if (!found.Value.Item1.Url.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.Ordinal)) throw new HdlssException(Tx.Get("hdr.err.badSource"));
        return found;
    }

    /// <summary>The installed add-on (managed record first, then one by another tool): relative path, name, and its hash on disk.</summary>
    private (string Name, string? Sha, string? Published)? InstalledAddon(GameContext ctx)
    {
        var record = ctx.State.Get(Id);
        string? rel = record?.Files.Select(f => f.Path).FirstOrDefault(p => AddonName().IsMatch(Path.GetFileName(p)))
            ?? (record == null ? ReShadeAddons.ForeignAddons(ctx, HdrPattern).FirstOrDefault() : null);
        if (rel == null) return null;
        string? sha = null;
        try { sha = FileHash.OfFileOrNull(PathGuard.Resolve(ctx.Folder, rel)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HdlssException) { }
        sha ??= record?.FindFile(rel)?.After;
        string? published = null;
        record?.Meta.TryGetValue(PublishedMeta, out published);
        return (Path.GetFileName(rel), sha, published);
    }

    /// <summary>What the catalog would install now for this game (GitHub only): asset, its date, and the name it carries.</summary>
    private async Task<(ReleaseAsset Asset, DateTime Published)?> CatalogAssetAsync(GameContext ctx, string? onlyName, CancellationToken cancel)
    {
        try
        {
            var markdown = await ctx.Services.Http.GetTextAsync(CatalogUrl, cancel, TimeSpan.FromHours(6));
            var uri = ValidateSource(Resolve(markdown, ctx.Game.Name).Url);
            if (uri.Host != "github.com") return null;
            var name = Path.GetFileName(uri.AbsolutePath);
            if (onlyName != null && !string.Equals(onlyName, name, StringComparison.OrdinalIgnoreCase)) return null;
            return await FindGitHubAssetAsync(ctx, uri, name, cancel);
        }
        catch (Exception ex) when (ex is HdlssException or HttpRequestException or IOException or JsonException || (ex is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            return null;
        }
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var installed = InstalledAddon(ctx);
        var latest = await CatalogAssetAsync(ctx, installed?.Name, cancel);
        return latest == null ? null : DateLabel(latest.Value.Published) ?? ShortHash(latest.Value.Asset.Sha256);
    }

    public override async Task<UpdateCheck?> CheckUpdateAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var installed = InstalledAddon(ctx);
        if (installed == null) return null;
        var latest = await CatalogAssetAsync(ctx, installed.Value.Name, cancel);
        if (latest == null) return null;
        return CompareAsset(installed.Value.Name, installed.Value.Sha, installed.Value.Published,
            latest.Value.Asset.Name, latest.Value.Asset.Sha256, latest.Value.Published);
    }

    /// <summary>
    /// Pure comparison of the installed add-on with the current catalog asset. The file content is the truth
    /// (same SHA-256 = up to date); without an asset hash the recorded publish date is compared; with neither, null.
    /// A different file name means the catalog does not describe this file: null.
    /// </summary>
    public static UpdateCheck? CompareAsset(string? installedName, string? installedSha, string? installedPublished,
        string assetName, string? assetSha, DateTime assetPublished)
    {
        if (installedName == null || !string.Equals(installedName, assetName, StringComparison.OrdinalIgnoreCase)) return null;
        DateTime? had = DateTime.TryParse(installedPublished, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;
        var assetDate = assetPublished.Year > 2000 ? assetPublished.ToUniversalTime() : (DateTime?)null;
        string? latestDate = DateLabel(assetDate), hadDate = DateLabel(had);
        if (assetSha != null)
        {
            if (installedSha == null) return null;
            if (FileHash.Equal(installedSha, assetSha))
            {
                var label = hadDate ?? latestDate ?? ShortHash(assetSha)!;
                return new(label, latestDate ?? label, VersionVerdict.UpToDate);
            }
            // Different content: dates when they tell the two apart, otherwise short hashes.
            if (hadDate != null && latestDate != null && hadDate != latestDate) return new(hadDate, latestDate, VersionVerdict.Newer);
            return new(ShortHash(installedSha)!, ShortHash(assetSha)!, VersionVerdict.Newer);
        }
        if (had == null || assetDate == null || hadDate == null || latestDate == null) return null;
        return assetDate > had && hadDate != latestDate
            ? new(hadDate, latestDate, VersionVerdict.Newer)
            : new(hadDate, hadDate, VersionVerdict.UpToDate);
    }

    private static string? DateLabel(DateTime? utc) => utc is { } v && v.Year > 2000 ? v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static string? ShortHash(string? sha) => sha is { Length: >= 8 } ? sha[..8].ToLowerInvariant() : null;

    public Task<ChangePlan> PlanImportAsync(GameContext ctx, string file, IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var name = Path.GetFileName(file);
        if (!AddonName().IsMatch(name) || !PeFile.IsPe64(file)) throw new HdlssException(Tx.Get("hdr.err.pickAddon"));
        if (NotHdr().IsMatch(name)) throw new HdlssException(Tx.Get("hdr.err.nrMfg"));
        var plan = ctx.NewPlan(this, $"{name} · importato");
        plan.Meta("source", "local file");
        plan.AddFile(ReShadeAddons.AddonTarget(ctx, name), file);
        plan.Note(Tx.Get("hdr.note.manual"));
        return Task.FromResult(plan.Build());
    }

    private static string TitleKey(string title)
    {
        title = Regex.Replace(title, @"\[([^\]]+)\]\([^)]*\)", "$1");
        title = WebUtility.HtmlDecode(title).Replace("™", "").Replace("®", "").Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return Regex.Replace(title, @"[^\p{L}\p{N}]", "");
    }

    public static Uri ValidateSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo.Length != 0 || u.Query.Length != 0)
            throw new HdlssException(Tx.Get("hdr.err.badSource"));
        bool ok = u.Host == "github.com" && Regex.IsMatch(u.AbsolutePath, @"^/[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+/releases/download/[a-zA-Z0-9_.-]+/renodx-[a-zA-Z0-9_-]+\.addon64$")
                  || Regex.IsMatch(u.Host, @"^[a-zA-Z0-9-]+\.github\.io$") && Regex.IsMatch(u.AbsolutePath, @"^/renodx/renodx-[a-zA-Z0-9_-]+\.addon64$");
        if (!ok) throw new HdlssException(Tx.Get("hdr.err.badSourceHost", u.Host, u.AbsolutePath));
        return u;
    }

    public static bool Official(Uri u) =>
        u.Host == "github.com" && u.AbsolutePath.StartsWith("/clshortfuse/renodx/releases/download/", StringComparison.OrdinalIgnoreCase)
        || (u.Host is "renodx.github.io" or "clshortfuse.github.io") && u.AbsolutePath.StartsWith("/renodx/", StringComparison.Ordinal);

    /// <summary>Finds the single direct x64 download for <paramref name="title"/> in the catalog markdown.</summary>
    public static CatalogMod Resolve(string markdown, string title)
    {
        var key = TitleKey(title);
        var matches = new List<CatalogMod>();
        bool found = false; string? nexus = null;
        CatalogMod? extended = null; bool inExtended = false; string? extendedUrl = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("### Unreal Engine Extended "))
            {
                inExtended = true;
                var link = Regex.Match(line, @"https://[^\s)<>"" ]+/renodx-ue-extended\.addon64");
                extendedUrl = link.Success ? link.Value : null;
                continue;
            }
            if (inExtended && (line.StartsWith("### ") || line.StartsWith("## ") || line.StartsWith("<details>"))) inExtended = false;
            if (inExtended && extendedUrl != null && line.StartsWith('|'))
            {
                var row = line.Split('|');
                if (row.Length >= 5 && TitleKey(row[1]) == key)
                    extended = new CatalogMod(row[1].Trim(), extendedUrl, "Unreal Engine Extended · " + WebUtility.HtmlDecode(row[2].Trim() + " · " + row[3].Trim()),
                        string.Equals(row[3].Trim(), "Engine.ini", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (!line.StartsWith('|')) continue;
            var cells = line.Split('|');
            if (cells.Length < 5 || TitleKey(cells[1]) != key) continue;
            found = true;
            var page = Regex.Match(cells[3], @"https://(?:www\.)?nexusmods\.com/[a-zA-Z0-9_-]+/mods/[0-9]+/?");
            if (page.Success) nexus = page.Value;
            foreach (Match link in Regex.Matches(cells[3], @"https://[^\s)<>""]+"))
            {
                try
                {
                    var u = ValidateSource(link.Value);
                    if (NotHdrStatic(Path.GetFileName(u.AbsolutePath))) continue;
                    matches.Add(new CatalogMod(cells[1].Trim(), u.AbsoluteUri, WebUtility.HtmlDecode(cells[4].Trim()), false));
                }
                catch (HdlssException) { }
            }
        }
        var unique = matches.GroupBy(m => m.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (unique.Count == 0 && extended != null) return extended;
        if (unique.Count == 0 && nexus != null)
            throw new HdlssException(Tx.Get("hdr.err.nexus", title, nexus));
        if (unique.Count == 0)
            throw new HdlssException(found ? Tx.Get("hdr.err.noDirect")
                : Tx.Get("hdr.err.notInCatalog", title));
        if (unique.Count > 1) throw new HdlssException(Tx.Get("hdr.err.several"));
        return unique[0];
    }

    private static bool NotHdrStatic(string name) => Regex.IsMatch(name, "dlss|neural|mfg|framegen", RegexOptions.IgnoreCase);
}

/// <summary>
/// Unreal Engine HDR through Engine.ini / GameUserSettings.ini in Saved/Config. Values are
/// semantic INI edits: removal restores only what HDLSS set, later user edits survive.
/// </summary>
public sealed class UnrealHdrComponent : ComponentBase
{
    public const string ComponentId = "unreal-hdr";
    public const string TelemetryFile = "Mods/HDLSSTelemetry/telemetry.txt";

    public override string Id => ComponentId;
    public override string Name => Tx.Get("uhdr.name");
    public override string Description => Tx.Get("uhdr.desc");
    public override ComponentCategory Category => ComponentCategory.Hdr;
    public override bool IsRelevant(GameContext ctx) => ctx.Game.Engine != EngineKind.Unknown;
    public override IReadOnlyList<ComponentOption> Options =>
    [
        new("profile", Tx.Get("uhdr.opt.profile"), [("native", Tx.Get("uhdr.opt.native")), ("renodx", "RenoDX UE Extended")], "native"),
        new("peak", Tx.Get("uhdr.opt.peak"), [("600", "600 nit"), ("800", "800 nit"), ("1000", "1000 nit"), ("1500", "1500 nit"), ("2000", "2000 nit"), ("4000", "4000 nit")], "1000",
            Custom: new OptionRange(100, 10000, "nit")),
        new("ui", Tx.Get("uhdr.opt.ui"), [("0.75", "75%"), ("1", "100%"), ("1.25", "125%"), ("1.5", "150%")], "1"),
    ];

    private static readonly (string Section, string Key, string Value)[] RenoDxKeys =
    [
        ("SystemSettings", "r.AllowHDR", "1"), ("SystemSettings", "r.HDR.EnableHDROutput", "1"),
        ("SystemSettings", "r.HDR.Display.OutputDevice", "3"), ("SystemSettings", "r.HDR.Display.ColorGamut", "2"),
        ("SystemSettings", "r.HDR.UI.CompositeMode", "1"), ("/Script/Engine.RendererSettings", "r.LUT.UpdateEveryFrame", "1"),
    ];

    public override ComponentStatus Detect(GameContext ctx)
    {
        var status = base.Detect(ctx);
        var telemetry = ReadTelemetry(ctx);
        return telemetry == null ? status : status with { Detail = status.Detail + "\n" + telemetry };
    }

    protected override ComponentStatus? DetectExternal(GameContext ctx) =>
        NativeHdrEnabled(ctx) ? new ComponentStatus(ComponentStatusKind.External, Tx.Get("uhdr.status.native")) : null;

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (ctx.Game.Engine == EngineKind.Unknown) { blockers.Add(Tx.Get("uhdr.block.notUnreal")); return; }
        if (ctx.Game.Engine == EngineKind.Unreal4) blockers.Add(Tx.Get("uhdr.block.ue4"));
        if (ctx.Services.Settings.Profile(ctx.Game.Id).ConfigFolder == null && Config(ctx) == null)
            blockers.Add(Tx.Get("uhdr.block.noConfig"));
        if (!ctx.Installed(Id) && NativeHdrEnabled(ctx)) blockers.Add(Tx.Get("uhdr.block.nativeOn"));
        if (ReShadeAddons.AddonPresent(ctx, "^AutoHDR\\.addon64$")) blockers.Add(Tx.Get("uhdr.block.autoHdr"));
    }

    public override Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var config = Config(ctx) ?? throw new BlockedException(Tx.Get("uhdr.err.noConfig"));
        var profile = Option(options, "profile");
        var peakOption = Options.First(o => o.Key == "peak");
        var peak = int.Parse(peakOption.Normalize(Option(options, "peak"))
            ?? throw new BlockedException(Tx.Get("uhdr.err.peak", peakOption.Custom!.Hint)), CultureInfo.InvariantCulture);
        var ui = Option(options, "ui");
        var plan = ctx.NewPlan(this, profile == "renodx" ? "RenoDX UE Extended" : Tx.Get("uhdr.version.native", peak));
        plan.AddRoot("cfg", config, ctx.Game.Root);
        plan.Meta("config", config);
        if (profile == "renodx")
        {
            if (!ReShadeAddons.AddonPresent(ctx, @"^renodx-ue-extended\.addon64$"))
                throw new BlockedException(Tx.Get("uhdr.err.install"));
            foreach (var (s, k, v) in RenoDxKeys) plan.IniSet("@cfg/Engine.ini", s, k, v);
            plan.IniSet(ctx.Scan.ReShadeConfig, "renodx", "Set_Path", "0");
            plan.Note(Tx.Get("uhdr.note.renodx"));
        }
        else
        {
            var values = new Dictionary<string, string>
            {
                ["r.AllowHDR"] = "1", ["r.HDR.EnableHDROutput"] = "1", ["r.HDR.Display.OutputDevice"] = "5",
                ["r.HDR.Display.ColorGamut"] = "0", ["r.HDR.Display.MaxLuminance"] = peak.ToString(CultureInfo.InvariantCulture),
                ["r.HDR.UI.CompositeMode"] = "1", ["r.HDR.UI.Level"] = ui,
            };
            foreach (var (k, v) in values) plan.IniSet("@cfg/Engine.ini", "SystemSettings", k, v);
            var user = plan.Ini("@cfg/GameUserSettings.ini");
            var sections = Sections(user.ToString());
            if (sections.Count == 0) throw new HdlssException(Tx.Get("uhdr.err.noSection"));
            foreach (var section in sections)
            {
                plan.IniSet("@cfg/GameUserSettings.ini", section, "bUseHDRDisplayOutput", "True");
                plan.IniSet("@cfg/GameUserSettings.ini", section, "HDRDisplayOutputNits", peak.ToString(CultureInfo.InvariantCulture));
            }
            plan.Note(Tx.Get("uhdr.note.windows"));
        }
        plan.Note(Tx.Get("uhdr.note.config", config));
        return Task.FromResult(plan.Build());
    }

    public string? Config(GameContext ctx)
    {
        var saved = ctx.Services.Settings.Profile(ctx.Game.Id).ConfigFolder;
        if (saved != null && File.Exists(Path.Combine(saved, "GameUserSettings.ini"))) return saved;
        if (ctx.State.Get(Id)?.Meta.TryGetValue("config", out var recorded) == true && Directory.Exists(recorded)) return recorded;
        return new GameLibrary(ctx.Services.Settings, ctx.Services.Log).UnrealConfigFolder(ctx.Game);
    }

    private bool NativeHdrEnabled(GameContext ctx)
    {
        var config = Config(ctx);
        if (config == null) return false;
        var path = Path.Combine(config, "GameUserSettings.ini");
        if (!File.Exists(path)) return false;
        var doc = IniDocument.Load(path);
        return Sections(doc.ToString()).Any(s => doc.Get(s, "bUseHDRDisplayOutput") is { } v && (v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1"));
    }

    private static List<string> Sections(string text) =>
        Regex.Matches(text, @"(?m)^\s*\[([^\]\r\n]+)\]\s*\r?$").Select(m => m.Groups[1].Value)
             .Where(s => s.Contains("UserSettings", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();

    /// <summary>Live values written once per second by the UE4SS reader (see Ue4ssComponent).</summary>
    private static string? ReadTelemetry(GameContext ctx)
    {
        foreach (var rel in new[] { TelemetryFile, "ue4ss/" + TelemetryFile })
        {
            var path = PathGuard.Resolve(ctx.Folder, rel);
            if (!File.Exists(path)) continue;
            var age = DateTime.Now - File.GetLastWriteTime(path);
            var values = File.ReadAllLines(path).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!values.ContainsKey("complete")) return null;
            return Tx.Get("uhdr.telemetry", age.TotalMinutes < 2 ? Tx.Get("uhdr.now") : File.GetLastWriteTime(path).ToString("g"), values.GetValueOrDefault("enabled", "?"), values.GetValueOrDefault("max", "?"), values.GetValueOrDefault("ui", "?"));
        }
        return null;
    }
}
