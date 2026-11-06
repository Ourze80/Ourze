using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Hdlss.App.ViewModels;

namespace Hdlss.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.LogLines.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                        LogList.ScrollIntoView(LogList.Items[^1]);
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
                MessageBox.Show(this, "Attendi la fine dell'operazione in corso.", "HDLSS5");
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
