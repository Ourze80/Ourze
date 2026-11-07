using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>MFG Unlock: multi frame generation on RTX 4000 for games with native DLSS-G.</summary>
public sealed class MfgUnlockComponent : ComponentBase
{
    public const string ComponentId = "mfg-unlock";
    public const string AddonName = "renodx-mfgunlock.addon64";
    private const string Repo = "mavismmg/MFGAdaUnlock-RenoDx";
    public override string Id => ComponentId;

    public override Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        Releases.LatestTagAsync(ctx, Repo, cancel);
    public override string Name => "MFG Unlock (RTX 4000)";
    public override string Description => "Sblocca la generazione multi-fotogramma su RTX 4000 nei giochi con DLSS Frame Generation nativo. Attiva FG nel gioco e scegli il moltiplicatore in ReShade › MFG Unlock. Dipende da gioco, driver e versione DLSS-G.";
    public override ComponentCategory Category => ComponentCategory.FrameGeneration;
    public override bool NeedsReShade => true;

    protected override ComponentStatus? DetectExternal(GameContext ctx) =>
        ReShadeAddons.ForeignAddons(ctx, "^renodx-mfgunlock\\.addon64$").Any() ? new ComponentStatus(ComponentStatusKind.External, $"{AddonName} · installato da altro software") : null;

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        ReShadeAddons.RequireAddonSupport(ctx, blockers);
        if (ctx.Gpu.Series == 50) blockers.Add("Mod non necessaria: le RTX 5000 hanno già MFG in modo nativo. Non c'è nulla da installare.");
        else if (ctx.Gpu.Series != 40) blockers.Add("Serve una GPU RTX 4000: questa mod aggiunge MFG alle RTX 4000 e non funziona con altre schede. Non è disponibile su questo PC.");
        var others = ReShadeAddons.ForeignAddons(ctx, "mfg|framegen").Where(a => !Path.GetFileName(a).Equals(AddonName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0) blockers.Add($"Altro add-on MFG presente: due add-on MFG insieme si disturbano ({string.Join(", ", others)}). Rimuovi quello dell'altro programma.");
        if (!HasFrameGeneration(ctx))
            blockers.Add("DLSS Frame Generation assente nel gioco: la mod ne ha bisogno e non trova nvngx_dlssg.dll né sl.dlss_g.dll. Serve un gioco che la supporti.");
    }

    /// <summary>
    /// DLSS Frame Generation anywhere in the install: Unreal games keep it in the Streamline plugin
    /// (Engine/Plugins/Runtime/Nvidia/StreamlineCore/Binaries/ThirdParty/Win64), not next to the exe.
    /// </summary>
    internal static bool HasFrameGeneration(GameContext ctx) =>
        File.Exists(PathGuard.Resolve(ctx.Folder, "sl.dlss_g.dll"))
        || DlssFiles.Find(ctx.Game.Root).Any(f => Path.GetFileName(f).Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(Path.GetDirectoryName(f)!, "sl.dlss_g.dll")));

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var (release, asset, file) = await Releases.LatestAsset(ctx, Repo, a => a.Name == AddonName, 32_000_000, requireDigest: true, progress, cancel);
        if (!PeFile.IsPe64(file) || asset.Size < 4096) throw new HdlssException("MFG Unlock: add-on non valido.");
        var plan = ctx.NewPlan(this, release.Tag);
        plan.Meta("source", asset.Url);
        plan.AddFile(ReShadeAddons.AddonTarget(ctx, AddonName), file);
        plan.IniListAdd(ctx.Scan.ReShadeConfig, "ADDON", "LoadFromDllMain", AddonName);
        plan.Note("I file NVIDIA/Streamline del gioco non vengono modificati.");
        return plan.Build();
    }
}

