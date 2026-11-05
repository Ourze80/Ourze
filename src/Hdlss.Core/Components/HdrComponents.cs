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
    public override string Description => "Mod HDR specifica per il gioco, cercata per titolo esatto nel catalogo RenoDX. Si regola dal menu ReShade (Home). Non combinarla con altre conversioni HDR o con l'HDR del motore.";
    public override ComponentCategory Category => ComponentCategory.Hdr;
    public override bool NeedsReShade => true;
    public string ImportLabel => "Importa add-on…";
    public string ImportFilter => "Add-on RenoDX|renodx-*.addon64";

    [GeneratedRegex(@"^renodx-[a-zA-Z0-9_.-]+\.addon64$", RegexOptions.IgnoreCase)] private static partial Regex AddonName();
    [GeneratedRegex("dlss|neural|mfg|framegen", RegexOptions.IgnoreCase)] private static partial Regex NotHdr();
    private const string HdrPattern = @"^renodx-(?!dlss|mfg).*\.addon64$";

    public sealed record CatalogMod(string Title, string Url, string Notes, bool EngineRecipe);

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var addon = ReShadeAddons.ForeignAddons(ctx, HdrPattern).FirstOrDefault();
        return addon == null ? null : new ComponentStatus(ComponentStatusKind.External, $"{addon} · installato da altro software");
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        ReShadeAddons.RequireAddonSupport(ctx, blockers);
        if (ReShadeAddons.AddonPresent(ctx, "^AutoHDR\\.addon64$"))
            blockers.Add("AutoHDR presente: rimuovilo, due conversioni HDR non vanno combinate.");
        var foreign = ReShadeAddons.ForeignAddons(ctx, HdrPattern).ToList();
        if (foreign.Count > 0) blockers.Add($"Altro add-on RenoDX HDR presente: {string.Join(", ", foreign)}.");
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
        progress?.Report(new("Cerco la mod nel catalogo RenoDX…"));
        var markdown = await ctx.Services.Http.GetTextAsync(CatalogUrl, cancel, TimeSpan.FromHours(6));
        var mod = Resolve(markdown, ctx.Game.Name);
        var uri = ValidateSource(mod.Url);
        var name = Path.GetFileName(uri.AbsolutePath);
        string url = mod.Url; string? sha = null;
        if (uri.Host == "github.com")
        {
            var parts = uri.AbsolutePath.Split('/');
            var repo = parts[1] + "/" + parts[2];
            using var json = await ctx.Services.Http.GetJsonAsync(GitHub.ReleasesUrl(repo, 20), cancel, TimeSpan.FromMinutes(30));
            var asset = GitHubRelease.ParseList(json.RootElement).Where(r => !r.Draft)
                .SelectMany(r => r.Assets.Select(a => (r, a)))
                .Where(x => string.Equals(x.a.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.r.Published).Select(x => x.a).FirstOrDefault()
                ?? throw new HdlssException("Nessuna pubblicazione corrente per questa mod RenoDX.");
            if (!asset.Url.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.Ordinal)) throw new HdlssException("Origine RenoDX non valida.");
            url = asset.Url; sha = asset.Sha256;
        }
        var file = await ctx.Services.Http.DownloadAsync(url, name, sha, 64_000_000, progress, cancel);
        if (!PeFile.IsPe64(file)) throw new HdlssException("Il download non contiene un add-on x64 valido.");

        var plan = ctx.NewPlan(this, $"{mod.Title} · {name}");
        plan.Meta("source", url);
        if (!Official(new Uri(url))) plan.Note($"⚠ Origine non ufficiale (fork RenoDX): {new Uri(url).Host}{new Uri(url).AbsolutePath}. Applica solo se ti fidi dell'autore.");
        plan.AddFile(ReShadeAddons.AddonTarget(ctx, name), file);
        if (mod.Notes.Length > 0) plan.Note("Note del catalogo: " + mod.Notes);
        if (mod.EngineRecipe) plan.Note("Questa mod richiede anche «HDR Unreal (Engine.ini)» con profilo RenoDX.");
        plan.Note("Avvia il gioco e regola l'HDR dal menu ReShade (Home), seguendo le istruzioni dell'autore.");
        return plan.Build();
    }

    public Task<ChangePlan> PlanImportAsync(GameContext ctx, string file, IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var name = Path.GetFileName(file);
        if (!AddonName().IsMatch(name) || !PeFile.IsPe64(file)) throw new HdlssException("Scegli un add-on RenoDX x64 originale (renodx-….addon64).");
        if (NotHdr().IsMatch(name)) throw new HdlssException("Questo è un add-on NR/MFG: usa il componente dedicato.");
        var plan = ctx.NewPlan(this, $"{name} · importato");
        plan.Meta("source", "file locale");
        plan.AddFile(ReShadeAddons.AddonTarget(ctx, name), file);
        plan.Note("Add-on importato manualmente: segui le istruzioni del suo autore.");
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
            throw new HdlssException("Origine RenoDX non valida.");
        bool ok = u.Host == "github.com" && Regex.IsMatch(u.AbsolutePath, @"^/[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+/releases/download/[a-zA-Z0-9_.-]+/renodx-[a-zA-Z0-9_-]+\.addon64$")
                  || Regex.IsMatch(u.Host, @"^[a-zA-Z0-9-]+\.github\.io$") && Regex.IsMatch(u.AbsolutePath, @"^/renodx/renodx-[a-zA-Z0-9_-]+\.addon64$");
        if (!ok) throw new HdlssException($"Origine RenoDX non valida: {u.Host}{u.AbsolutePath}");
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
            throw new HdlssException($"{title}: la mod è pubblicata su Nexus Mods senza download diretto. Scaricala dalla pagina dell'autore e usa «Importa add-on…».\n{nexus}");
        if (unique.Count == 0)
            throw new HdlssException(found ? "La mod è nel catalogo ma senza download diretto x64. Consulta le istruzioni dell'autore."
                : $"Nel catalogo RenoDX non c'è una mod HDR per «{title}». Se ne hai scaricata una a parte (ad es. da Nexus Mods), usa «Importa add-on…». Demo ed edizioni diverse non vengono abbinate automaticamente.");
        if (unique.Count > 1) throw new HdlssException("Il catalogo propone più mod per questo titolo: scarica quella adatta e usa «Importa add-on…».");
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
    public override string Name => "HDR Unreal (Engine.ini)";
    public override string Description => "Attiva l'HDR del motore nei giochi Unreal tramite Engine.ini. Profilo «Nativo» per giochi senza menu HDR; profilo «RenoDX» per RenoDX Unreal Engine Extended. Avvia il gioco una volta prima di applicarlo.";
    public override ComponentCategory Category => ComponentCategory.Hdr;
    public override bool IsRelevant(GameContext ctx) => ctx.Game.Engine != EngineKind.Unknown;
    public override IReadOnlyList<ComponentOption> Options =>
    [
        new("profile", "Profilo", [("native", "Nativo (scRGB, ACES)"), ("renodx", "RenoDX UE Extended")], "native"),
        new("peak", "Luminanza massima", [("600", "600 nit"), ("800", "800 nit"), ("1000", "1000 nit"), ("1500", "1500 nit"), ("2000", "2000 nit"), ("4000", "4000 nit")], "1000"),
        new("ui", "Luminosità interfaccia", [("0.75", "75%"), ("1", "100%"), ("1.25", "125%"), ("1.5", "150%")], "1"),
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
        NativeHdrEnabled(ctx) ? new ComponentStatus(ComponentStatusKind.External, "HDR nativo già attivo nelle impostazioni del gioco") : null;

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (ctx.Game.Engine == EngineKind.Unknown) { blockers.Add("Disponibile solo per giochi Unreal Engine."); return; }
        if (ctx.Game.Engine == EngineKind.Unreal4) blockers.Add("Unreal Engine 4: profilo non verificato, usa una mod RenoDX dedicata.");
        if (ctx.Services.Settings.Profile(ctx.Game.Id).ConfigFolder == null && Config(ctx) == null)
            blockers.Add("Cartella Saved/Config non trovata: avvia il gioco una volta, poi riprova.");
        if (!ctx.Installed(Id) && NativeHdrEnabled(ctx)) blockers.Add("HDR nativo già attivo nel gioco: usa il suo menu HDR.");
        if (ReShadeAddons.AddonPresent(ctx, "^AutoHDR\\.addon64$")) blockers.Add("AutoHDR presente: rimuovilo prima.");
    }

    public override Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var config = Config(ctx) ?? throw new BlockedException("Cartella Saved/Config non trovata.");
        var profile = Option(options, "profile");
        var peak = int.Parse(Option(options, "peak"), CultureInfo.InvariantCulture);
        var ui = Option(options, "ui");
        var plan = ctx.NewPlan(this, profile == "renodx" ? "RenoDX UE Extended" : $"Nativo · {peak} nit");
        plan.AddRoot("cfg", config, ctx.Game.Root);
        plan.Meta("config", config);
        if (profile == "renodx")
        {
            if (!ReShadeAddons.AddonPresent(ctx, @"^renodx-ue-extended\.addon64$"))
                throw new BlockedException("Installa prima RenoDX Unreal Engine Extended (RenoDX HDR) per questo gioco.");
            foreach (var (s, k, v) in RenoDxKeys) plan.IniSet("@cfg/Engine.ini", s, k, v);
            plan.IniSet(ctx.Scan.ReShadeConfig, "renodx", "Set_Path", "0");
            plan.Note("Profilo RenoDX: regola luminanza e colore dal menu RenoDX in ReShade.");
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
            if (sections.Count == 0) throw new HdlssException("GameUserSettings.ini senza sezione UserSettings: avvia il gioco e riprova.");
            foreach (var section in sections)
            {
                plan.IniSet("@cfg/GameUserSettings.ini", section, "bUseHDRDisplayOutput", "True");
                plan.IniSet("@cfg/GameUserSettings.ini", section, "HDRDisplayOutputNits", peak.ToString(CultureInfo.InvariantCulture));
            }
            plan.Note("Attiva l'HDR di Windows prima di avviare il gioco. Calibra il picco con «Windows HDR Calibration».");
        }
        plan.Note($"File di configurazione: {config}. La presenza dei valori non garantisce l'uscita HDR: verifica in gioco (UE4SS + lettore mostra i valori attivi).");
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
            return $"In gioco ({(age.TotalMinutes < 2 ? "ora" : File.GetLastWriteTime(path).ToString("g"))}): HDR {values.GetValueOrDefault("enabled", "?")} · picco {values.GetValueOrDefault("max", "?")} nit · UI {values.GetValueOrDefault("ui", "?")}";
        }
        return null;
    }
}
