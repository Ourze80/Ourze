using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hdlss.App.Views;
using Hdlss.Core;
using Hdlss.Core.Components;

namespace Hdlss.App.ViewModels;

public sealed class OptionViewModel(ComponentOption option) : ObservableObject
{
    public ComponentOption Option { get; } = option;
    public string Label => Option.Label;
    public IReadOnlyList<ChoiceItem> Choices { get; } = option.Choices.Select(c => new ChoiceItem(c.Value, c.Label)).ToList();
    public event Action? Changed;
    private ChoiceItem? _selected;
    public ChoiceItem Selected
    {
        get => _selected ??= Choices.First(c => c.Value == Option.Default);
        set
        {
            if (value == null) return;   // an editable ComboBox clears SelectedItem for typed values
            if (SetProperty(ref _selected, value)) { OnPropertyChanged(nameof(Text)); Changed?.Invoke(); }
        }
    }

    /// <summary>The option also accepts a typed number (e.g. peak nits).</summary>
    public bool IsEditable => Option.Custom != null;
    public bool IsFixed => !IsEditable;
    public string? Hint => Option.Custom?.Hint;

    private string? _error;
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error != null;

    /// <summary>Text of the editable ComboBox: a choice label or a typed number.</summary>
    public string Text
    {
        get => Selected.Label;
        set
        {
            if (value == Selected.Label) { Error = null; return; }
            var normalized = Choices.FirstOrDefault(c => c.Label == value)?.Value ?? Option.Normalize(value);
            if (normalized == null) { Error = Hint; return; }
            Error = null;
            Select(normalized);
        }
    }

    /// <summary>Selects a stored value: a choice, or a valid custom number.</summary>
    public bool Select(string value)
    {
        if (Choices.FirstOrDefault(c => c.Value == value) is { } choice) { Selected = choice; return true; }
        if (Option.Custom is { } range && range.Normalize(value) is { } n) { Selected = new ChoiceItem(n, range.Label(n)); return true; }
        return false;
    }
}

