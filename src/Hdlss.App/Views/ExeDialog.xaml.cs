using System.Windows;
using System.Windows.Controls;
using Hdlss.Core;
using Hdlss.Core.Games;
using Hdlss.Core.IO;

namespace Hdlss.App.Views;

public partial class ExeDialog : Window
{
    /// <summary>One entry of the executable picker.</summary>
    public sealed record ExeChoice(string Rel, string Label);

    private readonly AppHost _host;
    private readonly Game _game;
    private readonly List<ExeChoice> _choices = [];
    private readonly string? _recommended;

    public ExeDialog(AppHost host, Game game)
    {
        InitializeComponent();
        _host = host;
        _game = game;
        Func<string, bool> accept = f => PeFile.IsPe64(f) || XboxLibrary.IsDeclaredX64(game, f);
        var found = ExecutableDetector.Candidates(game.Root, accept: accept);
        var (pick, _) = ExecutableDetector.Pick(game.Root, accept);
        _recommended = pick.Length > 0 ? PathGuard.RelativeTo(game.Root, pick) : null;

        foreach (var f in found) Add(PathGuard.RelativeTo(game.Root, f));
        var current = game.HasExe ? PathGuard.RelativeTo(game.Root, game.Exe) : null;
        if (current != null) Add(current);
        // Recommended first, then the rest by size (the game is almost always the biggest).
        _choices.Sort((a, b) => (b.Rel == _recommended).CompareTo(a.Rel == _recommended) is var c and not 0 ? c
            : Size(b.Rel).CompareTo(Size(a.Rel)));
        Targets.ItemsSource = _choices;
        Targets.SelectedItem = _choices.FirstOrDefault(c => c.Rel.Equals(current ?? _recommended, StringComparison.OrdinalIgnoreCase));

        var saved = host.Settings.Profile(game.Id).ModFolder;
        ModAuto.IsChecked = string.IsNullOrEmpty(saved);
        ModFolder.Text = saved ?? "";
        UpdateModHint();

        var detected = game.HasExe ? ExecutableDetector.DetectApi(game.Exe) : GraphicsApi.Unknown;
        ApiAuto.Content = $"{Loc.T("exe.auto")} ({(detected == GraphicsApi.Unknown ? "non riconosciuta" : detected.ToString())})";
        var chosen = host.Settings.Profile(game.Id).ApiOverride;
        (chosen switch { GraphicsApi.DX11 => ApiDx11, GraphicsApi.DX12 => ApiDx12, GraphicsApi.Vulkan => ApiVulkan, _ => ApiAuto }).IsChecked = true;
        if (game.Store == GameStore.Manual) Targets.IsEnabled = false;
        ConfigFolder.Text = host.Settings.Profile(game.Id).ConfigFolder ?? host.Library.UnrealConfigFolder(game) ?? "";
    }

    private long Size(string rel)
    {
        try { return new FileInfo(PathGuard.Resolve(_game.Root, rel)).Length; } catch (Exception ex) when (ex is IOException or UnsafePathException) { return 0; }
    }

