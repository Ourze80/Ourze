using System.Windows;

namespace Hdlss.App.Views;

/// <summary>Small themed question with a primary action and a cancel button. Enter and Esc = cancel (the primary action may be destructive).</summary>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string message, string primary, string cancel)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        CancelButton.Content = cancel;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