/// <summary>Ultimate ASI Loader x64 under the DLL name required by the ASI mod.</summary>
public sealed partial class AsiLoaderComponent : ComponentBase
{
    public const string ComponentId = "asi-loader";
    private const string Repo = "ThirteenAG/Ultimate-ASI-Loader";
    private const string AssetName = "Ultimate-ASI-Loader-NoPDB_x64.zip";
    public static readonly string[] Names = ["dinput8.dll", "version.dll", "winmm.dll", "winhttp.dll", "wininet.dll", "dsound.dll", "dxgi.dll", "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "binkw64.dll", "bink2w64.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll"];

    public override string Id => ComponentId;
    public override string Name => "Ultimate ASI Loader";
    public override string Description => "Caricatore per mod .asi (ultima stabile dell'autore). Scegli il nome DLL richiesto dalla mod. Non installa mod ASI.";
    public override ComponentCategory Category => ComponentCategory.Loaders;
    public override IReadOnlyList<ComponentOption> Options => [new("name", "Nome DLL", Names.Select(n => (n, n)).ToList(), "dinput8.dll")];

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")] private static partial Regex StableTag();

    public static bool IsLoader(string path) =>
        File.Exists(path) && PeFile.IsPe64(path) && PeFile.Identity(path).Contains("Ultimate ASI Loader", StringComparison.OrdinalIgnoreCase);

    public static List<string> Present(GameContext ctx) => Names.Where(n => IsLoader(PathGuard.Resolve(ctx.Folder, n))).ToList();

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var tag = await Releases.LatestTagAsync(ctx, Repo, cancel, StableTag().IsMatch);
        var name = ctx.State.Get(Id)?.Files.FirstOrDefault()?.Path ?? Option(options, "name");
        return tag == null ? null : $"{tag} · {name}";
    }

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var present = Present(ctx);
        return present.Count == 0 ? null : new ComponentStatus(ComponentStatusKind.External, $"Presente come {string.Join(", ", present)} · {PeFile.FileVersion(PathGuard.Resolve(ctx.Folder, present[0]))}");
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        var present = Present(ctx);
        if (present.Count > 1) blockers.Add($"Più ASI Loader presenti: il gioco ne carica uno solo ({string.Join(", ", present)}). Tieni un solo loader e rimuovi gli altri.");
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var present = Present(ctx);
        var name = ctx.State.Get(Id)?.Files.FirstOrDefault()?.Path ?? (present.Count == 1 ? present[0] : Option(options, "name"));
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new HdlssException("Nome DLL non supportato.");
        var target = PathGuard.Resolve(ctx.Folder, name);
        if (File.Exists(target) && !IsLoader(target)) throw new BlockedException($"{name} appartiene a un altro componente: scegli un altro nome.");

        var (release, asset, zip) = await Releases.LatestAsset(ctx, Repo, a => a.Name == AssetName, 20_000_000, requireDigest: true, progress, cancel, StableTag().IsMatch);
        var temp = Releases.TempDir(ctx);
        try
        {
            Archives.Extract(zip, temp, n => n == "dinput8.dll", 16_000_000);
            var dll = Path.Combine(temp, "dinput8.dll");
            if (!IsLoader(dll)) throw new HdlssException("ASI Loader: libreria x64 non valida nel pacchetto.");
            var plan = ctx.NewPlan(this, $"{release.Tag} · {name}");
            plan.Meta("source", asset.Url);
            plan.Meta("name", name);
            plan.AddFile(name, dll);
            if (present.Count == 1 && ctx.State.Get(Id) == null) plan.Note($"ASI Loader esistente ({name}) aggiornato: la versione precedente resta nel backup.");
            plan.Note("Le mod .asi e le loro impostazioni non vengono toccate.");
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }
}

