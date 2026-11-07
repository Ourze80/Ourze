using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// Neural Rendering for games WITHOUT native DLSS (DX11/DX12/Vulkan x64): DLSS5-Feeder
/// supplies depth/motion estimated by LumeniteFX to RenoDX NR.
/// </summary>
public sealed partial class FeederComponent : ComponentBase
{
    public const string ComponentId = "nr-feeder";
    public const string Repo = "jlrouzies-fr/DLSS5-Feeder";
    public const string Prefix = "reshade-shaders/HDLSS-Feeder";
    public const string Preset = "HDLSS-Feeder.ini";
    public const string FeedAddon = "dlss5-feed.addon64";

    private const string LumeniteUrl = "https://github.com/umar-afzaal/LumeniteFX/archive/25f40a2ac70bc90bd3dc53bb2fc9d5fa50536b8b.zip";
    private const string LumeniteSha = "06d40e49b54b7e2fcae61b1f489b25a0c1779796160955760a3739b28cd103cc";
    private const string HeadersBase = "https://raw.githubusercontent.com/crosire/reshade-shaders/fd0022170615ce0d8162d219bff07232fa6dd84f/Shaders/";
    private static readonly (string Name, string Sha)[] Headers =
    [
        ("ReShade.fxh", "6dabfbbaf968c3871905d2ea17f96572ff7b1cec01310b5d0e5252b66b30174f"),
        ("ReShadeUI.fxh", "78adf672df47460297eb9fe6dd238d2aafa24510b52b84feb1a745dff70eb901"),
    ];
    private const string DlssManifest = "https://raw.githubusercontent.com/beeradmoore/dlss-swapper-manifest-builder/main/manifest.json";

    public override string Id => ComponentId;
    public override string Name => "Feeder (senza DLSS) · sperimentale";
    public override string Description => "NR per giochi senza DLSS nativo, x64 DX11/DX12/Vulkan. Installa Feeder, LumeniteFX, RenoDX NR Lecram 10.8, DLSS SR e il modello NR. I vettori di movimento stimati possono causare scie o toccare l'HUD.";
    public override IReadOnlyList<string> Requires => [ReShadeComponent.ComponentId];

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")] private static partial Regex StableTag();

    protected override ComponentStatus? DetectExternal(GameContext ctx) =>
        ctx.Scan.Addons.Any(a => Path.GetFileName(a).Equals(FeedAddon, StringComparison.OrdinalIgnoreCase))
            ? new ComponentStatus(ComponentStatusKind.External, $"{FeedAddon} presente · installato da altro software") : null;

    protected override void CheckBlockers(GameContext ctx, List<string> blockers)
    {
        NrRules.CheckGpu(ctx, blockers);
        NrRules.CheckSingleInjector(ctx, Id, blockers);
        if (ctx.Api == GraphicsApi.Unknown) blockers.Add("API grafica non riconosciuta: Feeder funziona solo con giochi x64 DX11, DX12 o Vulkan e HDLSS5 non ha capito quale usa questo gioco. Impostala in «Eseguibile e API».");
        if (ctx.Installed(ReShadeComponent.ComponentId) && ctx.Api != GraphicsApi.Vulkan && !ctx.Scan.ReShadeSupportsAddons)
            blockers.Add("ReShade senza caricamento anticipato degli add-on: la copia installata non lo supporta e Feeder ne ha bisogno. Reinstalla ReShade dalla sua scheda.");
        if (ctx.Scan.OptiScalerProxy != null) blockers.Add("OptiScaler presente: Feeder usa RenoDX NR e non può convivere con OptiScaler. Rimuovi OptiScaler dalla sua scheda.");
        if (ctx.Scan.AddonFolder.Length > 0) blockers.Add("AddonPath di ReShade personalizzato: Feeder trova gli add-on solo nella cartella del gioco. Riporta AddonPath alla cartella del gioco nel file di configurazione di ReShade.");
        foreach (var name in new[] { "nvngx_dlss.dll", "nvngx_dlssnr.dll" })
        {
            var owner = ctx.State.OwnerOf(name);
            if (owner != null && owner != Id && !(name == "nvngx_dlssnr.dll" && NrRules.CanTakeOverModel(owner))) blockers.Add($"{name} è gestito da «{ComponentCatalog.NameOf(owner)}»: una DLL può avere un solo gestore. Rimuovi «{ComponentCatalog.NameOf(owner)}» dalla sua scheda, poi installa Feeder.");
        }
    }

