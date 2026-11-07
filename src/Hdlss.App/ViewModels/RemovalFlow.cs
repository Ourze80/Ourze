using System.Windows;
using Hdlss.App.Views;
using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.Transactions;

namespace Hdlss.App.ViewModels;

/// <summary>The one way the app plans a removal: if a file was changed by another program, ask whether to leave it and remove the rest.</summary>
public static class RemovalFlow
{
    public static async Task<ChangePlan> PlanAsync(AppHost host, GameContext ctx, string componentId, CancellationToken cancel)
    {
        try { return await Task.Run(() => host.Components.PlanRemoval(ctx, componentId, false), cancel); }
        catch (ModifiedByOthersException ex)
        {
            var text = ex.Message + Loc.T("removal.note");
            var accepted = Application.Current.Dispatcher.Invoke(() => Dialogs.Choose(Loc.T("removal.blocked"), text, Loc.T("removal.anyway")));
            if (!accepted) throw new OperationCanceledException();
            return await Task.Run(() => host.Components.PlanRemoval(ctx, componentId, true), cancel);
        }
    }
}
