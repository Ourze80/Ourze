using System.Text.RegularExpressions;
using Hdlss.Core.IO;

namespace Hdlss.Core.Components;

/// <summary>What is physically present in a game folder, independent of who installed it.</summary>
public sealed class FolderScan
{
    public static readonly string[] ProxyNames = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];
    private static readonly string[] ReShadeNames = [.. ProxyNames, "ReShade64.dll", "d3d11.dll", "dinput8.dll", "opengl32.dll", Vulkan.ReShadeDll];

    public List<string> ReShadeLoaders { get; } = [];
    public bool ReShadeSupportsAddons { get; private set; }
    public string? OptiScalerProxy { get; private set; }
    public List<string> OptiScalerProxies { get; } = [];
    public List<string> Addons { get; } = [];
    public string? AntiCheat { get; private set; }
    public bool HasNrModel { get; private set; }
    public string ReShadeConfig { get; private set; } = "ReShade.ini";
    /// <summary>Add-on folder relative to the game folder: "" = the game folder, "?" = outside it.</summary>
    public string AddonFolder { get; private set; } = "";
    /// <summary>AddonPath as written in ReShade.ini (for messages).</summary>
    public string AddonPathValue { get; private set; } = "";

    /// <summary>
    /// ReShade's AddonPath relative to <paramref name="folder"/>. ReShade's own folder picker writes
    /// absolute paths: those are accepted when they point at the game folder or inside it.
    /// </summary>
    internal static string AddonFolderFor(string folder, string value)
    {
        if (value is "" or "." or ".\\" or "./") return "";
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(folder, value));
            if (string.Equals(full.TrimEnd('\\', '/'), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return "";
            var rel = PathGuard.RelativeTo(folder, full);
            PathGuard.Resolve(folder, rel);
            return rel;
        }
        catch (Exception ex) when (ex is UnsafePathException or ArgumentException or NotSupportedException or IOException) { return "?"; }
    }

    public static FolderScan Run(string folder, string gameRoot)
    {
        var s = new FolderScan();
        foreach (var name in ReShadeNames)
        {
            var path = PathGuard.Resolve(folder, name);
            if (File.Exists(path) && IsReShade(path)) s.ReShadeLoaders.Add(name);
        }
        s.ReShadeSupportsAddons = s.ReShadeLoaders.Count == 1
            && PeFile.ContainsAscii(PathGuard.Resolve(folder, s.ReShadeLoaders[0]), "LoadFromDllMain");
        foreach (var name in ProxyNames)
        {
            var path = PathGuard.Resolve(folder, name);
            if (File.Exists(path) && IsOptiScaler(path)) s.OptiScalerProxies.Add(name);
        }
        s.OptiScalerProxy = s.OptiScalerProxies.FirstOrDefault();

        // ReShade reads "<loader>.ini" when ReShade.ini is absent.
        if (s.ReShadeLoaders.Count == 1 && !s.ReShadeLoaders[0].Contains('/'))
        {
            var custom = Path.ChangeExtension(s.ReShadeLoaders[0], ".ini");
            if (!File.Exists(PathGuard.Resolve(folder, "ReShade.ini")) && File.Exists(PathGuard.Resolve(folder, custom))) s.ReShadeConfig = custom;
        }
        var addonPath = IniDocument.Load(PathGuard.Resolve(folder, s.ReShadeConfig)).Get("ADDON", "AddonPath");
        s.AddonPathValue = addonPath?.Trim().Trim('"') ?? "";
        s.AddonFolder = AddonFolderFor(folder, s.AddonPathValue);
        foreach (var dir in new[] { folder, s.AddonFolder is "" or "?" ? folder : PathGuard.Resolve(folder, s.AddonFolder) }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*.addon64"))
                    s.Addons.Add(PathGuard.RelativeTo(folder, file));

        s.HasNrModel = File.Exists(PathGuard.Resolve(folder, "nvngx_dlssnr.dll"));
        if (Directory.Exists(Path.Combine(gameRoot, "EasyAntiCheat")) || Directory.Exists(Path.Combine(gameRoot, "BattlEye"))
            || Directory.EnumerateFiles(folder, "*EasyAntiCheat*").Any())
            s.AntiCheat = "Anti-cheat rilevato (EasyAntiCheat/BattlEye): le DLL aggiunte al gioco possono far scattare un ban. HDLSS5 non installa né modifica componenti in questo gioco.";
        return s;
    }

    /// <summary>Neural Rendering add-ons not counting the given (managed) relative paths.</summary>
    public IEnumerable<string> NrAddons() =>
        Addons.Where(a => Regex.IsMatch(Path.GetFileName(a), "renodx-dlss|dlss5|dlssnr|neural|shortfuse", RegexOptions.IgnoreCase));

    public static bool IsReShade(string path) =>
        PeFile.IsPe64(path) && PeFile.Identity(path).Contains("ReShade", StringComparison.OrdinalIgnoreCase);

    public static bool IsOptiScaler(string path)
    {
        if (!PeFile.IsPe64(path)) return false;
        var id = PeFile.Identity(path);
        if (Regex.IsMatch(id, "ReShade|SCSKiller|Ultimate.ASI.Loader", RegexOptions.IgnoreCase)) return false;
        if (id.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)) return true;
        return new FileInfo(path).Length < 80_000_000 && PeFile.ContainsAscii(path, "OptiScaler.ini");
    }
}
