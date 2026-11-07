using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hdlss.App.Views;
using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.Diagnostics;

namespace Hdlss.App.ViewModels;

/// <summary>One line of the per-game table: which DLL, what the game has, what is newer, in how many folders.</summary>
public sealed record DllRow(string Kind, string Current, string? Latest, int Folders)
{
    public bool IsOutdated => Latest != null;
    public string StateText => IsOutdated ? $"→ {Latest}" : Loc.T("dll.stLatest");
    public string FoldersText => Folders > 1 ? Loc.F("dll.inFolders", Folders) : "";
}

/// <summary>One entry of a version drop-down: "latest", an exact version, or "original" (the game's own files).</summary>
public sealed record VersionChoice(string Label, string Value)
{
    public const string LatestValue = "latest";
    public const string OriginalValue = "original";
    public override string ToString() => Label;
}

/// <summary>
/// "DLL" page: every game's NVIDIA DLSS (SR/FG/RR) and Streamline DLLs. For each game a drop-down
/// picks the newest build, an older one, or the game's original files; "Applica" does it.
/// </summary>
public sealed partial class DlssPageViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main => main;
    public ObservableCollection<DlssGameViewModel> Games { get; } = [];
    public bool Loaded { get; private set; }

    [ObservableProperty] private bool _checking;
    [ObservableProperty] private string _summary = Loc.T("dll.notChecked");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNewest))] private string _newest = "";
    public bool HasNewest => Newest.Length > 0;
    [ObservableProperty] private int _outdatedCount;

    internal Dictionary<string, DlssBuild>? LatestDlss { get; private set; }
    internal IReadOnlyList<Version> DlssVersions { get; private set; } = [];
    internal IReadOnlyList<StreamlineRelease> StreamlineReleases { get; private set; } = [];

    public bool HasOutdated => OutdatedCount > 0;
    public string UpdateAllText => Loc.F("dll.updateAll", OutdatedCount);
    partial void OnOutdatedCountChanged(int value) { OnPropertyChanged(nameof(HasOutdated)); OnPropertyChanged(nameof(UpdateAllText)); }

    /// <summary>Texts are built when the page loads: after a language change, rebuild them.</summary>
    public async Task OnLanguageChangedAsync()
    {
        OnPropertyChanged(nameof(UpdateAllText));
        if (Loaded) await RefreshAsync();
        else Summary = Loc.T("dll.notChecked");
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Checking) return;
        Checking = true;
        Summary = Loc.T("dll.checking");
        try
        {
            var host = main.Host;
            try
            {
                var all = await DlssCatalog.AllAsync(host.Services.Http, CancellationToken.None);
                LatestDlss = all.ToDictionary(p => p.Key, p => p.Value[0], StringComparer.OrdinalIgnoreCase);
                DlssVersions = all.TryGetValue("nvngx_dlss.dll", out var sr) ? sr.Select(b => b.Version).Take(15).ToList() : [];
            }
            catch (Exception ex) when (ex is HdlssException or HttpRequestException or IOException) { host.Log.Warn("Catalogo DLSS", ex); LatestDlss = null; DlssVersions = []; }
            try { StreamlineReleases = (await StreamlineCatalog.AllAsync(host.Services.Http, CancellationToken.None)).Take(12).ToList(); }
            catch (Exception ex) when (ex is HdlssException or HttpRequestException or IOException) { host.Log.Warn("Catalogo Streamline", ex); StreamlineReleases = []; }

            var parts = new List<string>();
            if (LatestDlss != null) parts.Add("DLSS " + string.Join(" · ", LatestDlss.Values.Select(b => $"{DlssCatalog.Label(b.Kind)} {b.Version}")));
            if (StreamlineReleases.Count > 0) parts.Add($"Streamline {StreamlineReleases[0].Version}");
            Newest = parts.Count == 0 ? Loc.T("dll.unreachable") : Loc.T("dll.newest") + string.Join("   ·   ", parts);

            var rows = await Task.Run(() =>
            {
                var list = new List<DlssGameViewModel>();
                foreach (var item in main.Games.ToList())
                {
                    var game = item.Game;
                    if (game.ProtectedReason != null) continue;
                    try
                    {
                        host.Library.Resolve(game);
                        if (!game.HasExe) continue;
                        var ctx = new GameContext(game, host.Services, host.Gpu.Value);
                        var dlss = DlssUpdateComponent.Inspect(ctx, LatestDlss);
                        var sl = StreamlineComponent.Folders(ctx);
                        if (dlss.Count > 0 || sl.Count > 0) list.Add(new DlssGameViewModel(this, item, ctx, dlss, sl));
                    }
                    catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException)
                    {
                        host.Log.Debug($"DLL · {game.Name}: {ex.Message}");
                    }
                }
                return list;
            });
            Games.Clear();
            foreach (var r in rows.OrderByDescending(r => r.Outdated).ThenBy(r => r.Name)) Games.Add(r);
            OutdatedCount = rows.Count(r => r.Outdated);
            Summary = rows.Count == 0 ? Loc.T("dll.none")
                : Loc.F(rows.Count == 1 ? "dll.summary.one" : "dll.summary.many", rows.Count, OutdatedCount);
            Loaded = true;
        }
        finally { Checking = false; }
    }

    /// <summary>Applies one component choice: newest / exact version (install) or original (remove).</summary>
    private async Task ApplyChoice(GameContext ctx, string componentId, string optionKey, VersionChoice choice,
        IProgress<OperationProgress> progress, CancellationToken cancel)
    {
        var host = main.Host;
        ctx.Refresh();
        Hdlss.Core.Transactions.ChangePlan plan;
        if (choice.Value == VersionChoice.OriginalValue)
        {
            if (!ctx.Installed(componentId)) return;
            plan = await RemovalFlow.PlanAsync(host, ctx, componentId, cancel);
        }
        else
        {
            var options = new Dictionary<string, string>();
            if (choice.Value != VersionChoice.LatestValue) options[optionKey] = choice.Value;
            plan = await Task.Run(() => host.Components.PlanInstallAsync(ctx, ComponentCatalog.Get(componentId), options, progress, cancel), cancel);
        }
        if (plan.IsEmpty) return;
        if (main.Advanced && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
        await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
    }

    internal async Task ApplyAsync(DlssGameViewModel row)
    {
        var what = new List<string>();
        if (row.DlssChanged) what.Add($"DLSS → {row.SelectedDlss!.Label}");
        if (row.StreamlineChanged) what.Add($"Streamline → {row.SelectedStreamline!.Label}");
        if (what.Count == 0) return;
        if (!main.Advanced && !Dialogs.Confirm(Loc.F("dll.apply.confirm", row.Name, string.Join("\n• ", what)))) return;
        var ok = await main.RunAsync($"DLL · {row.Name}", async (p, c) =>
        {
            if (row.DlssChanged) await ApplyChoice(row.Context, DlssUpdateComponent.ComponentId, DlssUpdateComponent.VersionOption, row.SelectedDlss!, p, c);
            if (row.StreamlineChanged) await ApplyChoice(row.Context, StreamlineComponent.ComponentId, StreamlineComponent.VersionOption, row.SelectedStreamline!, p, c);
        });
        if (ok) await RefreshAsync();
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var rows = Games.Where(g => g.Outdated).ToList();
        if (rows.Count == 0) return;
        if (!Dialogs.Confirm(Loc.F("dll.updateAll.confirm", rows.Count, string.Join("\n• ", rows.Select(r => r.Name))))) return;
        var failed = new List<string>();
        var latest = new VersionChoice("", VersionChoice.LatestValue);
        await main.RunAsync(Loc.T("dll.updateAll.op"), async (p, c) =>
        {
            foreach (var row in rows)
            {
                try
                {
                    if (row.HasDlss) await ApplyChoice(row.Context, DlssUpdateComponent.ComponentId, DlssUpdateComponent.VersionOption, latest, p, c);
                    if (row.HasStreamline) await ApplyChoice(row.Context, StreamlineComponent.ComponentId, StreamlineComponent.VersionOption, latest, p, c);
                }
                catch (HdlssException ex) { failed.Add($"{row.Name}: {ex.Message.Split('\n')[0]}"); }
            }
        });
        await RefreshAsync();
        Dialogs.Info(failed.Count == 0 ? Loc.T("dll.updateAll.ok") : Loc.F("dll.updateAll.partial", failed.Count, string.Join("\n", failed)));
    }
}

