using Hdlss.Core.Games;
using Hdlss.Core.Text;

namespace Hdlss.Core.Components;

public enum GameBadge { Checking, Ready, NrActive, Legacy, Foreign, AntiCheat, Protected, NoExe, Warning }

/// <summary>One-line status for the library card. No network; computed in the background.</summary>
public sealed record GameSummary(GameBadge Badge, string Text, string? Tip = null)
{
    public static GameSummary Checking { get; } = new(GameBadge.Checking, "…");

    public static GameSummary Compute(Game game, Services services)
    {
        if (game.ProtectedReason != null) return new(GameBadge.Protected, Tx.Get("sum.protected"));
        if (!game.HasExe) return new(GameBadge.NoExe, Tx.Get("sum.noExe"));
        var folder = game.Folder;
        if (Directory.Exists(Path.Combine(game.Root, "EasyAntiCheat")) || Directory.Exists(Path.Combine(game.Root, "BattlEye")))
            return new(GameBadge.AntiCheat, Tx.Get("sum.antiCheat"));
        var state = services.States.Load(game.Id, folder);
        var nr = state.Get(RenoDxNrComponent.ComponentId) != null ? NrPath.Native
               : state.Get(FeederComponent.ComponentId) != null ? NrPath.Feeder
               : NrRules.DoesNr(state.Get(OptiScalerComponent.ComponentId), OptiScalerComponent.ComponentId) ? NrPath.OptiScaler : (NrPath?)null;
        // Only 1.x installs the 2.0 can take over count (DLL swaps and Streamline stay in "Avanzate").
        var legacyInstalls = Legacy.LegacyInstalls.Find(folder, services.Log);
        bool legacy = legacyInstalls.Any(l => l.AdoptableAs != null && !state.Components.ContainsKey(l.AdoptableAs));
        int foreign = 0;
        bool conflict = false;
        try
        {
            var ctx = new GameContext(game, services, Hardware.GpuInfo.Unknown, background: true);
            var skip = legacyInstalls.Where(l => l.AdoptableAs != null).Select(l => l.AdoptableAs!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreign = ForeignInstalls.Detect(ctx, skip).Count;
            conflict = NrConflicts.Check(ctx).Count > 0;
        }
        catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException)
        {
            services.Log.Debug($"Stato di {game.Name}: {ex.Message}");
        }
        if (conflict) return new(GameBadge.Warning, Tx.Get("sum.conflict"), Tx.Get("sum.conflict.tip"));
        if (nr != null) return new(GameBadge.NrActive, legacy ? Tx.Get("sum.nrLegacy") : foreign > 0 ? Tx.Get("sum.nrForeign") : Tx.Get("sum.nrActive"));
        if (legacy) return new(GameBadge.Legacy, Tx.Get("sum.legacy"));
        if (foreign > 0) return new(GameBadge.Foreign, foreign == 1 ? Tx.Get("sum.foreign1") : Tx.Get("sum.foreignN", foreign));
        return new(GameBadge.Ready, state.Components.Count > 0 ? Tx.Get("sum.components", state.Components.Count) : Tx.Get("sum.ready"));
    }
}