public sealed record ChoiceItem(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the component card offers: install only if missing, update only if newer.</summary>
public enum UpdateState { NotInstalled, Checking, UpToDate, UpdateAvailable, OptionsChanged, Unknown, Unavailable }

public sealed partial class ComponentViewModel : ObservableObject
{
    private readonly GameDetailViewModel _owner;
    private readonly Hdlss.Core.Transactions.ComponentRecord? _record;

    public IComponent Component { get; }
    public int Step { get; }
    public string Name => Component.Name;
    public string Description => Component.Description;
    public ObservableCollection<OptionViewModel> Options { get; }

    public ComponentStatusKind Kind { get; }
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusBrushKey = "Muted";
    [ObservableProperty] private UpdateState _state;
    [ObservableProperty] private string? _latestVersion;
    public string BlockersText { get; }
    public bool HasBlockers => BlockersText.Length > 0;
    /// <summary>The blockers as "title / detail" notices, in the same order as <see cref="BlockersText"/>.</summary>
    public IReadOnlyList<NoticeViewModel> Blockers { get; }
    public bool IsManaged { get; }
    private bool IsTakenOver { get; }
    /// <summary>Not installed by HDLSS5, or taken over since by another program: HDLSS5 leaves it alone.</summary>
    public bool IsExternallyManaged => !IsLegacy && !Component.ChecksGameFiles && (Kind == ComponentStatusKind.External || IsTakenOver);
    private bool IsLegacy { get; }
    public bool HasOptions => Options.Count > 0;
    public bool HasStep => Step > 0;
    public bool CanImportFile => Component is ILocalImport;
    /// <summary>Installed by another tool and recognised: can be taken over with "Importa in HDLSS5".</summary>
    public bool CanImportExternal => _owner.Foreign.Any(f => f.Install.ComponentId == Component.Id);
    public string ImportLabel => (Component as ILocalImport)?.ImportLabel ?? "";
    /// <summary>The RenoDX NR card also takes a locally imported DLSS5 NR (Lecram 12.x) package.</summary>
    public bool CanImportNr => Component.Id == RenoDxNrComponent.ComponentId;
    /// <summary>"Importate: 12.9.9, 12.9.8" or empty.</summary>
    public string NrImportedText
    {
        get
        {
            if (!CanImportNr) return "";
            var list = _owner.Main.Host.Services.NrAddons.Packages;
            return list.Count == 0 ? "" : Loc.F("nr.imported", string.Join(", ", list.Select(p => p.Version.ToString())));
        }
    }
    public bool HasNrImported => NrImportedText.Length > 0;
    public string CategoryName => ComponentCatalog.CategoryName(Component.Category);

    /// <summary>Main button: shown only when there is something to do.</summary>
    public bool ShowAction => !CanImportExternal && State is UpdateState.NotInstalled or UpdateState.UpdateAvailable or UpdateState.OptionsChanged;
    public string ActionLabel => State switch
    {
        UpdateState.UpdateAvailable when IsDowngrade => Loc.T("comp.reinstall"),
        UpdateState.UpdateAvailable => LatestVersion is { Length: <= 28 } v ? Loc.F("comp.updateTo", v) : Loc.T("component.update"),
        UpdateState.OptionsChanged => Loc.T("comp.applyChanges"),
        _ => Loc.T("component.install"),
    };
    /// <summary>Installed version and the one on offer, shown under the button.</summary>
    [ObservableProperty] private string _versionsText = "";
    public bool HasVersions => State == UpdateState.UpdateAvailable && VersionsText.Length > 0;
    /// <summary>The offered version is not newer by number: the button reinstalls rather than updates.</summary>
    [ObservableProperty] private bool _isDowngrade;
    public bool ShowDowngradeWarning => HasVersions && IsDowngrade;
    partial void OnVersionsTextChanged(string value) { OnPropertyChanged(nameof(HasVersions)); OnPropertyChanged(nameof(ShowDowngradeWarning)); }
    partial void OnIsDowngradeChanged(bool value) { OnPropertyChanged(nameof(ActionLabel)); OnPropertyChanged(nameof(ShowDowngradeWarning)); }
    /// <summary>Secondary "Reinstalla": when the latest version can't be checked, or for tinkerers.</summary>
    public bool ShowReinstall => IsManaged && (State == UpdateState.Unknown || State == UpdateState.UpToDate && _owner.Advanced);
    public bool IsChecking => State == UpdateState.Checking;
    public string CheckingText => IsManaged || Kind == ComponentStatusKind.External ? Loc.T("comp.checkingUpdates") : Loc.T("comp.checkingAvail");
    /// <summary>Why this component can't be installed for the game (e.g. no mod in the catalog).</summary>
    [ObservableProperty] private string _unavailableText = "";
    public bool IsUnavailable => UnavailableText.Length > 0;
    partial void OnUnavailableTextChanged(string value) => OnPropertyChanged(nameof(IsUnavailable));

    public Brush StatusBrush => (Brush)System.Windows.Application.Current.Resources[StatusBrushKey];

    partial void OnStatusBrushKeyChanged(string value) => OnPropertyChanged(nameof(StatusBrush));
    partial void OnStateChanged(UpdateState value)
    {
        OnPropertyChanged(nameof(ShowAction)); OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ShowReinstall)); OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(HasVersions)); OnPropertyChanged(nameof(ShowDowngradeWarning));
    }

    public ComponentViewModel(GameDetailViewModel owner, IComponent component, int step)
    {
        _owner = owner;
        Component = component;
        Step = step;
        Options = new(component.Options.Select(o => new OptionViewModel(o)));
        var ctx = owner.Context!;
        _record = ctx.State.Get(component.Id);
        IsManaged = _record != null;
        // Pre-select the options the component was installed with.
        if (_record != null)
            foreach (var o in Options)
                if (_record.Meta.TryGetValue(o.Option.Key, out var v)) o.Select(v);
        foreach (var o in Options) o.Changed += OnOptionChanged;
        try
        {
            var status = component.Detect(ctx);
            Kind = status.Kind;
            StatusText = status.Detail;
            StatusBrushKey = Kind switch { ComponentStatusKind.Installed => "Accent", ComponentStatusKind.Blocked => "Danger", _ => "Muted" };
            BlockersText = string.Join("\n", component.Blockers(ctx));
            // Hashes files, so computed once here (same place and thread as Blockers).
            if (IsManaged && component is ComponentBase cb) IsTakenOver = cb.TakenOverBy(ctx) != null;
            if (!IsManaged && owner.LegacyComponents.Contains(component.Id))
            {
                IsLegacy = true;
                StatusText = Loc.T("comp.st.legacy");
                BlockersText = Loc.T("comp.blocker.legacy");
            }
            else if (!IsManaged && owner.ForeignComponents.Contains(component.Id))
            {
                var source = owner.Foreign.First(x => x.Install.ComponentId == component.Id).Install.Source;
                StatusText = Loc.F("comp.st.foreign", source);
                BlockersText = "";
            }
        }
        catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException)
        {
            Kind = ComponentStatusKind.Blocked;
            StatusText = Loc.T("comp.st.unreadable");
            StatusBrushKey = "Danger";
            BlockersText = ex.Message;
        }
        Blockers = BlockersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => NoticeViewModel.FromText(t)).ToList();
        // Managed components, and the game's own DLLs (DLSS, Streamline, NR model) even when untouched.
        if (IsManaged || component.ChecksGameFiles && Kind == ComponentStatusKind.External && !HasBlockers)
        {
            State = UpdateState.Checking;
            _ = CheckForUpdateAsync();
        }
        else if (component.ChecksAvailability && Kind == ComponentStatusKind.NotInstalled && !HasBlockers)
        {
            State = UpdateState.Checking;
            _ = CheckAvailabilityAsync();
        }
        else State = UpdateState.NotInstalled;
    }

    /// <summary>Not installed: is there anything to install for this game? Offline counts as yes.</summary>
    private async Task CheckAvailabilityAsync()
    {
        var ctx = _owner.Context;
        string? reason = null;
        if (ctx != null)
        {
            try { reason = await Task.Run(() => Component.UnavailableReasonAsync(ctx, CancellationToken.None)); }
            catch (Exception ex) when (ex is HdlssException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _owner.Main.Host.Log.Debug($"Disponibilità {Name}: {ex.Message}");
            }
        }
        if (reason == null) { State = UpdateState.NotInstalled; return; }
        UnavailableText = reason;
        StatusText = Loc.T("comp.st.unavailable");
        StatusBrushKey = "Muted";
        State = UpdateState.Unavailable;
    }

    private Dictionary<string, string> SelectedOptions() => Options.ToDictionary(o => o.Option.Key, o => o.Selected.Value);

    private bool OptionsDiffer() =>
        _record != null && Options.Any(o => _record.Meta.TryGetValue(o.Option.Key, out var v) ? v != o.Selected.Value : o.Selected.Value != o.Option.Default);

    private void OnOptionChanged()
    {
        if (!IsManaged)
        {
            if (Component.ChecksGameFiles && Kind == ComponentStatusKind.External) { State = UpdateState.Checking; _ = CheckForUpdateAsync(); }
            return;
        }
        if (OptionsDiffer()) State = UpdateState.OptionsChanged;
        else { State = UpdateState.Checking; _ = CheckForUpdateAsync(); }
    }

    /// <summary>Compares what is on disk with what an install would bring now (no download).</summary>
    private async Task CheckForUpdateAsync()
    {
        var ctx = _owner.Context;
        if (ctx == null) return;
        UpdateCheck? check = null;
        try
        {
            var options = SelectedOptions();
            check = await Task.Run(() => Component.CheckUpdateAsync(ctx, options, CancellationToken.None));
        }
        catch (Exception ex) when (ex is HdlssException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _owner.Main.Host.Log.Debug($"Controllo aggiornamenti {Name}: {ex.Message}");
        }
        if (State == UpdateState.OptionsChanged) return;
        LatestVersion = check?.Latest;
        var source = IsManaged ? "" : Loc.T("comp.st.originals");
        if (check == null)
        {
            if (!IsManaged) { State = UpdateState.NotInstalled; return; }
            State = UpdateState.Unknown;
            StatusText = Loc.F("comp.st.installed", VersionCompare.Display(_record?.Version));
            StatusBrushKey = "Accent";
            return;
        }
        switch (check.Verdict)
        {
            case VersionVerdict.UpToDate:
                State = UpdateState.UpToDate;
                StatusText = Loc.F("comp.st.upToDate", source, check.Present);
                StatusBrushKey = "Accent";
                break;
            case VersionVerdict.Newer:
                IsDowngrade = VersionCompare.IsDowngrade(check.Present, check.Latest);
                VersionsText = $"{VersionCompare.Display(check.Present)} → {check.Latest}";
                State = UpdateState.UpdateAvailable;
                StatusText = Loc.F("comp.st.available", source, check.Present, check.Latest);
                StatusBrushKey = "Warn";
                break;
            default:
                IsDowngrade = VersionCompare.IsDowngrade(check.Present, check.Latest);
                VersionsText = $"{VersionCompare.Display(check.Present)} → {check.Latest}";
                State = UpdateState.UpdateAvailable;
                StatusText = Loc.F("comp.st.unknownVer", check.Present, check.Latest);
                StatusBrushKey = "Warn";
                break;
        }
    }

    private bool CanInstall() => !HasBlockers && !_owner.Main.Busy;
    private bool CanImportNrNow() => !_owner.Main.Busy;
    private bool CanRemove() => IsManaged && !_owner.Main.Busy;

    [RelayCommand]
    private Task ImportExternalAsync() =>
        _owner.Foreign.FirstOrDefault(f => f.Install.ComponentId == Component.Id) is { } item ? _owner.ImportOneAsync(item.Install) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var ctx = _owner.Context!;
        var host = _owner.Main.Host;
        var options = Options.ToDictionary(o => o.Option.Key, o => o.Selected.Value);
        var ok = await _owner.Main.RunAsync(Loc.F("comp.op.prepare", Name), async (progress, cancel) =>
        {
            await _owner.ReleaseStandaloneModelAsync(ctx, Component.Id, progress, cancel);
            var plan = await Task.Run(() => host.Components.PlanInstallAsync(ctx, Component, options, progress, cancel), cancel);
            foreach (var (k, v) in options) plan.Meta[k] = v;
            if (plan.IsEmpty) { Dialogs.Info(Loc.F("comp.dlg.upToDate", Name, string.Join("\n", plan.Notes))); return; }
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
            if (plan.Notes.Count > 0) Dialogs.Info(Loc.F("comp.dlg.installed", Name, string.Join("\n", plan.Notes)));
        });
        if (ok) await _owner.AfterChangeAsync(Component.Id);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task ImportAsync()
    {
        if (Component is not ILocalImport importer) return;
        var file = Dialogs.PickFile(importer.ImportFilter);
        if (file == null) return;
        var ctx = _owner.Context!;
        var host = _owner.Main.Host;
        var ok = await _owner.Main.RunAsync(Loc.F("comp.op.import", Name), async (progress, cancel) =>
        {
            var plan = await Task.Run(() => host.Components.PlanImportAsync(ctx, Component, file, progress, cancel), cancel);
            if (plan.IsEmpty) { Dialogs.Info(Loc.T("comp.dlg.fileInstalled")); return; }
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
        });
        if (ok) await _owner.AfterChangeAsync(Component.Id);
    }

    public const string NrAuthorUrl = "https://buymeacoffee.com/lecram";

    [RelayCommand]
    private static void OpenNrAuthor() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NrAuthorUrl) { UseShellExecute = true });

    [RelayCommand(CanExecute = nameof(CanImportNrNow))]
    private async Task ImportNrAsync()
    {
        var source = Dialogs.PickNrSource();
        if (source == null) return;
        var library = _owner.Main.Host.Services.NrAddons;
        NrAddonPackage? package = null;
        string? error = null;
        var ok = await _owner.Main.RunAsync(Loc.F("comp.op.import", Name), async (progress, cancel) =>
        {
            try { package = await Task.Run(() => library.Import(source), cancel); }
            catch (HdlssException ex) { error = ex.Message; }
        });
        if (!ok) return;
        if (error != null) { Dialogs.Error(error, Loc.T("nr.import.error")); return; }
        if (package != null) Dialogs.Info(Loc.F("nr.import.done", package.Label));
        await _owner.AfterChangeAsync(Component.Id);
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        var ctx = _owner.Context!;
        var host = _owner.Main.Host;
        var ok = await _owner.Main.RunAsync(Loc.F("comp.op.remove", Name), async (progress, cancel) =>
        {
            var plan = await RemovalFlow.PlanAsync(host, ctx, Component.Id, cancel);
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
        });
        if (ok) await _owner.AfterChangeAsync(Component.Id);
    }
}
