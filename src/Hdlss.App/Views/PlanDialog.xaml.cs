using System.Windows;
using Hdlss.Core.Transactions;

namespace Hdlss.App.Views;

public partial class PlanDialog : Window
{
    public PlanDialog(ChangePlan plan)
    {
        InitializeComponent();
        bool import = plan.Adopt?.Meta.ContainsKey("imported") == true;
        Header.Text = plan.Cleanup ? plan.ComponentName : import ? $"{Loc.T("plan.import")} · {System.Text.RegularExpressions.Regex.Replace(plan.ComponentName, @"\s·\s(importazione|import|imported)$", "")}"
            : $"{Loc.T(plan.Adopt != null ? "plan.adopt" : plan.IsRemoval ? "plan.removal" : "plan.install")} · {plan.ComponentName}";
        int adds = plan.Files.Count(f => !f.IsDelete && f.Expected == null);
        int replaces = plan.Files.Count(f => !f.IsDelete && f.Expected != null);
        int deletes = plan.Files.Count(f => f.IsDelete);
        SubHeader.Text = Loc.F("plan.sub", adds, replaces, deletes);
        Body.Text = plan.Describe();
        if (import) ApplyButton.Content = Loc.T("foreign.import");
        else if (plan.Adopt != null) ApplyButton.Content = Loc.T("legacy.adopt");
        else if (plan.IsRemoval) ApplyButton.Content = plan.ComponentId.StartsWith("legacy:") ? Loc.T("legacy.restore") : Loc.T("component.remove");
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
