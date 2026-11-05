using Hdlss.Core.Diagnostics;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>Plan → preview → apply workflow shared by every component.</summary>
public sealed class ComponentService(Services services)
{
    public static void CheckGlobal(GameContext ctx)
    {
        if (ctx.Game.ProtectedReason != null) throw new BlockedException(ctx.Game.ProtectedReason);
        if (!IO.PeFile.IsPe64(ctx.Game.Exe) && !Games.XboxLibrary.IsDeclaredX64(ctx.Game, ctx.Game.Exe)) throw new BlockedException("L'eseguibile selezionato non è x64.");
        if (ctx.Scan.AntiCheat != null) throw new BlockedException(ctx.Scan.AntiCheat);
        GameLauncher.EnsureClosed(ctx.Game);
    }

    public async Task<ChangePlan> PlanInstallAsync(GameContext ctx, IComponent component, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        CheckGlobal(ctx);
        var blockers = component.Blockers(ctx);
        if (blockers.Count > 0) throw new BlockedException(string.Join("\n", blockers));
        var plan = await component.PlanInstallAsync(ctx, options, progress, cancel);
        services.Log.Info($"Anteprima {component.Name} per {ctx.Game.Name}: {plan.Files.Count} file");
        return plan;
    }

    public async Task<ChangePlan> PlanImportAsync(GameContext ctx, IComponent component, string file,
        IProgress<OperationProgress>? progress, CancellationToken cancel)
    {
        if (component is not ILocalImport importer) throw new HdlssException($"{component.Name} non supporta l'importazione di file.");
        CheckGlobal(ctx);
        var blockers = component.Blockers(ctx);
        if (blockers.Count > 0) throw new BlockedException(string.Join("\n", blockers));
        return await importer.PlanImportAsync(ctx, file, progress, cancel);
    }

    public ChangePlan PlanRemoval(GameContext ctx, string componentId, bool force = false)
    {
        CheckGlobal(ctx);
        var record = ctx.State.Get(componentId) ?? throw new HdlssException("Il componente non è gestito da HDLSS5 in questo gioco.");
        var dependents = ComponentCatalog.Dependents(ctx, componentId).ToList();
        if (dependents.Count > 0)
            throw new BlockedException($"Rimuovi prima: {string.Join(", ", dependents.Select(d => d.Name))}.");
        return RemovalPlanner.Plan(services.Store, ctx.State, ctx.StateStamp, ctx.Game.Name, record, force);
    }

    public Journal Apply(GameContext ctx, ChangePlan plan, IProgress<OperationProgress>? progress)
    {
        GameLauncher.EnsureClosed(ctx.Game);
        try { return services.Engine.Apply(plan, progress); }
        finally { ctx.Refresh(); }
    }
}
