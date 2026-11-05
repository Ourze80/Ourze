using System.Windows;
using Hdlss.Core.Transactions;

namespace Hdlss.App.Views;

public partial class PlanDialog : Window
{
    public PlanDialog(ChangePlan plan)
    {
        InitializeComponent();
        bool import = plan.Adopt?.Meta.ContainsKey("imported") == true;
        Header.Text = import ? $"Importazione · {plan.ComponentName.Replace(" · importazione", "")}"
            : $"{(plan.Adopt != null ? "Adozione" : plan.IsRemoval ? "Rimozione" : "Installazione")} · {plan.ComponentName}";
        int adds = plan.Files.Count(f => !f.IsDelete && f.Expected == null);
        int replaces = plan.Files.Count(f => !f.IsDelete && f.Expected != null);
        int deletes = plan.Files.Count(f => f.IsDelete);
        SubHeader.Text = $"{adds} nuovi · {replaces} sostituiti o ripristinati · {deletes} rimossi. Ogni file modificato viene salvato prima della scrittura; l'operazione è annullabile dalla Cronologia.";
        Body.Text = plan.Describe();
        if (import) ApplyButton.Content = "Importa";
        else if (plan.Adopt != null) ApplyButton.Content = "Adotta";
        else if (plan.IsRemoval) ApplyButton.Content = plan.ComponentId.StartsWith("legacy:") ? "Ripristina" : "Rimuovi";
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