    private ExeChoice Add(string rel)
    {
        var existing = _choices.FirstOrDefault(c => c.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var full = PathGuard.Resolve(_game.Root, rel);
        var notes = new List<string>();
        var info = ExecutableDetector.Describe(full);
        if (info.Length > 0) notes.Add(info);
        if (rel.Equals(_recommended, StringComparison.OrdinalIgnoreCase)) notes.Add("consigliato");
        else if (ExecutableDetector.IsServiceExecutable(full)) notes.Add("strumento, non il gioco");
        var choice = new ExeChoice(rel, notes.Count == 0 ? rel : $"{rel}   —   {string.Join(" · ", notes)}");
        _choices.Add(choice);
        return choice;
    }

    private string? AutoModFolder() =>
        Targets.SelectedItem is ExeChoice c ? ExecutableDetector.ModFolderFor(_game.Root, PathGuard.Resolve(_game.Root, c.Rel)) : null;

    private void UpdateModHint()
    {
        if (ModHint == null) return;
        ModFolder.IsEnabled = ModBrowse.IsEnabled = ModAuto.IsChecked != true;
        string folder;
        if (ModAuto.IsChecked == true)
        {
            var auto = AutoModFolder();
            folder = auto != null ? PathGuard.RelativeTo(_game.Root, auto)
                : Targets.SelectedItem is ExeChoice c ? (Path.GetDirectoryName(c.Rel) is { Length: > 0 } d ? d : ".") : "?";
            ModHint.Text = auto != null
                ? $"Le mod andranno in «{folder}»: il gioco si avvia da un'altra cartella ma carica le DLL da qui (tipico di Unreal Engine)."
                : $"Le mod andranno in «{folder}», la cartella dell'eseguibile.";
        }
        else ModHint.Text = "Cartella scelta a mano, relativa alla cartella del gioco. Usala solo se sai da dove il gioco carica dxgi.dll (ad es. guardando ReShade.log).";
    }

    private void Targets_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModHint();
    private void ModAuto_Changed(object sender, RoutedEventArgs e) => UpdateModHint();

    private void BrowseMod_Click(object sender, RoutedEventArgs e)
    {
        var folder = Dialogs.PickFolder(ModFolder.Text.Length > 0 ? Path.Combine(_game.Root, ModFolder.Text) : _game.Root);
        if (folder == null) return;
        try { ModFolder.Text = PathGuard.RelativeTo(_game.Root, folder); }
        catch (Exception ex) when (ex is HdlssException or UnsafePathException) { ErrorText.Text = "La cartella delle mod deve stare dentro la cartella del gioco."; }
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var folder = Dialogs.PickFolder(ConfigFolder.Text.Length > 0 ? ConfigFolder.Text
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (folder != null) ConfigFolder.Text = folder;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var file = Dialogs.PickExe(_game.Root);
        if (file == null) return;
        try
        {
            var rel = PathGuard.RelativeTo(_game.Root, file);
            if (!PeFile.IsPe64(file) && !XboxLibrary.IsDeclaredX64(_game, file)) throw new HdlssException("L'eseguibile non è x64.");
            var choice = Add(rel);
            Targets.Items.Refresh();
            Targets.SelectedItem = choice;
        }
        catch (Exception ex) when (ex is HdlssException or UnsafePathException) { ErrorText.Text = ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = _host.Settings.Profile(_game.Id);
            if (_game.Store != GameStore.Manual)
            {
                if (Targets.SelectedItem is not ExeChoice choice) throw new HdlssException("Scegli un eseguibile.");
                var full = PathGuard.Resolve(_game.Root, choice.Rel);
                if (!File.Exists(full) || (!PeFile.IsPe64(full) && !XboxLibrary.IsDeclaredX64(_game, full))) throw new HdlssException("Eseguibile x64 non valido.");
                profile.Target = choice.Rel;

                if (ModAuto.IsChecked == true) profile.ModFolder = null;
                else
                {
                    var rel = ModFolder.Text.Trim().TrimEnd('\\', '/');
                    if (rel.Length == 0) throw new HdlssException("Indica la cartella delle mod o scegli «Automatica».");
                    if (!Directory.Exists(PathGuard.Resolve(_game.Root, rel))) throw new HdlssException("La cartella delle mod non esiste.");
                    profile.ModFolder = rel;
                }
            }
            profile.ApiOverride = ApiDx11.IsChecked == true ? GraphicsApi.DX11 : ApiDx12.IsChecked == true ? GraphicsApi.DX12
                : ApiVulkan.IsChecked == true ? GraphicsApi.Vulkan : null;
            var config = ConfigFolder.Text.Trim();
            var auto = _host.Library.UnrealConfigFolder(_game);
            if (config.Length == 0 || string.Equals(config, auto, StringComparison.OrdinalIgnoreCase)) profile.ConfigFolder = null;
            else
            {
                if (!File.Exists(Path.Combine(config, "GameUserSettings.ini"))) throw new HdlssException("La cartella indicata non contiene GameUserSettings.ini.");
                Hdlss.Core.Transactions.Locations.ValidateRoot(config, _game.Root);
                profile.ConfigFolder = Path.GetFullPath(config);
            }
            _host.SaveSettings();
            _host.Log.Info($"{_game.Name}: eseguibile {profile.Target ?? _game.Exe} · mod {profile.ModFolder ?? "automatica"} · API {profile.ApiOverride?.ToString() ?? "automatica"}");
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HdlssException or IOException or UnsafePathException)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
