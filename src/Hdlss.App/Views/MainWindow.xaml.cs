using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Hdlss.App.ViewModels;

namespace Hdlss.App.Views;

public partial class MainWindow : Window
{
    private bool _scrollQueued;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.LogLines.CollectionChanged += (_, e) =>
                {
                    // Scroll after the ListBox has processed the change: doing it inside this handler can run
                    // before the ListBox sees the new line, WPF throws, the error is logged, and it loops.
                    if (e.Action != NotifyCollectionChangedAction.Add || _scrollQueued) return;
                    _scrollQueued = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                    {
                        _scrollQueued = false;
                        if (LogList.IsVisible && vm.LogLines.Count > 0) LogList.ScrollIntoView(vm.LogLines[^1]);
                    });
                };
        };
        // Ctrl+F: search the library from anywhere.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && DataContext is MainViewModel vm)
            {
                vm.ShowLibraryCommand.Execute(null);
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
        Closing += (_, e) =>
        {
            if (DataContext is MainViewModel { Busy: true })
            {
                MessageBox.Show(this, Loc.T("dlg.busy"), "HDLSS5");
                e.Cancel = true;
                return;
            }
            (DataContext as MainViewModel)?.TrySaveSettings();
        };
    }

    // Editable option (typed nits): commit the text on Enter or after picking from the list.
    private void EditableOption_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EditableOption_Commit(sender, e); e.Handled = true; }
    }

    private void EditableOption_Commit(object? sender, EventArgs e) =>
        (sender as System.Windows.Controls.ComboBox)?.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty)?.UpdateSource();
}
