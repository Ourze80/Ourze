using System.Windows;
using Hdlss.App.ViewModels;
using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Transactions;
using Microsoft.Win32;

namespace Hdlss.App.Views;

/// <summary>Thin facade so view models never touch Window types directly.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    public static void Error(string message, string title = "Errore") =>
        MessageBox.Show(Owner!, message, "HDLSS5 · " + title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Info(string message) =>
        MessageBox.Show(Owner!, message, "HDLSS5", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(Owner!, message, "HDLSS5", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static bool ConfirmPlan(ChangePlan plan) =>
        new PlanDialog(plan) { Owner = Owner }.ShowDialog() == true;

    public static string? PickExe(string? folder)
    {
        var dialog = new OpenFileDialog { Filter = "Eseguibile del gioco|*.exe", InitialDirectory = folder ?? "" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static void Guide() => new GuideDialog { Owner = Owner }.ShowDialog();

    public static string? PickFolder(string? initial)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = initial ?? "" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public static bool EditExecutable(AppHost host, Game game) =>
        new ExeDialog(host, game) { Owner = Owner }.ShowDialog() == true;

    public static bool History(AppHost host, GameContext ctx) =>
        new HistoryDialog(host, ctx) { Owner = Owner }.ShowDialog() == true;

    public static bool Settings(MainViewModel main) =>
        new SettingsDialog(main.Host) { Owner = Owner }.ShowDialog() == true;
}