    public override async Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var tag = await Releases.LatestTagAsync(ctx, Repo, cancel, StableTag().IsMatch);
        return tag == null ? null : $"{tag} · {NrModels.ForGpu(ctx.Gpu).Label}";
    }

    public override async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        var http = ctx.Services.Http;
        var vulkan = ctx.Api == GraphicsApi.Vulkan;
        progress?.Report(new("Cerco l'ultima release stabile di Feeder…"));
        using var json = await http.GetJsonAsync(GitHub.LatestUrl(Repo), cancel, TimeSpan.FromMinutes(30));
        var release = GitHubRelease.Parse(json.RootElement);
        if (!StableTag().IsMatch(release.Tag) || release.Draft || release.Prerelease) throw new HdlssException("Nessuna release stabile di Feeder.");
        var assetName = $"DLSS5-Feeder-{release.Tag[1..]}.zip";
        var asset = release.Assets.SingleOrDefault(a => a.Name == assetName);
        if (asset == null || asset.Sha256 == null || !GitHubRelease.FromRepo(asset, Repo, release.Tag) || asset.Size > 16_000_000)
            throw new HdlssException("Pacchetto Feeder non verificabile (origine, SHA-256 o dimensione).");

        var temp = Path.Combine(ctx.Services.Paths.Temp, Guid.NewGuid().ToString("N"));
        try
        {
            var feederZip = await http.DownloadAsync(asset.Url, asset.Name, asset.Sha256, 16_000_000, progress, cancel);
            var wanted = new[] { FeedAddon, "reshade-shaders/Shaders/DLSS5_Feed.fx" }.Concat(vulkan ? new[] { "layer-x64/VkLayer_feed_vk.dll" } : Array.Empty<string>()).ToList();
            var got = Archives.Extract(feederZip, Path.Combine(temp, "feeder"), n => wanted.Contains(n), 16_000_000, 64_000_000);
            if (wanted.Any(w => got.Count(g => g == w) != 1)) throw new HdlssException("Pacchetto Feeder incompleto.");
            if (!PeFile.IsPe64(Path.Combine(temp, "feeder", FeedAddon))) throw new HdlssException("Feeder non x64.");

            progress?.Report(new("Scarico LumeniteFX e header ReShade (versioni fissate)…"));
            var lumeniteZip = await http.DownloadAsync(LumeniteUrl, "lumenite.zip", LumeniteSha, 64_000_000, progress, cancel);
            var lumenite = Path.Combine(temp, "lumenite");
            var shaderFiles = Archives.Extract(lumeniteZip, lumenite, n =>
            {
                var cut = n.IndexOf('/');
                if (cut < 0) return false;
                var rel = n[(cut + 1)..];
                if (!rel.StartsWith("Shaders/", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)) return false;
                return !rel.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) || rel.EndsWith("lumenite_Kernel.fx", StringComparison.OrdinalIgnoreCase);
            });
            var headers = new List<(string Name, string File)>();
            foreach (var (name, sha) in Headers)
                headers.Add((name, await http.DownloadAsync(HeadersBase + name, name, sha, 1_000_000, progress, cancel)));

            var dlss = await DownloadDlssSr(ctx, temp, progress, cancel);
            var model = NrRules.ModelFile(ctx, out var nr);
            var renoNr = RenoDxNrComponent.Bundled(ctx, "renodx-dlss5-lecram-10.8.addon64", RenoDxNrComponent.LecramHash);

            var plan = ctx.NewPlan(this, $"{release.Tag} · {nr.Label}");
            plan.Meta("source", asset.Url);
            plan.AddFile(FeedAddon, Path.Combine(temp, "feeder", FeedAddon));
            plan.AddFile(RenoDxNrComponent.AddonName, renoNr);
            plan.AddFile("nvngx_dlss.dll", dlss);
            plan.AddFile("nvngx_dlssnr.dll", model);
            if (vulkan)
            {
                plan.AddFile(Vulkan.FeedDll, Path.Combine(temp, "feeder", "layer-x64", "VkLayer_feed_vk.dll"));
                plan.AddBytes(Vulkan.FeedJson, Vulkan.LayerManifest(Vulkan.FeedLayer, "VkLayer_feed_vk.dll", reshade: false));
            }
            foreach (var rel in shaderFiles)
                plan.AddFile($"{Prefix}/{rel[(rel.IndexOf('/') + 1)..]}", PathGuard.Resolve(lumenite, rel));
            foreach (var (name, file) in headers) plan.AddFile($"{Prefix}/Shaders/{name}", file);
            plan.AddFile($"{Prefix}/Shaders/DLSS5_Feed.fx", Path.Combine(temp, "feeder", "reshade-shaders", "Shaders", "DLSS5_Feed.fx"));
            if (!shaderFiles.Any(f => f.EndsWith("/lumenite_Kernel.fx", StringComparison.OrdinalIgnoreCase)))
                throw new HdlssException("LumeniteFX incompleto: manca lumenite_Kernel.fx.");

            var config = ctx.Scan.ReShadeConfig;
            plan.AddBytes(Preset, BuildPreset(ctx, plan, config), keepIfModified: true);
            plan.IniListAdd(config, "GENERAL", "EffectSearchPaths", $".\\{Prefix.Replace('/', '\\')}\\Shaders\\**");
            plan.IniListAdd(config, "GENERAL", "TextureSearchPaths", $".\\{Prefix.Replace('/', '\\')}\\Textures\\**");
            plan.IniSet(config, "GENERAL", "PresetPath", ".\\" + Preset);
            plan.IniListAdd(config, "ADDON", "LoadFromDllMain", RenoDxNrComponent.AddonName);
            plan.IniListAdd(config, "ADDON", "LoadFromDllMain", FeedAddon);
            plan.Note("In gioco: verifica Generic Depth, poi LUMENITE: Kernel sopra DLSS 5 Feed. Disattiva MSAA/SSAA" + (vulkan ? " e NVIDIA Smooth Motion; avvia da HDLSS5." : "."));
            plan.Note("Controlla dlss5-feed.log: «feature ready» e «frame delivered» indicano NR attivo.");
            return plan.Build();
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>Preset = the user's current preset (if any) + Lumenite Kernel and DLSS5 Feed techniques.</summary>
    private static byte[] BuildPreset(GameContext ctx, PlanBuilder plan, string config)
    {
        var existingPreset = PathGuard.Resolve(ctx.Folder, Preset);
        IniDocument doc;
        if (File.Exists(existingPreset)) doc = IniDocument.Load(existingPreset);
        else
        {
            var current = plan.Ini(config).Get("GENERAL", "PresetPath")?.Trim();
            doc = IniDocument.Parse("");
            if (!string.IsNullOrEmpty(current) && !Path.IsPathRooted(current))
            {
                var rel = current.StartsWith(".\\") || current.StartsWith("./") ? current[2..] : current;
                try
                {
                    var full = PathGuard.Resolve(ctx.Folder, rel);
                    if (File.Exists(full)) doc = IniDocument.Load(full);
                }
                catch (UnsafePathException) { }
            }
        }
        string[] techniques = ["Lumenite_Kernel@lumenite_Kernel.fx", "DLSS5_Feed@DLSS5_Feed.fx"];
        foreach (var key in new[] { "Techniques", "TechniqueSorting" })
        {
            var list = IniDocument.SplitList(doc.Get("", key)).Where(t => !techniques.Contains(t, StringComparer.OrdinalIgnoreCase)).Concat(techniques);
            doc.Set("", key, string.Join(",", list));
        }
        var defs = IniDocument.SplitList(doc.Get("DLSS5_Feed.fx", "PreprocessorDefinitions"))
            .Where(d => !d.StartsWith("DLSS5_MV_PROVIDER=", StringComparison.OrdinalIgnoreCase)).Append("DLSS5_MV_PROVIDER=3");
        doc.Set("DLSS5_Feed.fx", "PreprocessorDefinitions", string.Join(",", defs));
        return doc.ToBytes();
    }

    /// <summary>Latest signed release DLSS SR (nvngx_dlss.dll) from the DLSS Swapper manifest, MD5-verified.</summary>
    private static async Task<string> DownloadDlssSr(GameContext ctx, string temp, IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        progress?.Report(new("Cerco DLSS Super Resolution nel catalogo DLSS Swapper…"));
        using var manifest = await ctx.Services.Http.GetJsonAsync(DlssManifest, cancel, TimeSpan.FromHours(12));
        var entry = manifest.RootElement.GetProperty("dlss").EnumerateArray()
            .Where(e => !e.GetProperty("is_dev_file").GetBoolean() && e.GetProperty("is_signature_valid").GetBoolean())
            .Select(e => (Version: Version.TryParse(e.GetProperty("version").GetString(), out var v) ? v : null, E: e))
            .Where(x => x.Version != null).OrderByDescending(x => x.Version).Select(x => x.E).FirstOrDefault();
        if (entry.ValueKind == JsonValueKind.Undefined) throw new HdlssException("Catalogo DLSS vuoto.");
        var url = entry.GetProperty("download_url").GetString()!;
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "dlss-swapper-downloads.beeradmoore.com" || !uri.AbsolutePath.StartsWith("/dlss/"))
            throw new HdlssException("Origine DLSS non valida nel catalogo.");
        var zipMd5 = entry.GetProperty("zip_md5_hash").GetString()!;
        var dllMd5 = entry.GetProperty("md5_hash").GetString()!;
        var zip = await ctx.Services.Http.DownloadAsync(url, Path.GetFileName(uri.AbsolutePath), null, 200_000_000, progress, cancel);
        if (!Md5(zip).Equals(zipMd5, StringComparison.OrdinalIgnoreCase)) throw new HdlssException("Archivio DLSS diverso dal catalogo.");
        var dir = Path.Combine(temp, "dlss");
        Archives.Extract(zip, dir, n => n.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase), 250_000_000);
        var dll = Path.Combine(dir, "nvngx_dlss.dll");
        if (!File.Exists(dll) || !Md5(dll).Equals(dllMd5, StringComparison.OrdinalIgnoreCase) || !PeFile.IsPe64(dll))
            throw new HdlssException("nvngx_dlss.dll diverso dal catalogo.");
        return dll;
    }

    private static string Md5(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(MD5.HashData(s));
    }
}