/// <summary>REFramework loader for RE Engine games (nightly; legacy TDB builds for RE2/RE3/RE7 non-RT).</summary>
public sealed class ReFrameworkComponent : ComponentBase
{
    public const string ComponentId = "reframework";
    private static readonly Dictionary<string, string> GameIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["re2"] = "RE2", ["bhd2"] = "RE2", ["re3"] = "RE3", ["bhd3"] = "RE3", ["re4"] = "RE4", ["bhd4"] = "RE4", ["re7"] = "RE7", ["bhd7"] = "RE7",
        ["re8"] = "RE8", ["village"] = "RE8", ["re9"] = "RE9", ["devilmaycry5"] = "DMC5", ["dmc5"] = "DMC5", ["monsterhunterrise"] = "MHRISE",
        ["mhrise"] = "MHRISE", ["mhrisesunbreakdemo"] = "MHRISE", ["streetfighter6"] = "SF6", ["sf6"] = "SF6", ["dd2"] = "DD2", ["drdr"] = "DRDR",
        ["makaimura_gg_re"] = "GGR", ["makaimura"] = "GGR", ["gs456"] = "GS456", ["kunitsugami"] = "KUNITSU", ["onimusha2"] = "ONIMUSHA2",
        ["monsterhunterwilds"] = "MHWILDS", ["mhwilds"] = "MHWILDS", ["monster_hunter_stories_3_twisted_reflection_trial"] = "MHSTORIES3",
        ["monster_hunter_stories_3_twisted_reflection"] = "MHSTORIES3", ["starforce"] = "STARFORCE", ["megamanstarforce"] = "STARFORCE",
        ["mmstarforce"] = "STARFORCE", ["pragmata"] = "PRAGMATA", ["onimushawots"] = "ONIMUSHA_WOTS", ["onimushawots_demo"] = "ONIMUSHA_WOTS",
    };

    public override string Id => ComponentId;
    public override string Name => "REFramework";
    public override string Description => "Loader per mod e script dei giochi RE Engine (Capcom). Installalo solo se una mod lo richiede. Versione corrente: nightly ufficiale; edizioni senza ray tracing di RE2/RE3/RE7: pacchetto TDB stabile.";
    public override ComponentCategory Category => ComponentCategory.Loaders;
    public override bool IsRelevant(GameContext ctx) => Identify(ctx.Game) != null;
    public override IReadOnlyList<ComponentOption> Options =>
        [new("edition", "Edizione", [("current", "Corrente / ray tracing"), ("legacy", "Legacy senza ray tracing (RE2/RE3/RE7)")], "current")];

    public static string? Identify(Game game) =>
        GameIds.TryGetValue(Path.GetFileNameWithoutExtension(game.Exe), out var id) ? id : null;

    public static bool Recognized(string path) =>
        PeFile.IsPe64(path) && new FileInfo(path).Length < 80_000_000 && PeFile.ContainsAscii(path, "REFramework") && PeFile.ContainsAscii(path, "re2_framework_log");

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        var dll = PathGuard.Resolve(ctx.Folder, "dinput8.dll");
        return File.Exists(dll) && Recognized(dll) ? new ComponentStatus(ComponentStatusKind.External, $"Presente · {PeFile.FileVersion(dll)}") : null;
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (Identify(ctx.Game) == null) { blockers.Add("Gioco RE Engine non riconosciuto: REFramework funziona solo con alcuni giochi RE Engine e l'eseguibile scelto non risulta tra questi. Controlla l'eseguibile in «Eseguibile»."); return; }
        var dll = PathGuard.Resolve(ctx.Folder, "dinput8.dll");
        if (File.Exists(dll) && !Recognized(dll) && ctx.State.OwnerOf("dinput8.dll") is not ComponentId)
            blockers.Add("dinput8.dll già usata da un altro loader: REFramework ha bisogno dello stesso file (per esempio c'è l'ASI Loader). Rimuovi prima l'altro loader.");
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var id = Identify(ctx.Game);
        if (id == null) return null;
        var tag = await Releases.LatestTagAsync(ctx, Option(options, "edition") == "legacy" ? "praydog/REFramework" : "praydog/REFramework-nightly", cancel);
        return tag == null ? null : $"{id} · {tag}";
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var id = Identify(ctx.Game)!;
        bool legacy = Option(options, "edition") == "legacy";
        if (legacy && id is not ("RE2" or "RE3" or "RE7")) throw new HdlssException("L'edizione legacy esiste solo per RE2, RE3 e RE7.");
        var repo = legacy ? "praydog/REFramework" : "praydog/REFramework-nightly";
        var assetName = legacy ? id switch { "RE2" => "RE2_TDB66.zip", "RE3" => "RE3_TDB67.zip", _ => "RE7_TDB49.zip" } : "REFramework.zip";
        var (release, asset, zip) = await Releases.LatestAsset(ctx, repo, a => a.Name == assetName, 80_000_000, requireDigest: !legacy, progress, cancel);
        var temp = Releases.TempDir(ctx);
        try
        {
            Archives.Extract(zip, temp, n => n == "dinput8.dll", 80_000_000);
            var dll = Path.Combine(temp, "dinput8.dll");
            if (!File.Exists(dll) || !Recognized(dll)) throw new HdlssException("REFramework: pacchetto non valido.");
            var plan = ctx.NewPlan(this, $"{id} · {release.Tag}");
            plan.Meta("source", asset.Url);
            plan.AddFile("dinput8.dll", dll, sameKind: Recognized);
            plan.Note("Script e impostazioni di REFramework (cartella reframework) restano invariati alla rimozione.");
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }
}

