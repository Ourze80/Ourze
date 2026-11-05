using Hdlss.Core.Games;
using Hdlss.Core.IO;

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
        NrPath.Native => "DLSS del gioco + RenoDX NR",
        NrPath.Feeder => "Feeder (per giochi senza DLSS)",
        _ => "OptiScaler F5",
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
        if (Installed(ctx) is { } current) return new(current, "È il percorso già installato su questo gioco.", dlss);
        if (!ctx.Gpu.SupportsNr) return new(null, "Serve una scheda NVIDIA RTX 4000 o 5000: su questo PC non è stata trovata.", dlss);
        return ctx.Api switch
        {
            GraphicsApi.DX12 when dlss => new(NrPath.Native, "Il gioco usa DirectX 12 e ha già DLSS: RenoDX NR lo sfrutta direttamente. È il percorso più stabile.", true),
            GraphicsApi.DX12 or GraphicsApi.DX11 or GraphicsApi.Vulkan when !dlss =>
                new(NrPath.Feeder, "Il gioco non ha DLSS: Feeder ricostruisce le informazioni che servono al Neural Rendering. È sperimentale, possono comparire scie leggere.", false),
            GraphicsApi.DX11 or GraphicsApi.Vulkan =>
                new(NrPath.Feeder, "Il gioco ha DLSS ma non usa DirectX 12: Feeder è il percorso che funziona con DirectX 11 e Vulkan.", true),
            _ => new(null, "Non riesco a capire quale grafica usa il gioco. Aprila in «Avanzate › Eseguibile e API» e scegli DirectX 11, 12 o Vulkan.", dlss),
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
