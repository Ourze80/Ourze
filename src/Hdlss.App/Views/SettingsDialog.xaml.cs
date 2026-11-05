using System.Diagnostics;
using System.Windows;
using Hdlss.Core.Legacy;

namespace Hdlss.App.Views;

public partial class SettingsDialog : Window
{
    private readonly AppHost _host;

    public SettingsDialog(AppHost host)
    {
        InitializeComponent();
        _host = host;
        LanguageBox.ItemsSource = new[] { "it · Italiano", "en · English" };
        LanguageBox.SelectedIndex = host.Settings.Language == "en" ? 1 : 0;
        LegacyDir.Text = host.Settings.LegacyAppDir ?? "";
        Libraries.Text = string.Join(Environment.NewLine, host.Settings.ExtraSteamLibraries);
        LegacyDir.TextChanged += (_, _) => UpdateLegacyInfo();
        UpdateLegacyInfo();
        DataInfo.Text = $"{host.Paths.Data} · archivio backup {host.Services.Store.SizeBytes() / 1048576.0:N0} MB";
    }

    private void UpdateLegacyInfo()
    {
        var data = LegacyInstalls.FindDataFolder(string.IsNullOrWhiteSpace(LegacyDir.Text) ? null : LegacyDir.Text.Trim(), _host.Paths.AppDir);
        LegacyInfo.Text = data == null
            ? "Nessuna cartella dati 1.x trovata. Serve solo per ripristinare giochi modificati dalla versione precedente."
            : $"Dati 1.x: {data}. I file della 1.x non vengono mai modificati da questa versione.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = Dialogs.PickFolder(LegacyDir.Text);
        if (folder != null) LegacyDir.Text = folder;
    }

    private async void Maintenance_Click(object sender, RoutedEventArgs e)
    {
        MaintenanceInfo.Text = "Pulizia in corso…";
        try
        {
            var report = await Task.Run(() => _host.Maintenance.Run(_host.Settings.KeepJournalsPerGame));
            MaintenanceInfo.Text = report.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MaintenanceInfo.Text = ex.Message;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Paths.Data}\"") { UseShellExecute = true });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        s.Language = LanguageBox.SelectedIndex == 1 ? "en" : "it";
        s.LegacyAppDir = string.IsNullOrWhiteSpace(LegacyDir.Text) ? null : LegacyDir.Text.Trim();
        s.ExtraSteamLibraries = Libraries.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _host.SaveSettings();
        Loc.Instance.Language = s.Language;
        DialogResult = true;
    }
}
