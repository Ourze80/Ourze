using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>OptiScaler (official, no NR) or the F5 · Janblade fork with multipass NR.</summary>
public sealed partial class OptiScalerComponent : ComponentBase
{
    public const string ComponentId = "optiscaler";
    private const string OfficialRepo = "optiscaler/OptiScaler";
    private const string F5Repo = "janblade/OptiScaler-F5-DLSSNR-Multipass";

    public override string Id => ComponentId;
    public override string Name => "OptiScaler";
    public override string Description => "Upscaler/NR come proxy DLL. F5 · Janblade aggiunge Neural Rendering multipass (configurabile nel menu in gioco, Insert). Standard: solo upscaling, NR spento.";
    public override IReadOnlyList<ComponentOption> Options =>
    [
        new("build", "Build", [("f5", "F5 · Janblade · NR Multipass"), ("official", "Standard · senza NR")], "f5"),
        MenuKeys.Option(MenuKeys.OptiScalerDefault),
    ];

    [GeneratedRegex(@"^optiscaler-dlssnr-f5-v?[0-9]+(?:\.[0-9]+)*(?:-with-dlss-fg)?\.zip$", RegexOptions.IgnoreCase)] private static partial Regex F5Asset();

    private static readonly HashSet<string> OfficialFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_vk.dll", "dlssg_to_fsr3_amd_is_better.dll", "fakenvapi.dll", "libxell.dll", "libxess.dll", "libxess_dx11.dll",
        "libxess_fg.dll", "D3D12_Optiscaler/D3D12Core.dll",
    };

    protected override ComponentStatus? DetectExternal(GameContext ctx) => ctx.Scan.OptiScalerProxies.Count switch
    {
        0 => null,
        1 => new ComponentStatus(ComponentStatusKind.External, $"OptiScaler presente come {ctx.Scan.OptiScalerProxy} · installato da altro software"),
        _ => new ComponentStatus(ComponentStatusKind.Blocked, "Più copie di OptiScaler: " + string.Join(", ", ctx.Scan.OptiScalerProxies)),
    };

    /// <summary>F5 runs a Neural Rendering pass; Standard does not. Imported installs without a build are treated as NR (safe side).</summary>
    public static bool IsNrBuild(ComponentRecord r) => r.Meta.GetValueOrDefault("build", "f5") != "official";

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        // The single-NR rule applies to the F5 build only: checked in PlanInstallAsync where the build is known.
        var managed = ctx.State.Get(Id);
        var foreign = ctx.Scan.OptiScalerProxies.Where(p => managed?.FindFile(p) == null).ToList();
        if (foreign.Count > 0) blockers.Add($"OptiScaler di un altro programma ({string.Join(", ", foreign)}): HDLSS5 non lo gestisce ancora. Usa «Importa in HDLSS5» per gestirlo da qui.");
        if (ctx.Api == GraphicsApi.Unknown) blockers.Add("API grafica non riconosciuta: HDLSS5 non ha capito se il gioco usa DX11, DX12 o Vulkan. Impostala in «Eseguibile e API».");
    }

    private static async Task<(GitHubRelease? Release, ReleaseAsset? Asset)> FindReleaseAsync(GameContext ctx, bool f5, CancellationToken cancel)
    {
        using var json = await ctx.Services.Http.GetJsonAsync(GitHub.ReleasesUrl(f5 ? F5Repo : OfficialRepo, 10), cancel, TimeSpan.FromMinutes(30));
        foreach (var r in GitHubRelease.ParseList(json.RootElement).Where(r => !r.Draft && !r.Prerelease).OrderByDescending(r => r.Published))
        {
            var candidates = r.Assets.Where(a => f5 ? F5Asset().IsMatch(a.Name)
                : a.Name.Contains("optiscaler", StringComparison.OrdinalIgnoreCase) && (a.Name.EndsWith(".zip") || a.Name.EndsWith(".7z")) && !a.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name.Contains("with-dlss", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 0) return (r, candidates[0]);
        }
        return (null, null);
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        bool f5 = Option(options, "build") == "f5";
        var (release, _) = await FindReleaseAsync(ctx, f5, cancel);
        return release == null ? null : $"{(f5 ? "F5" : "Standard")} · {release.Tag}";
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var build = Option(options, "build");
        var f5 = build == "f5";
        var repo = f5 ? F5Repo : OfficialRepo;
        var menuKey = Option(options, MenuKeys.OptionKey);
        var key = MenuKeys.Parse(menuKey) ?? MenuKeys.OptiScalerKey(ctx);
        if (ctx.Installed(ReShadeComponent.ComponentId) && ctx.Api != GraphicsApi.Vulkan && key == MenuKeys.ReShadeKey(ctx))
            throw new BlockedException(MenuKeys.ConflictMessage(key));
        if (f5)
        {
            var nrBlock = new List<string>();
            NrRules.CheckGpu(ctx, nrBlock);
            NrRules.CheckSingleInjector(ctx, Id, nrBlock);
            if (nrBlock.Count > 0) throw new BlockedException(string.Join("\n", nrBlock));
        }

        progress?.Report(new($"Cerco l'ultima release stabile di {repo}…"));
        var (release, asset) = await FindReleaseAsync(ctx, f5, cancel);
        if (release == null || asset == null) throw new HdlssException("Nessun pacchetto OptiScaler compatibile nelle ultime release.");
        if (!GitHubRelease.FromRepo(asset, repo, release.Tag)) throw new HdlssException("Origine del pacchetto non valida.");
        if (asset.Sha256 == null) ctx.Services.Log.Warn($"{asset.Name}: la release non pubblica uno SHA-256, verifica solo tramite HTTPS.");

        var archive = await ctx.Services.Http.DownloadAsync(asset.Url, asset.Name, asset.Sha256, 512L << 20, progress, cancel);
        var temp = Path.Combine(ctx.Services.Paths.Temp, Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new("Estrazione e verifica del pacchetto…"));
            Archives.Extract(archive, temp);
            var mains = Directory.GetFiles(temp, "OptiScaler.dll", SearchOption.AllDirectories);
            if (mains.Length != 1 || !FolderScan.IsOptiScaler(mains[0])) throw new HdlssException("Il pacchetto non contiene un unico OptiScaler.dll x64.");
            var package = Path.GetDirectoryName(mains[0])!;
            var packageIni = Path.Combine(package, "OptiScaler.ini");
            if (!File.Exists(packageIni)) throw new HdlssException("OptiScaler.ini mancante nel pacchetto.");

            var managed = ctx.State.Get(Id);
            var proxy = managed?.Files.Select(f => f.Path).FirstOrDefault(p => FolderScan.ProxyNames.Contains(p, StringComparer.OrdinalIgnoreCase))
                        ?? FolderScan.ProxyNames.FirstOrDefault(p => !File.Exists(PathGuard.Resolve(ctx.Folder, p)))
                        ?? throw new BlockedException("Nessun nome di proxy DLL libero nella cartella del gioco.");

            var plan = ctx.NewPlan(this, $"{(f5 ? "F5" : "Standard")} · {release.Tag}");
            plan.Meta("build", build);
            plan.Meta("source", asset.Url);
            plan.Meta(MenuKeys.OptionKey, menuKey);
            foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
            {
                var rel = PathGuard.RelativeTo(package, file);
                if (!Allowed(rel, f5)) continue;
                plan.AddFile(rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxy : rel, file);
            }

            // Settings: generated into a fresh ini, or applied as reversible edits to an existing one.
            var iniPath = PathGuard.Resolve(ctx.Folder, "OptiScaler.ini");
            var ownsIni = managed?.FindFile("OptiScaler.ini") != null;
            var settings = new (string Section, string Key, string Value)[]
            {
                ("DlssNr", "Enabled", f5 ? "true" : "false"),
                ("Hotfix", "CheckForUpdate", "false"),
                ("Log", "LogToFile", "true"),
            }.ToList();
            // With ReShade managed here, OptiScaler becomes its loader (ReShade moves to ReShade64.dll right after).
            if (ctx.Installed(ReShadeComponent.ComponentId) && ctx.Api != GraphicsApi.Vulkan)
                settings.Add(("Plugins", "LoadReshade", "true"));
            var menuCode = MenuKeys.Parse(menuKey);
            if (!File.Exists(iniPath) || ownsIni)
            {
                var doc = IniDocument.Load(File.Exists(iniPath) ? iniPath : packageIni);
                foreach (var (s, k, v) in settings) doc.Set(s, k, v);
                // "Default" goes back to OptiScaler's own (auto = Insert) only if HDLSS5 changed the key
                // before; otherwise a key picked in OptiScaler's menu stays.
                if (menuCode != null) doc.Set("Menu", "ShortcutKey", MenuKeys.Hex(menuCode.Value));
                else if (MenuKeys.Parse(managed?.Meta.GetValueOrDefault(MenuKeys.OptionKey, MenuKeys.Default) ?? MenuKeys.Default) != null)
                    doc.Set("Menu", "ShortcutKey", "auto");
                plan.AddBytes("OptiScaler.ini", doc.ToBytes(), keepIfModified: true, builtOnCurrent: File.Exists(iniPath));
            }
            else
            {
                foreach (var (s, k, v) in settings) plan.IniSet("OptiScaler.ini", s, k, v);
                MenuKeys.Apply(plan, managed, "OptiScaler.ini", "Menu", "ShortcutKey", menuKey, MenuKeys.OptiScalerDefault, MenuKeys.Hex);
            }

            if (f5)
            {
                var modelOwner = ctx.State.OwnerOf("nvngx_dlssnr.dll");
                if (modelOwner == null || modelOwner == Id) plan.AddFile("nvngx_dlssnr.dll", NrRules.ModelFile(ctx, out _));
                plan.Note($"Nel menu OptiScaler ({MenuKeys.NameOf(menuCode ?? MenuKeys.OptiScalerDefault)}) configura Multipass e Optical F5Low. La conversione SDR→HDR non supporta DLSS-G.");
            }
            plan.Note($"Proxy: {proxy}. Riavvia il gioco e verifica OptiScaler.log.");
            return plan.Build();
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static bool Allowed(string rel, bool f5)
    {
        var name = Path.GetFileName(rel);
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        if (rel.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)) return false;
        bool privateFg = f5 && rel.Equals("OptiScaler/streamline/nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase);
        if (name.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase) && !privateFg) return false; // DLSS DLLs are never replaced blindly
        if (rel.StartsWith("Licenses/", StringComparison.OrdinalIgnoreCase) && ext is ".txt" or ".md") return true;
        if (!f5) return OfficialFiles.Contains(rel) || rel.Equals("fakenvapi.ini", StringComparison.OrdinalIgnoreCase);
        if (rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) || privateFg) return true;
        if (rel.StartsWith("OptiScaler/", StringComparison.OrdinalIgnoreCase))
            return ext is ".dll" or ".bin" or ".json" or ".ini" or ".hlsl" or ".cso" or ".cube" or ".txt" or ".md";
        return rel is "LICENSE" or "README.md" or "INSTALL-DLSSNR.md" or "TROUBLESHOOTING.md" or "docs/DLSS-FRAME-GENERATION.md";
    }
}