public sealed partial class DlssGameViewModel : ObservableObject
{
    private readonly DlssPageViewModel _page;
    private readonly GameItemViewModel _item;
    private readonly VersionChoice? _initialDlss, _initialStreamline;

    public DlssGameViewModel(DlssPageViewModel page, GameItemViewModel item, GameContext context,
        IReadOnlyList<DlssUpdateComponent.FileState> dlss, IReadOnlyList<StreamlineComponent.FolderState> streamline)
    {
        _page = page;
        _item = item;
        Context = context;
        HasDlss = dlss.Count > 0;
        HasStreamline = streamline.Count > 0;
        bool dlssManaged = context.Installed(DlssUpdateComponent.ComponentId);
        bool slManaged = context.Installed(StreamlineComponent.ComponentId);
        Managed = dlssManaged || slManaged;

        var latestSl = page.StreamlineReleases.FirstOrDefault()?.Version;
        Outdated = dlss.Any(f => f.Outdated) || latestSl != null && streamline.Any(f => f.Current != null && f.Current < latestSl);

        var rows = dlss.GroupBy(f => (f.Label, f.Current))
            .Select(g => new DllRow(g.Key.Label, g.Key.Current?.ToString() ?? "?", g.First().Outdated ? g.First().Latest?.ToString() : null, g.Count()))
            .OrderBy(r => KindOrder(r.Kind)).ToList();
        foreach (var group in streamline.GroupBy(f => f.Current))
            rows.Add(new DllRow("Streamline", group.Key?.ToString() ?? "?", latestSl != null && group.Key < latestSl ? latestSl.ToString() : null, group.Count()));
        Rows = rows;
        FilesTooltip = Loc.T("dll.files") + string.Join("\n", dlss.Select(f => $"{f.Relative}   {f.Current}")
            .Concat(streamline.Select(f => Loc.F("dll.modules", f.Relative.Length == 0 ? "" : f.Relative + "/", f.Current, f.Files.Count))));

        // DLSS drop-down: original · newest · older builds. The current state is pre-selected.
        var currentSr = dlss.FirstOrDefault(f => f.FullPath.EndsWith("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))?.Current ?? dlss.FirstOrDefault()?.Current;
        DlssChoices = Choices(page.DlssVersions.Select(v => v.ToString()).ToList(), currentSr?.ToString(), dlssManaged);
        _initialDlss = dlssManaged ? DlssChoices.FirstOrDefault(c => c.Value == currentSr?.ToString()) ?? DlssChoices[0] : DlssChoices[0];
        _selectedDlss = _initialDlss;

        var currentSl = streamline.FirstOrDefault()?.Current;
        StreamlineChoices = Choices(page.StreamlineReleases.Select(r => r.Tag).ToList(), currentSl == null ? null : "v" + currentSl, slManaged);
        _initialStreamline = slManaged ? StreamlineChoices.FirstOrDefault(c => c.Value == "v" + currentSl) ?? StreamlineChoices[0] : StreamlineChoices[0];
        _selectedStreamline = _initialStreamline;
    }

    /// <summary>Same row order in every game: Super Resolution, Frame Generation, Ray Reconstruction.</summary>
    private static int KindOrder(string kind) =>
        kind.Contains("Super", StringComparison.OrdinalIgnoreCase) ? 0
        : kind.Contains("Frame", StringComparison.OrdinalIgnoreCase) ? 1
        : kind.Contains("Ray", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    private static List<VersionChoice> Choices(IReadOnlyList<string> versions, string? current, bool managed)
    {
        var list = new List<VersionChoice>
        {
            new(managed ? Loc.T("dll.choice.original") : Loc.F("dll.choice.originalCur", current != null ? $" ({current.TrimStart('v')})" : ""), VersionChoice.OriginalValue),
        };
        if (versions.Count > 0) list.Add(new(Loc.F("dll.choice.latest", versions[0].TrimStart('v')), VersionChoice.LatestValue));
        foreach (var v in versions.Skip(1)) list.Add(new(v.TrimStart('v') + (managed && v == current ? Loc.T("dll.choice.current") : ""), v));
        if (managed && versions.Count > 0 && versions[0] == current) list[1] = list[1] with { Label = list[1].Label + Loc.T("dll.choice.current"), Value = versions[0] };
        return list;
    }

    public GameContext Context { get; }
    public string Name => _item.Name;
    public ImageSource? Cover => _item.Cover;
    public ImageSource? ExeIcon => _item.ExeIcon;
    public bool HasDlss { get; }
    public bool HasStreamline { get; }
    public bool Managed { get; }
    public bool Outdated { get; }
    public IReadOnlyList<DllRow> Rows { get; }
    public string FilesTooltip { get; }
    public IReadOnlyList<VersionChoice> DlssChoices { get; }
    public IReadOnlyList<VersionChoice> StreamlineChoices { get; }
    public string StateText => Outdated ? Loc.T("dll.state.outdated") : Loc.T("dll.state.ok") + (Managed ? Loc.T("dll.state.changed") : "");
    public string StateBrushKey => Outdated ? "Warn" : "Success";
    public System.Windows.Media.Brush StateBrush => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[StateBrushKey];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasChanges))] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private VersionChoice? _selectedDlss;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasChanges))] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))] private VersionChoice? _selectedStreamline;

    public bool DlssChanged => HasDlss && SelectedDlss != null && SelectedDlss != _initialDlss;
    public bool StreamlineChanged => HasStreamline && SelectedStreamline != null && SelectedStreamline != _initialStreamline;
    public bool HasChanges => DlssChanged || StreamlineChanged;

    [RelayCommand(CanExecute = nameof(HasChanges))] private Task ApplyAsync() => _page.ApplyAsync(this);
}