/// <summary>UE4SS (official stable) for Unreal mods, with an optional read-only HDR CVar reader.</summary>
public sealed partial class Ue4ssComponent : ComponentBase
{
    public const string ComponentId = "ue4ss";
    public const string ReaderMod = "HDLSSTelemetry";

    public override string Id => ComponentId;
    public override string Name => "UE4SS";
    public override string Description => "Script system per mod Unreal (ultima stabile ufficiale). Il «lettore HDLSS» opzionale legge una volta al secondo i valori HDR attivi in gioco, senza modificarli, e li mostra nella scheda HDR Unreal.";
    public override ComponentCategory Category => ComponentCategory.Loaders;
    public override bool IsRelevant(GameContext ctx) => ctx.Game.Engine != EngineKind.Unknown;
    public override IReadOnlyList<ComponentOption> Options => [new("reader", "Lettore HDR HDLSS", [("on", "Includi"), ("off", "Non includere")], "on")];

    [GeneratedRegex(@"^UE4SS_v[0-9]+\.[0-9]+\.[0-9]+\.zip$")] private static partial Regex PackageName();

    public override Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        Releases.LatestTagAsync(ctx, "UE4SS-RE/RE-UE4SS", cancel);

    protected override ComponentStatus? DetectExternal(GameContext ctx)
    {
        foreach (var rel in new[] { "UE4SS.dll", "ue4ss/UE4SS.dll" })
        {
            var dll = PathGuard.Resolve(ctx.Folder, rel);
            if (File.Exists(dll)) return new ComponentStatus(ComponentStatusKind.External, $"Presente ({rel}) · {PeFile.FileVersion(dll)}");
        }
        return null;
    }

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        if (ctx.Game.Engine == EngineKind.Unknown) blockers.Add("UE4SS serve solo per Unreal Engine: questo gioco non risulta fatto con Unreal. Se lo è, scegli l'eseguibile in Binaries/Win64 da «Eseguibile».");
        if (File.Exists(PathGuard.Resolve(ctx.Folder, "override.txt"))) blockers.Add("Installazione UE4SS personalizzata: c'è un file override.txt, quindi HDLSS5 non tocca nulla. Gestiscila a mano.");
        bool flat = File.Exists(PathGuard.Resolve(ctx.Folder, "UE4SS.dll")), nested = File.Exists(PathGuard.Resolve(ctx.Folder, "ue4ss/UE4SS.dll"));
        if (flat && nested) blockers.Add("Due copie di UE4SS nel gioco: una in ue4ss\\ e una nella cartella del gioco. Tienine una sola: rimuovi quella che non usi, da questa scheda se l'ha installata HDLSS5, altrimenti con il programma che l'ha installata.");
        if (!ctx.Installed(Id))
        {
            if (flat || nested) blockers.Add("UE4SS già presente: è stato installato a mano o da un altro programma (per esempio UE Mod Manager). Aggiornalo con lo stesso metodo.");
            else if (File.Exists(PathGuard.Resolve(ctx.Folder, "dwmapi.dll"))) blockers.Add("dwmapi.dll già occupata da un altro loader: UE4SS usa lo stesso file e HDLSS5 non lo sovrascrive. Rimuovi prima l'altro loader.");
        }
        else if (ChangedByOthers(ctx) is { } rel)
            blockers.Add($"UE4SS ({rel}) modificato da un altro programma: per esempio UE Mod Manager, con la sua build per questo gioco. HDLSS5 non lo sovrascrive. Aggiornalo con quel programma.");
    }

    /// <summary>
    /// A recorded runtime or proxy whose content is no longer what HDLSS5 wrote: another manager
    /// (UE Mod Manager ships game-specific UE4SS builds) has taken over the install.
    /// </summary>
    public override string? TakenOverBy(GameContext ctx) => ChangedByOthers(ctx);

    internal string? ChangedByOthers(GameContext ctx)
    {
        if (ctx.State.Get(Id) is not { } record) return null;
        foreach (var rel in new[] { "dwmapi.dll", "UE4SS.dll", "ue4ss/UE4SS.dll" })
        {
            var full = PathGuard.Resolve(ctx.Folder, rel);
            if (record.FindFile(rel) is { } f && File.Exists(full)
                && !string.Equals(FileHash.OfFile(full), f.After, StringComparison.OrdinalIgnoreCase)) return rel;
        }
        return null;
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var (release, asset, zip) = await Releases.LatestAsset(ctx, "UE4SS-RE/RE-UE4SS", a => PackageName().IsMatch(a.Name), 150_000_000, requireDigest: false, progress, cancel);
        var temp = Releases.TempDir(ctx);
        try
        {
            var files = Archives.Extract(zip, temp, n =>
                n is "UE4SS.dll" or "dwmapi.dll" or "UE4SS-settings.ini"
                || n.StartsWith("Mods/", StringComparison.Ordinal) || n.StartsWith("ue4ss/", StringComparison.Ordinal), 60_000_000, 150_000_000);
            if (!files.Contains("dwmapi.dll") || !(files.Contains("UE4SS.dll") || files.Contains("ue4ss/UE4SS.dll")))
                throw new HdlssException("Pacchetto UE4SS senza runtime o loader.");
            foreach (var dll in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                if (!PeFile.IsPe64(PathGuard.Resolve(temp, dll))) throw new HdlssException($"UE4SS: {dll} non è x64.");
            CheckLayout(ctx, files);

            var plan = ctx.NewPlan(this, release.Tag);
            plan.Meta("source", asset.Url);
            var record = ctx.State.Get(Id);
            int preserved = 0;
            foreach (var rel in files)
            {
                var isConfig = !rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                // Existing mod/config files belong to the user: never overwrite one we don't own.
                if (isConfig && record?.FindFile(rel) == null && File.Exists(plan.Full(rel))) { preserved++; continue; }
                plan.AddFile(rel, PathGuard.Resolve(temp, rel), keepIfModified: isConfig);
            }
            if (preserved > 0) plan.Note($"{preserved} file di mod/configurazione già presenti conservati.");

            if (Option(options, "reader") == "on")
            {
                var mods = files.Any(f => f.StartsWith("ue4ss/", StringComparison.Ordinal)) ? "ue4ss/Mods" : "Mods";
                var script = Path.Combine(ctx.Services.Paths.Bundled, "hdlss-telemetry.lua");
                if (!File.Exists(script)) throw new HdlssException("bundled/hdlss-telemetry.lua mancante.");
                plan.AddFile($"{mods}/{ReaderMod}/Scripts/main.lua", script);
                plan.AddBytes($"{mods}/{ReaderMod}/enabled.txt", []);
                plan.Note("Lettore HDR attivo: dopo l'avvio del gioco i valori compaiono nella scheda «HDR Unreal».");
            }
            plan.Note("Compatibilità specifica del gioco non garantita: controlla UE4SS.log dopo l'avvio.");
            return plan.Build();
        }
        finally { Releases.Cleanup(temp); }
    }

    /// <summary>
    /// The package's runtime must go where the installed one is: a flat package over a nested
    /// install (ue4ss\UE4SS.dll, e.g. a game-specific build) would replace the proxy and leave two
    /// copies, and the game would start the wrong one.
    /// </summary>
    internal static void CheckLayout(GameContext ctx, IReadOnlyCollection<string> packageFiles)
    {
        var runtime = packageFiles.Contains("ue4ss/UE4SS.dll") ? "ue4ss/UE4SS.dll" : "UE4SS.dll";
        var other = runtime == "UE4SS.dll" ? "ue4ss/UE4SS.dll" : "UE4SS.dll";
        if (File.Exists(PathGuard.Resolve(ctx.Folder, other)))
            throw new BlockedException($"UE4SS è installato in {other}, il pacchetto ufficiale lo mette in {runtime}: installarlo lascerebbe due copie e il gioco avvierebbe quella sbagliata. Se vuoi davvero passare alla versione ufficiale, rimuovi prima UE4SS.");
    }
}
