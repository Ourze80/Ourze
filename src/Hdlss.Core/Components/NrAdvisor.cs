using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Text;

namespace Hdlss.Core.Components;

public enum NrPath { Native, Feeder, OptiScaler }

public sealed record NrRecommendation(NrPath? Path, string Reason, bool HasNativeDlss);

/// <summary>Chooses the Neural Rendering path for a game and explains why in plain words.</summary>
public static class NrAdvisor
{
    public static readonly IReadOnlyDictionary<NrPath, string[]> Components = new Dictionary<NrPath, string[]>
    {
        [NrPath.Native] = [ReShadeComponent.ComponentId, RenoDxNrComponent.ComponentId],
        [NrPath.Feeder] = [ReShadeComponent.ComponentId, FeederComponent.ComponentId],
        [NrPath.OptiScaler] = [OptiScalerComponent.ComponentId],
    };

    public static string Title(NrPath path) => path switch
    {
        NrPath.Native => Tx.Get("nr.title.native"),
        NrPath.Feeder => Tx.Get("nr.title.feeder"),
        _ => Tx.Get("nr.title.opti"),
    };

    public static NrPath? Installed(GameContext ctx) =>
        ctx.Installed(RenoDxNrComponent.ComponentId) ? NrPath.Native
        : ctx.Installed(FeederComponent.ComponentId) ? NrPath.Feeder
        : NrRules.DoesNr(ctx, OptiScalerComponent.ComponentId) ? NrPath.OptiScaler
        : null;

    /// <summary>True when the game ships NVIDIA DLSS Super Resolution somewhere in its install.</summary>
    public static bool HasNativeDlss(GameContext ctx)
    {
        var feeder = ctx.State.Get(FeederComponent.ComponentId);
        foreach (var file in DlssFiles.Find(ctx.Game.Root))
        {
            if (!Path.GetFileName(file).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)) continue;
            // The Feeder installs its own copy: that is not the game's DLSS.
            if (feeder != null && string.Equals(Path.GetDirectoryName(file), ctx.Folder, StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return File.Exists(Path.Combine(ctx.Folder, "sl.interposer.dll"));
    }

    public static NrRecommendation Recommend(GameContext ctx)
    {
        bool dlss = HasNativeDlss(ctx);
        if (Installed(ctx) is { } current) return new(current, Tx.Get("nr.reason.installed"), dlss);
        if (!ctx.Gpu.SupportsNr) return new(null, Tx.Get("nr.reason.noGpu"), dlss);
        return ctx.Api switch
        {
            GraphicsApi.DX12 when dlss => new(NrPath.Native, Tx.Get("nr.reason.native"), true),
            GraphicsApi.DX12 or GraphicsApi.DX11 or GraphicsApi.Vulkan when !dlss =>
                new(NrPath.Feeder, Tx.Get("nr.reason.feederNoDlss"), false),
            GraphicsApi.DX11 or GraphicsApi.Vulkan =>
                new(NrPath.Feeder, Tx.Get("nr.reason.feederDx11"), true),
            _ => new(null, Tx.Get("nr.reason.unknownApi"), dlss),
        };
    }
}

public static class DlssFiles
{
    public static readonly string[] Names = ["nvngx_dlss.dll", "nvngx_dlssg.dll", "nvngx_dlssd.dll"];

    // Folders that never hold the game's DLSS runtime but can be huge (Unreal content, shaders, saves).
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler", "_CommonRedist", "Paks", "Movies", "Localization", "Saved", "Shaders", "Source", "Intermediate",
        "reshade-shaders", "Mods", "ue4ss", "Logs", "Splash", "Config",
    };

    /// <summary>DLSS DLLs under <paramref name="root"/>, skipping backup and mod-manager folders.</summary>
    public static List<string> Find(string root, int maxDepth = 9)
    {
        var found = new List<string>();
        var queue = new Queue<(string, int)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            foreach (var name in Names)
            {
                var file = Path.Combine(dir, name);
                if (File.Exists(file)) found.Add(file);
            }
            if (depth >= maxDepth) continue;
            foreach (var sub in ExecutableDetector.SafeDirs(dir))
            {
                var n = Path.GetFileName(sub);
                if (n.StartsWith('.') || n.Contains("backup", StringComparison.OrdinalIgnoreCase) || Skipped.Contains(n)
                    || n.Equals("Content", StringComparison.OrdinalIgnoreCase) && depth > 0) continue;
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (IOException) { continue; }
                queue.Enqueue((sub, depth + 1));
            }
        }
        return found;
    }

    public static Version? VersionOf(string file) => PeFile.FileVersion(file);
}
