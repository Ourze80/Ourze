using System.Windows;
using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.Transactions;

namespace Hdlss.App.Views;

public partial class HistoryDialog : Window
{
    private readonly AppHost _host;
    private readonly GameContext _ctx;
    private bool _changed;

    public HistoryDialog(AppHost host, GameContext ctx)
    {
        InitializeComponent();
        _host = host;
        _ctx = ctx;
        Load();
        Closing += (_, _) => DialogResult = _changed;
    }

    private void Load() =>
        List.ItemsSource = _host.Services.Engine.Journals()
            .Where(j => string.Equals(j.Folder, _ctx.Folder, StringComparison.OrdinalIgnoreCase)).ToList();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Journal journal) return;
        var newer = _host.Services.Engine.Journals()
            .Where(j => string.Equals(j.Folder, _ctx.Folder, StringComparison.OrdinalIgnoreCase) && j.Status == JournalStatus.Completed && j.Time > journal.Time)
            .ToList();
        if (newer.Count > 0 && !Dialogs.Confirm($"Ci sono {newer.Count} operazioni successive. Annullare solo questa può lasciare i componenti incoerenti. Continuare?"))
            return;
        if (!Dialogs.Confirm($"Annullare «{journal.Description}» del {journal.Time:g}?")) return;
        try
        {
            ComponentService.CheckGlobal(_ctx);
            _host.Services.Engine.Undo(journal);
            _changed = true;
            Load();
        }
        catch (HdlssException ex) { Dialogs.Error(ex.Message); }
    }
}
