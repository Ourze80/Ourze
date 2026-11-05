using Hdlss.Core.Games;

namespace Hdlss.Core.Components;

public enum GameBadge { Checking, Ready, NrActive, Legacy, Foreign, AntiCheat, Protected, NoExe }

/// <summary>One-line status for the library card. No network; computed in the background.</summary>
public sealed record GameSummary(GameBadge Badge, string Text)
{
    public static GameSummary Checking { get; } = new(GameBadge.Checking, "…");

    public static GameSummary Compute(Game game, Services services)
    {
        if (game.ProtectedReason != null) return new(GameBadge.Protected, "Cartella protetta · solo avvio");
        if (!game.HasExe) return new(GameBadge.NoExe, "Scegli l'eseguibile");
        var folder = game.Folder;
        if (Directory.Exists(Path.Combine(game.Root, "EasyAntiCheat")) || Directory.Exists(Path.Combine(game.Root, "BattlEye")))
            return new(GameBadge.AntiCheat, "Anti-cheat · mod sconsigliate");
        var state = services.States.Load(game.Id, folder);
        var nr = state.Get(RenoDxNrComponent.ComponentId) != null ? NrPath.Native
               : state.Get(FeederComponent.ComponentId) != null ? NrPath.Feeder
               : NrRules.DoesNr(state.Get(OptiScalerComponent.ComponentId), OptiScalerComponent.ComponentId) ? NrPath.OptiScaler : (NrPath?)null;
        // Only 1.x installs the 2.0 can take over count (DLL swaps and Streamline stay in "Avanzate").
        var legacyInstalls = Legacy.LegacyInstalls.Find(folder, services.Log);
        bool legacy = legacyInstalls.Any(l => l.AdoptableAs != null && !state.Components.ContainsKey(l.AdoptableAs));
        int foreign = 0;
        try
        {
            var ctx = new GameContext(game, services, Hardware.GpuInfo.Unknown);
            var skip = legacyInstalls.Where(l => l.AdoptableAs != null).Select(l => l.AdoptableAs!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreign = ForeignInstalls.Detect(ctx, skip).Count;
        }
        catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException)
        {
            services.Log.Debug($"Stato di {game.Name}: {ex.Message}");
        }
        if (nr != null) return new(GameBadge.NrActive, legacy ? "NR attivo · resti 1.x" : foreign > 0 ? "NR attivo · mod da importare" : "Neural Rendering attivo");
        if (legacy) return new(GameBadge.Legacy, "Da portare sulla 2.0");
        if (foreign > 0) return new(GameBadge.Foreign, foreign == 1 ? "1 mod da importare" : $"{foreign} mod da importare");
        return new(GameBadge.Ready, state.Components.Count > 0 ? $"{state.Components.Count} componenti · NR non attivo" : "Pronto da configurare");
    }
}
