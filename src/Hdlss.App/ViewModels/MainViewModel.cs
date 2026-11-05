using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hdlss.App.Views;
using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Settings;

namespace Hdlss.App.ViewModels;

public enum Page { Library, Game, Dlss }
public enum LibraryFilter { All, Active, Attention }

public sealed partial class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _summaryCts;

    public AppHost Host { get; }
    public ObservableCollection<GameItemViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public ObservableCollection<string> LogLines { get; } = [];
    public DlssPageViewModel DlssPage { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsLibrary), nameof(IsGame), nameof(IsDlss), nameof(IsGamesSection))] private Page _page = Page.Library;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FilterAll), nameof(FilterActive), nameof(FilterAttention))] private LibraryFilter _filter;
    [ObservableProperty] private GameDetailViewModel? _detail;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _busy;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _status = Loc.T("status.ready");
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _gpuText = "…";
    [ObservableProperty] private bool _logOpen;
    [ObservableProperty] private string _librarySummary = "";

    public MainViewModel(AppHost host)
    {
        Host = host;
        DlssPage = new DlssPageViewModel(this);
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = o => o is GameItemViewModel g
            && (Search.Length == 0 || g.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase))
            && Filter switch { LibraryFilter.Active => g.IsActive, LibraryFilter.Attention => g.NeedsAttention, _ => true };
        host.Log.Written += entry =>
        {
            if (entry.Level == LogLevel.Debug) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                LogLines.Add(entry.ToString());
                while (LogLines.Count > 400) LogLines.RemoveAt(0);
            });
        };
    }

    public bool IsLibrary => Page == Page.Library;
    public bool IsGame => Page == Page.Game;
    public bool IsDlss => Page == Page.Dlss;
    public bool IsGamesSection => Page is Page.Library or Page.Game;

    /// <summary>"Modalità avanzata": shows paths, options, all components, 1.x details and history.</summary>
    public bool Advanced
    {
        get => Host.Settings.AdvancedMode;
        set
        {
            if (Host.Settings.AdvancedMode == value) return;
            Host.Settings.AdvancedMode = value;
            TrySaveSettings();
            OnPropertyChanged();
            Detail?.AdvancedChanged();
        }
    }

    public bool FilterAll { get => Filter == LibraryFilter.All; set { if (value) Filter = LibraryFilter.All; } }
    public bool FilterActive { get => Filter == LibraryFilter.Active; set { if (value) Filter = LibraryFilter.Active; } }
    public bool FilterAttention { get => Filter == LibraryFilter.Attention; set { if (value) Filter = LibraryFilter.Attention; } }

    public async Task InitializeAsync()
    {
        var maintenance = Host.StartupMaintenanceAsync();
        var gpu = await Task.Run(() => Host.Gpu.Value);
        GpuText = gpu.Series == 0 ? gpu.Name : $"{gpu.Name.Replace("NVIDIA GeForce ", "")} · driver {gpu.Driver}";
        await RefreshAsync();
        await maintenance;
    }

    partial void OnSearchChanged(string value) => GamesView.Refresh();
    partial void OnFilterChanged(LibraryFilter value) => GamesView.Refresh();

    [RelayCommand]
    private async Task OpenGameAsync(GameItemViewModel? item)
    {
        if (item == null) return;
        Host.Settings.LastGameId = item.Game.Id;
        var detail = new GameDetailViewModel(this, item);
        Detail = detail;
        Page = Page.Game;
        await detail.ReloadAsync();
    }

    [RelayCommand]
    private void ShowLibrary()
    {
        Page = Page.Library;
        UpdateLibrarySummary();
        GamesView.Refresh();
    }

    [RelayCommand]
    private async Task ShowDlssAsync()
    {
        Page = Page.Dlss;
        if (!DlssPage.Loaded) await DlssPage.RefreshAsync();
    }

    [RelayCommand]
    private void GoBack()
    {
        if (Page != Page.Library) ShowLibrary();
    }

    [RelayCommand]
    private void ToggleLog() => LogOpen = !LogOpen;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Scanning = true;
        Status = "Cerco i giochi installati…";
        try
        {
            var games = await Task.Run(() => Host.Library.Scan());
            Games.Clear();
            foreach (var g in games) Games.Add(new GameItemViewModel(g));
            Status = $"{games.Count} giochi trovati";
            _ = ComputeSummariesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HdlssException)
        {
            Host.Log.Error("Scansione libreria", ex);
            Status = "Scansione non riuscita: " + ex.Message;
        }
        finally { Scanning = false; }
    }

    /// <summary>Resolves executables and card status in the background, one game at a time.</summary>
    private async Task ComputeSummariesAsync()
    {
        _summaryCts?.Cancel();
        var cts = _summaryCts = new CancellationTokenSource();
        foreach (var item in Games.ToList())
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                var summary = await Task.Run(() =>
                {
                    Host.Library.Resolve(item.Game);
                    return GameSummary.Compute(item.Game, Host.Services);
                }, cts.Token);
                item.Summary = summary;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HdlssException)
            {
                Host.Log.Warn($"Stato di {item.Name}", ex);
            }
            catch (OperationCanceledException) { return; }
        }
        UpdateLibrarySummary();
        GamesView.Refresh();
    }

    public void UpdateLibrarySummary()
    {
        int active = Games.Count(g => g.IsActive), attention = Games.Count(g => g.NeedsAttention);
        LibrarySummary = $"{Games.Count} giochi · {active} con Neural Rendering" + (attention > 0 ? $" · {attention} da sistemare" : "");
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var exe = Dialogs.PickExe(null);
        if (exe == null) return;
        try
        {
            var game = GameLibrary.Manual(exe);
            if (!Host.Settings.ManualGames.Any(m => string.Equals(m.Exe, game.Exe, StringComparison.OrdinalIgnoreCase)))
            {
                Host.Settings.ManualGames.Add(new ManualGameEntry { Exe = game.Exe, Name = game.Name });
                Host.SaveSettings();
            }
            await RefreshAsync();
            await OpenGameAsync(Games.FirstOrDefault(g => g.Game.Id == game.Id));
        }
        catch (HdlssException ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (Dialogs.Settings(this)) await RefreshAsync();
    }

    [RelayCommand]
    private void OpenGuide() => Dialogs.Guide();

    [RelayCommand(CanExecute = nameof(Busy))]
    private void Cancel() => _cts?.Cancel();

    public void TrySaveSettings()
    {
        try { Host.SaveSettings(); }
        catch (IOException ex) { Host.Log.Warn("Salvataggio impostazioni", ex); }
    }

    /// <summary>Runs a long operation with progress, cancellation and uniform error reporting.</summary>
    public async Task<bool> RunAsync(string title, Func<IProgress<OperationProgress>, CancellationToken, Task> work)
    {
        if (Busy) return false;
        Busy = true;
        _cts = new CancellationTokenSource();
        Status = title;
        Progress = 0;
        ProgressIndeterminate = true;
        var progress = new Progress<OperationProgress>(p =>
        {
            Status = p.Message;
            ProgressIndeterminate = p.Percent == null;
            if (p.Percent != null) Progress = p.Percent.Value;
        });
        try
        {
            await work(progress, _cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Operazione annullata.";
            return false;
        }
        catch (HdlssException ex)
        {
            Host.Log.Warn($"{title}: {ex.Message}");
            Status = ex.Message.Split('\n')[0];
            Dialogs.Error(ex.Message, ex is BlockedException ? "Operazione non possibile" : "Errore");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Host.Log.Error(title, ex);
            Status = ex.Message;
            Dialogs.Error($"{ex.Message}\n\nDettagli nel registro attività.");
            return false;
        }
        finally
        {
            Busy = false;
            ProgressIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }
}
