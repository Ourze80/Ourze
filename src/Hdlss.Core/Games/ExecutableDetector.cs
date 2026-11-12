using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hdlss.Core.IO;

namespace Hdlss.Core.Games;

/// <summary>Finds the real game executable, its engine and its graphics API.</summary>
public static partial class ExecutableDetector
{
    [GeneratedRegex(@"crash|unins|redist|setup|report|helper|prereq|launcher|anticheat|eac|cefprocess|webhelper|checkenv|^[78]za?(_|\.|-)|^7z|dxsetup|bugsplat|sentry", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceExe();

    [GeneratedRegex("^(_CommonRedist|CommonRedist|Engine|Support|Installer|EasyAntiCheat|BattlEye|backup|_storage_|DirectX|vcredist)$", RegexOptions.IgnoreCase)]
    private static partial Regex SkippedDir();

    public static bool IsServiceExecutable(string path) => ServiceExe().IsMatch(Path.GetFileName(path));

    /// <summary>x64 executables under the game root, breadth-first, at most 6 levels deep.</summary>
    public static List<string> Candidates(string root, int maxDepth = 6, int limit = 400, Func<string, bool>? accept = null)
    {
        var result = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && result.Count < limit)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var file in SafeFiles(dir, "*.exe"))
                if (!IsServiceExecutable(file) && (accept ?? PeFile.IsPe64)(file)) result.Add(file);
            if (depth >= maxDepth) continue;
            foreach (var sub in SafeDirs(dir))
            {
                if (SkippedDir().IsMatch(Path.GetFileName(sub))) continue;
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (IOException) { continue; }
                queue.Enqueue((sub, depth + 1));
            }
        }
        return result;
    }

    /// <summary>
    /// Picks the most likely game executable: a single Unreal "Binaries/Win64" shipping exe,
    /// else the only candidate, else the largest x64 exe (flagged as a guess).
    /// </summary>
    public static (string Exe, bool Certain) Pick(string root, Func<string, bool>? accept = null)
    {
        var all = Candidates(root, accept: accept);
        var shipping = all.Where(f => f.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase)
                                   || f.Contains(@"\Binaries\WinGDK\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (shipping.Count == 1) return (shipping[0], true);
        var named = shipping.Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1) return (named[0], true);
        if (all.Count == 1) return (all[0], true);
        var bySize = all.Select(f => (File: f, Size: Size(f))).OrderByDescending(x => x.Size).ToList();
        if (bySize.Count == 0) return ("", false);
        // One binary dwarfs the rest (a 500 MB game next to a 1 MB launcher stub): not a guess.
        var dominant = bySize[0].Size >= 20L * 1024 * 1024 && bySize[0].Size >= 10 * bySize[1].Size;
        return (bySize[0].File, dominant);
    }

    private static bool InUnrealBinaries(string path) =>
        Regex.IsMatch(path.Replace('/', '\\').TrimEnd('\\') + "\\", @"\\Binaries\\(Win64|WinGDK)\\$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The single Unreal project folder of an install (a top-level folder with Content\Paks,
    /// not "Engine"), or null when there is none or more than one.
    /// </summary>
    public static string? UnrealProject(string root)
    {
        var found = SafeDirs(root)
            .Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(Path.Combine(d, "Content", "Paks")))
            .Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>&lt;Project&gt;\Binaries\Win64 (or WinGDK) of the single Unreal project, if it exists.</summary>
    public static string? UnrealBinaries(string root) =>
        UnrealProject(root) is { } project
            ? new[] { "Win64", "WinGDK" }.Select(p => Path.Combine(project, "Binaries", p)).FirstOrDefault(Directory.Exists)
            : null;

    /// <summary>
    /// Unreal games started from a launcher stub outside "Binaries" (e.g. platform\steam\Game.exe)
    /// load proxy DLLs from &lt;Project&gt;\Binaries\Win64, where the real game exe is. Returns that
    /// folder only when it holds a game exe larger than <paramref name="exe"/>. When the exe outside
    /// is itself the game (Phantom Blade Zero: a 500 MB binary, Binaries only has tools) it is null:
    /// the DLLs go next to the exe.
    /// </summary>
    public static string? ModFolderFor(string root, string exe)
    {
        if (string.IsNullOrEmpty(exe) || InUnrealBinaries(Path.GetDirectoryName(exe)!)) return null;
        if (UnrealBinaries(root) is not { } bin) return null;
        var size = Size(exe);
        return SafeFiles(bin, "*.exe").Any(f => !IsServiceExecutable(f) && Size(f) > size) ? bin : null;
    }

    private static long Size(string file) { try { return new FileInfo(file).Length; } catch (IOException) { return 0; } }

    /// <summary>One line for the executable picker: size and what the file says it is.</summary>
    public static string Describe(string exe)
    {
        var parts = new List<string>();
        try { parts.Add($"{new FileInfo(exe).Length / (1024.0 * 1024):0.#} MB"); } catch (IOException) { }
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            var what = string.IsNullOrWhiteSpace(v.FileDescription) ? v.ProductName : v.FileDescription;
            if (!string.IsNullOrWhiteSpace(what)) parts.Add(what.Trim());
        }
        catch (FileNotFoundException) { }
        return string.Join(" · ", parts);
    }

    public static EngineKind DetectEngine(string exe) => DetectEngine(exe, exe.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase));

    /// <param name="unrealLayout">The game uses an Unreal Binaries\Win64 folder (its own, or as mod folder).</param>
    public static EngineKind DetectEngine(string exe, bool unrealLayout)
    {
        if (!File.Exists(exe)) return EngineKind.Unknown;
        string version = "";
        try { var v = FileVersionInfo.GetVersionInfo(exe); version = $"{v.FileVersion} {v.ProductVersion}"; }
        catch (FileNotFoundException) { }
        if (Regex.IsMatch(version, @"UE5[-+]|\+\+UE5", RegexOptions.IgnoreCase)) return EngineKind.Unreal5;
        if (Regex.IsMatch(version, @"UE4[-+]|\+\+UE4", RegexOptions.IgnoreCase)) return EngineKind.Unreal4;
        if (!unrealLayout) return EngineKind.Unknown;
        return ScanMarker(exe) switch { "UE5" => EngineKind.Unreal5, "UE4" => EngineKind.Unreal4, _ => EngineKind.UnrealUnverified };
    }

    public static GraphicsApi DetectApi(string exe) => DetectApi(exe, out _);

    /// <summary>
    /// Graphics API from the import tables (regular + delay-load). When the executable imports
    /// several or none, falls back to folder evidence (DirectX 12 Agility SDK, Vulkan-only
    /// runtimes) and reports <paramref name="certain"/> = false.
    /// </summary>
    public static GraphicsApi DetectApi(string exe, out bool certain, string? modFolder = null)
    {
        certain = false;
        if (!File.Exists(exe)) return GraphicsApi.Unknown;
        // The Agility SDK sits next to the binaries that load it: the mod folder when the game
        // starts from a launcher stub (Unreal: <Project>/Binaries/Win64).
        string[] folders = modFolder != null ? [Path.GetDirectoryName(exe)!, modFolder] : [Path.GetDirectoryName(exe)!];
        bool agility = folders.Any(f => File.Exists(Path.Combine(f, "D3D12Core.dll")) || File.Exists(Path.Combine(f, "D3D12", "D3D12Core.dll")));
        var api = ClassifyApi(PeFile.Read(exe).Imports, agility, out certain);
        return api == GraphicsApi.Unknown && folders.Any(VulkanOnlyRuntime) ? GraphicsApi.Vulkan : api;
    }

    /// <summary>
    /// The folder ships a Vulkan renderer DLL (hookshot_vk.dll, renderer_vulkan.dll) and nothing for
    /// DirectX 12: a game that imports no graphics API and loads its renderer at run time.
    /// </summary>
    internal static bool VulkanOnlyRuntime(string folder)
    {
        var names = SafeFiles(folder, "*.dll").Select(f => Path.GetFileNameWithoutExtension(f)!).ToList();
        static bool Has(string n, params string[] marks) => marks.Any(m => n.EndsWith(m, StringComparison.OrdinalIgnoreCase));
        return names.Any(n => Has(n, "_vk", "_vulkan", "-vk", "-vulkan"))
            && !names.Any(n => Has(n, "_dx12", "_d3d12", "-dx12", "-d3d12", "_dx11", "_d3d11", "-dx11", "-d3d11") || n.Equals("D3D12Core", StringComparison.OrdinalIgnoreCase));
    }

    internal static GraphicsApi ClassifyApi(IReadOnlyCollection<string> imports, bool agility, out bool certain)
    {
        certain = false;
        bool dx12 = imports.Contains("d3d12.dll"), dx11 = imports.Contains("d3d11.dll"), vk = imports.Contains("vulkan-1.dll");
        // Libraries that exist only for DirectX 12 (e.g. amd_fidelityfx_loader_dx12.dll): engines
        // that load d3d12.dll at run time (RE Engine: Dragon's Dogma 2, Monster Hunter Wilds)
        // import only d3d11.dll, for video decoding.
        bool dx12Only = imports.Any(i => i.EndsWith("_dx12.dll", StringComparison.OrdinalIgnoreCase));
        switch ((dx12 || dx12Only, dx11, vk))
        {
            case (true, false, false): certain = true; return GraphicsApi.DX12;
            case (true, true, false) when dx12Only: certain = true; return GraphicsApi.DX12;
            case (false, false, true): certain = true; return GraphicsApi.Vulkan;
        }
        if (dx11 && !dx12 && !vk)
        {
            if (agility) return GraphicsApi.DX12;           // ships the Agility SDK: loads d3d12 at run time
            certain = true; return GraphicsApi.DX11;
        }
        if (dx12 && !vk) return GraphicsApi.DX12;            // DX11 is often imported only for tooling/video
        if (agility && !vk) return GraphicsApi.DX12;
        if (vk && !dx12 && !dx11) return GraphicsApi.Vulkan;
        return GraphicsApi.Unknown;
    }

    private static string ScanMarker(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var buffer = new byte[(1 << 20) + 128];
            int remain = 0; long total = 0;
            while (total < 512L << 20)
            {
                int n = s.Read(buffer, remain, 1 << 20);
                if (n == 0) break;
                int length = remain + n;
                var ascii = Encoding.ASCII.GetString(buffer, 0, length);
                var utf16 = Encoding.Unicode.GetString(buffer, 0, length - length % 2);
                if (ascii.Contains("++UE5+") || utf16.Contains("++UE5+")) return "UE5";
                if (ascii.Contains("++UE4+") || utf16.Contains("++UE4+")) return "UE4";
                remain = Math.Min(128, length);
                Buffer.BlockCopy(buffer, length - remain, buffer, 0, remain);
                total += n;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }

    internal static string[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static string[] SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
