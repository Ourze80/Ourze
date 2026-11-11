using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hdlss.App.Views;
using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.Games;
using Hdlss.Core.Legacy;
using Hdlss.Core.Transactions;

namespace Hdlss.App.ViewModels;

public enum PrimaryAction { None, EnableNr, AdoptLegacy, ImportForeign, Verify, ChooseExe }

public enum NrMethod { ReShade, OptiScaler }

public enum GameTab { ReShade, OptiScaler, Dll, Other }

public sealed partial class GameDetailViewModel : ObservableObject
{
    public MainViewModel Main { get; }
    public GameItemViewModel Item { get; }
    public Game Game => Item.Game;
    public GameContext? Context { get; private set; }

    /// <summary>Components of the selected category tab.</summary>
    public ObservableCollection<ComponentViewModel> TabComponents { get; } = [];
    public ObservableCollection<CompatIssue> CompatIssues { get; } = [];
    public ObservableCollection<LegacyItemViewModel> Legacy { get; } = [];
    public ObservableCollection<ForeignItemViewModel> Foreign { get; } = [];
    public ObservableCollection<UnknownInjector> UnknownInjectors { get; } = [];
    public ObservableCollection<NoticeViewModel> Warnings { get; } = [];
    public ObservableCollection<string> Chips { get; } = [];
    public ObservableCollection<string> PlanSteps { get; } = [];
    /// <summary>Component ids that a 1.x install in this folder can be adopted as.</summary>
    public HashSet<string> LegacyComponents { get; } = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _loading = true;
    [ObservableProperty] private string _exeText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private ImageSource? _hero;
    [ObservableProperty] private NrPath _path;
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private NrRecommendation? _recommendation;

    // Plain-language status card
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _explanation = "";
    [ObservableProperty] private string _headlineBrushKey = "Text";
    [ObservableProperty] private PrimaryAction _primary;
    [ObservableProperty] private string _primaryLabel = "";
    [ObservableProperty] private bool _canDisable;
    [ObservableProperty] private string? _verifyTitle;
    [ObservableProperty] private string? _verifyDetails;
    [ObservableProperty] private string _verifyBrushKey = "Muted";

    public GameDetailViewModel(MainViewModel main, GameItemViewModel item)
    {
        Main = main;
        Item = item;
        Hero = LoadHero();
    }

    private ImageSource? LoadHero()
    {
        var covers = Main.Host.Services.Covers;
        var image = Images.Load(covers.CurrentHero(Game) ?? covers.Current(Game), 1600);
        HeroPortrait = image != null && image.Height > image.Width;
        return image;
    }

    [ObservableProperty] private bool _heroPortrait;

    public bool HasCustomCover => Main.Host.Services.Covers.HasCustom(Game);

    public void RefreshHero()
    {
        Hero = LoadHero();
        OnPropertyChanged(nameof(HasCustomCover));
    }

    [RelayCommand]
    private void ChangeCover()
    {
        var file = Dialogs.PickFile(Loc.T("cover.filter"));
        if (file == null) return;
        try
        {
            Main.Host.Services.Covers.SetCustom(Game, file);
            Item.RefreshCover();
            RefreshHero();
        }
        catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException)
        {
            Main.Host.Log.Warn("Copertina personalizzata", ex);
            Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void ClearCover()
    {
        try
        {
            Main.Host.Services.Covers.ClearCustom(Game);
            Item.RefreshCover();
            RefreshHero();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Error(ex.Message);
        }
    }

    private bool _tabInitialized;
    public string Name => Game.Name;
    public bool Advanced => Main.Advanced;
    public bool HasLegacy => Legacy.Count > 0;
    public bool HasForeign => Foreign.Count > 0;
    public bool HasUnknown => UnknownInjectors.Count > 0;
    /// <summary>Component ids found installed by other tools (importable).</summary>
    public HashSet<string> ForeignComponents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasPrimary => Primary != PrimaryAction.None;
    public bool HasVerify => VerifyTitle != null;
    public bool HasPlanSteps => PlanSteps.Count > 0;
    public string PathTitle => NrAdvisor.Title(Path);

    public bool IsNative { get => Path == NrPath.Native; set { if (value) Path = NrPath.Native; } }
    public bool IsFeeder { get => Path == NrPath.Feeder; set { if (value) Path = NrPath.Feeder; } }
    public bool IsOpti { get => Path == NrPath.OptiScaler; set { if (value) Path = NrPath.OptiScaler; } }

    partial void OnPrimaryChanged(PrimaryAction value) => OnPropertyChanged(nameof(HasPrimary));
    partial void OnVerifyTitleChanged(string? value) => OnPropertyChanged(nameof(HasVerify));

    partial void OnPathChanged(NrPath value)
    {
        OnPropertyChanged(nameof(IsNative)); OnPropertyChanged(nameof(IsFeeder)); OnPropertyChanged(nameof(IsOpti)); OnPropertyChanged(nameof(PathTitle));
        RebuildComponentList();
        UpdateStatusCard();
    }

    partial void OnShowAllChanged(bool value) => RebuildComponentList();

    public void AdvancedChanged()
    {
        OnPropertyChanged(nameof(Advanced));
        RebuildComponentList();
    }

    public async Task ReloadAsync()
    {
        Loading = true;
        Error = null;
        Chips.Clear();
        try
        {
            var host = Main.Host;
            Chips.Add(Game.StoreLabel);
            if (Game.ProtectedReason != null)
            {
                ExeText = Game.Root;
                Context = null;
                SummaryComponents = SummaryWarnings = SummaryNr = SummaryNrTip = "";
                TabComponents.Clear(); CompatIssues.Clear(); Legacy.Clear(); PlanSteps.Clear();
                OnPropertyChanged(nameof(HasLegacy)); OnPropertyChanged(nameof(HasPlanSteps));
                SetCard(Loc.T("gd.protected"), Game.ProtectedReason, "Warn", PrimaryAction.None);
                return;
            }
            var (ctx, legacy, recommendation, foreign, unknown) = await Task.Run(() =>
            {
                host.Library.Resolve(Game);
                var c = new GameContext(Game, host.Services, host.Gpu.Value);
                var l = LegacyInstalls.Find(c.Folder, host.Log);
                // 1.x installs are handled by "Porta sulla 2.0"; everything else injected is "foreign".
                var skip = l.Where(x => x.AdoptableAs != null).Select(x => x.AdoptableAs!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var f = ForeignInstalls.Detect(c, skip);
                var u = ForeignInstalls.Unknown(c, f, l.SelectMany(x => x.Files.Select(y => y.Name)));
                return (c, l, NrAdvisor.Recommend(c), f, u);
            });
            Context = ctx;
            Recommendation = recommendation;
            ExeText = Game.ModFolder == null ? Game.Exe : Loc.F("gd.exeText", Game.Exe, Game.Folder);
            Chips.Add(ctx.Api switch { GraphicsApi.DX11 => "DirectX 11", GraphicsApi.DX12 => "DirectX 12", GraphicsApi.Vulkan => "Vulkan", _ => Loc.T("gd.api.unknown") }
                      + (host.Settings.Profile(Game.Id).ApiOverride != null ? Loc.T("gd.api.manual") : ctx.Api != GraphicsApi.Unknown && !ctx.ApiCertain ? Loc.T("gd.api.probable") : ""));
            Chips.Add(recommendation.HasNativeDlss ? Loc.T("gd.dlss.yes") : Loc.T("gd.dlss.no"));
            if (Game.Engine != EngineKind.Unknown) Chips.Add(Game.Engine == EngineKind.Unreal5 ? "Unreal Engine 5" : Game.Engine == EngineKind.Unreal4 ? "Unreal Engine 4" : "Unreal");
            Warnings.Clear();
            foreach (var w in Game.Warnings.Distinct()) Warnings.Add(WarningNotice(w));
            if (ctx.Scan.AntiCheat != null) Warnings.Add(WarningNotice(ctx.Scan.AntiCheat));
            await UpdateSummaryAsync(ctx);

            Legacy.Clear();
            LegacyComponents.Clear();
            foreach (var l in legacy)
            {
                Legacy.Add(new LegacyItemViewModel(this, l));
                if (l.AdoptableAs != null) LegacyComponents.Add(l.AdoptableAs);
            }
            OnPropertyChanged(nameof(HasLegacy));
            Foreign.Clear();
            ForeignComponents.Clear();
            foreach (var f in foreign) { Foreign.Add(new ForeignItemViewModel(this, f)); ForeignComponents.Add(f.ComponentId); }
            UnknownInjectors.Clear();
            foreach (var u in unknown) UnknownInjectors.Add(u);
            OnPropertyChanged(nameof(HasForeign)); OnPropertyChanged(nameof(HasUnknown));

            RefreshMethods();
            var current = NrAdvisor.Installed(ctx) ?? recommendation.Path;
            var method = current == NrPath.OptiScaler ? NrMethod.OptiScaler : NrMethod.ReShade;
            var path = method == NrMethod.OptiScaler ? NrPath.OptiScaler : (NrAdvisor.Installed(ctx) ?? ReShadePath);
            _method = method; OnPropertyChanged(nameof(Method)); OnPropertyChanged(nameof(IsMethodReShade)); OnPropertyChanged(nameof(IsMethodOpti));
            if (!_tabInitialized)
            {
                _tabInitialized = true;
                var tab = method == NrMethod.OptiScaler ? GameTab.OptiScaler : GameTab.ReShade;
                if (Tab != tab) Tab = tab; else OnTabChanged(tab);
            }
            else OnPropertyChanged(nameof(TabDescription));   // language may have changed
            UpdateCompat();
            if (Path != path) Path = path;
            else { RebuildComponentList(); UpdateStatusCard(); }
            Item.Summary = GameSummary.Compute(Game, host.Services);
        }
        catch (HdlssException ex)
        {
            Error = ex.Message;
            TabComponents.Clear();
            SetCard(Loc.T("gd.needInfo"), ex.Message, "Warn", PrimaryAction.ChooseExe, Loc.T("gd.chooseExe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.Host.Log.Error($"Lettura di {Game.Name}", ex);
            Error = ex.Message;
            TabComponents.Clear();
        }
        finally { Loading = false; }
    }

    // ── One-line summary under the title ──
    [ObservableProperty] private string _summaryComponents = "";
    [ObservableProperty] private string _summaryWarnings = "";
    [ObservableProperty] private string _summaryNr = "";
    [ObservableProperty] private string _summaryNrTip = "";
    public bool HasSummaryWarnings => SummaryWarnings.Length > 0;
    public bool HasSummaryNr => SummaryNr.Length > 0;
    partial void OnSummaryWarningsChanged(string value) => OnPropertyChanged(nameof(HasSummaryWarnings));
    partial void OnSummaryNrChanged(string value) => OnPropertyChanged(nameof(HasSummaryNr));

    private static readonly string[] NrCostIds = [RenoDxNrComponent.ComponentId, FeederComponent.ComponentId];

    private async Task UpdateSummaryAsync(GameContext ctx)
    {
        var n = ctx.State.Components.Count;
        SummaryComponents = n == 0 ? Loc.T("gd.sum.none") : n == 1 ? Loc.T("gd.sum.one") : Loc.F("gd.sum.many", n);
        var w = Warnings.Count;
        SummaryWarnings = w == 0 ? "" : w == 1 ? Loc.T("gd.warn.one") : Loc.F("gd.warn.many", w);
        if (!NrCostIds.Any(ctx.Installed)) { SummaryNr = ""; SummaryNrTip = ""; return; }
        var log = System.IO.Path.Combine(ctx.Folder, "ReShade.log");
        var cost = await Task.Run(() => NrCostReader.Read(log));
        var ui = Loc.Instance.Culture;
        if (cost == null)
        {
            SummaryNr = Loc.T("gd.nr.launch");
            SummaryNrTip = "";
        }
        else
        {
            SummaryNr = Loc.F("gd.nr.cost", cost.GpuMs.ToString("0.0", ui))
                        + (cost.HasFps ? $" (≈ {cost.FpsWithout} → {cost.FpsWith} FPS)" : "");
            SummaryNrTip = Loc.T("gd.nr.tip");
        }
    }

    /// <summary>
    /// The DLSS 5 card: what is installed and the single next step. DLSS 5 comes first on the page;
    /// other 1.x / foreign components are reported by the secondary banner.
    /// </summary>
    private void UpdateStatusCard()
    {
        var ctx = Context;
        if (ctx == null) return;
        PlanSteps.Clear();
        var installed = NrAdvisor.Installed(ctx);
        CanDisable = installed != null;
        var nrIds = NrAdvisor.Components.Values.SelectMany(x => x).Append(NrModelComponent.ComponentId).Where(id => id != ReShadeComponent.ComponentId).ToHashSet();
        var legacyNr = Legacy.FirstOrDefault(l => l.CanAdopt && nrIds.Contains(l.Install.AdoptableAs!) && !ctx.Installed(l.Install.AdoptableAs!));
        var foreignNr = Foreign.FirstOrDefault(f => nrIds.Contains(f.Install.ComponentId));

        if (installed != null)
        {
            var how = Loc.T(installed == NrPath.OptiScaler ? "gd.how.opti" : "gd.how.reshade");
            var menu = Loc.T(installed == NrPath.OptiScaler ? "gd.menu.opti" : "gd.menu.reshade");
            SetCard(Loc.F("gd.active.title", how),
                Loc.F("gd.active.text", menu),
                "Accent", PrimaryAction.Verify, Loc.T("gd.worked"));
        }
        else if (legacyNr != null)
        {
            SetCard(Loc.T("gd.legacy.title"),
                Loc.T("gd.legacy.text"),
                "Warn", PrimaryAction.AdoptLegacy, Loc.T("gd.legacy.btn"));
            PlanSteps.Add(legacyNr.Title);
        }
        else if (foreignNr != null)
        {
            SetCard(Loc.F("gd.foreign.title", foreignNr.Install.Source),
                Loc.T("gd.foreign.text"),
                "Warn", PrimaryAction.ImportForeign, Loc.T("comp.importHdlss"));
            PlanSteps.Add(foreignNr.Title);
            // The button imports everything recognised: list it all here instead of a second notice below.
            foreach (var other in Foreign.Where(f => f != foreignNr)) PlanSteps.Add(other.Title);
        }
        else if (!ctx.Gpu.SupportsNr)
        {
            SetCard(Loc.T("gd.noGpu.title"), Loc.T("gd.noGpu.text"), "Warn", PrimaryAction.None);
        }
        else if (ctx.Api == GraphicsApi.Unknown)
        {
            SetCard(Loc.T("gd.noApi.title"),
                Loc.T("gd.noApi.text"),
                "Warn", PrimaryAction.ChooseExe, Loc.T("gd.noApi.btn"));
        }
        else
        {
            SetCard(Loc.T("gd.none.title"), MethodExplanation, "Text", PrimaryAction.EnableNr,
                Loc.T(Method == NrMethod.OptiScaler ? "gd.none.btnOpti" : "gd.none.btnReShade"));
            foreach (var id in NrAdvisor.Components[Path].Where(id => !ctx.Installed(id)))
                PlanSteps.Add(ComponentCatalog.NameOf(id));
            PlanSteps.Add(Loc.F("gd.step.model", ctx.Gpu.Name.Replace("NVIDIA GeForce ", "")));
        }
        OnPropertyChanged(nameof(HasPlanSteps));
        UpdateBanner(legacyNr, foreignNr);
    }

    /// <summary>Secondary notice: other 1.x / foreign components that can be taken over.</summary>
    private void UpdateBanner(LegacyItemViewModel? shownLegacy, ForeignItemViewModel? shownForeign)
    {
        var ctx = Context!;
        var legacy = Legacy.Where(l => (l.IsStale || l.CanAdopt && !ctx.Installed(l.Install.AdoptableAs!)) && l != shownLegacy).ToList();
        var foreign = shownForeign != null ? [] : Foreign.ToList();
        if (legacy.Count > 0)
        {
            BannerText = Loc.F("gd.banner.legacy", legacy.Count);
            BannerAction = PrimaryAction.AdoptLegacy;
            BannerLabel = Loc.T("gd.legacy.btn");
        }
        else if (foreign.Count > 0)
        {
            BannerText = Loc.F("gd.banner.foreign", foreign.Count, string.Join(", ", foreign.Select(f => f.Install.ComponentName)));
            BannerAction = PrimaryAction.ImportForeign;
            BannerLabel = Loc.T("comp.importHdlss");
        }
        else BannerAction = PrimaryAction.None;
        OnPropertyChanged(nameof(HasBanner));
    }

    [ObservableProperty] private string _bannerText = "";
    [ObservableProperty] private string _bannerLabel = "";
    [ObservableProperty] private PrimaryAction _bannerAction;
    public bool HasBanner => BannerAction != PrimaryAction.None;

    [RelayCommand]
    private async Task RunBannerAsync()
    {
        if (BannerAction == PrimaryAction.AdoptLegacy) await AdoptAllAsync();
        else if (BannerAction == PrimaryAction.ImportForeign) await ImportAllAsync();
    }

    // ── DLSS 5 method: ReShade (RenoDX NR, or Feeder when the game has no DLSS / no DX12) or OptiScaler ──
    [ObservableProperty] private NrMethod _method;
    public bool IsMethodReShade { get => Method == NrMethod.ReShade; set { if (value) Method = NrMethod.ReShade; } }
    public bool IsMethodOpti { get => Method == NrMethod.OptiScaler; set { if (value) Method = NrMethod.OptiScaler; } }
    public bool ReShadeRecommended => Recommendation?.Path is NrPath.Native or NrPath.Feeder;
    public bool OptiRecommended => Recommendation?.Path == NrPath.OptiScaler;
    public bool MethodsEnabled => Context != null && NrAdvisor.Installed(Context) == null;

    /// <summary>The ReShade variant that fits this game.</summary>
    private bool HasNativeDx12Dlss => Context?.Api == GraphicsApi.DX12 && Recommendation?.HasNativeDlss == true;
    /// <summary>RenoDX NR without native DLSS: only with an imported DLSS5 NR 12 (the advisor then recommends Native).</summary>
    private bool ViaLecram12 => !HasNativeDx12Dlss && Recommendation?.Path == NrPath.Native;
    private NrPath ReShadePath => Context == null || HasNativeDx12Dlss || ViaLecram12 ? NrPath.Native : NrPath.Feeder;

    public string ReShadeDescription => ReShadePath == NrPath.Native
        ? Loc.T(ViaLecram12 ? "gd.desc.nr12" : "gd.desc.native")
        : Loc.T("gd.desc.feeder");
    public string OptiDescription => Loc.T("gd.desc.opti");

    private string MethodExplanation => Method == NrMethod.OptiScaler
        ? Loc.T("gd.expl.opti")
        : ReShadePath == NrPath.Native
            ? Loc.T(ViaLecram12 ? "gd.expl.nr12" : "gd.expl.native")
            : Loc.T("gd.expl.feeder");

    partial void OnMethodChanged(NrMethod value)
    {
        OnPropertyChanged(nameof(IsMethodReShade)); OnPropertyChanged(nameof(IsMethodOpti));
        var path = value == NrMethod.OptiScaler ? NrPath.OptiScaler : ReShadePath;
        if (Path != path) Path = path; else UpdateStatusCard();
    }

    private void RefreshMethods()
    {
        OnPropertyChanged(nameof(ReShadeRecommended)); OnPropertyChanged(nameof(OptiRecommended));
        OnPropertyChanged(nameof(ReShadeDescription)); OnPropertyChanged(nameof(MethodsEnabled));
    }
    private void SetCard(string headline, string explanation, string brush, PrimaryAction action, string label = "")
    {
        Headline = headline;
        Explanation = explanation;
        HeadlineBrushKey = brush;
        PrimaryLabel = label;
        Primary = action;
    }

    // ── Category tabs: what belongs to what ──
    private static readonly Dictionary<GameTab, string[]> TabIds = new()
    {
        [GameTab.ReShade] = [ReShadeComponent.ComponentId, RenoDxNrComponent.ComponentId, FeederComponent.ComponentId,
                             RenoDxHdrComponent.ComponentId, MfgUnlockComponent.ComponentId, "fx-lilium", "fx-cas"],
        [GameTab.OptiScaler] = [OptiScalerComponent.ComponentId],
        // The NR model is a DLL shared by both ways of running DLSS 5 (ReShade add-ons and OptiScaler F5).
        [GameTab.Dll] = [NrModelComponent.ComponentId, DlssUpdateComponent.ComponentId, StreamlineComponent.ComponentId],
        [GameTab.Other] = [UnrealHdrComponent.ComponentId, Ue4ssComponent.ComponentId, AsiLoaderComponent.ComponentId, ReFrameworkComponent.ComponentId],
    };

    [ObservableProperty] private GameTab _tab;
    public bool IsTabReShade { get => Tab == GameTab.ReShade; set { if (value) Tab = GameTab.ReShade; } }
    public bool IsTabOpti { get => Tab == GameTab.OptiScaler; set { if (value) Tab = GameTab.OptiScaler; } }
    public bool IsTabDll { get => Tab == GameTab.Dll; set { if (value) Tab = GameTab.Dll; } }
    public bool IsTabOther { get => Tab == GameTab.Other; set { if (value) Tab = GameTab.Other; } }
    public string TabReShadeLabel => TabLabel("ReShade", GameTab.ReShade);
    public string TabOptiLabel => TabLabel("OptiScaler", GameTab.OptiScaler);
    public string TabDllLabel => TabLabel("DLL", GameTab.Dll);
    public string TabOtherLabel => TabLabel(Loc.T("tab.other"), GameTab.Other);
    public bool TabEmpty => TabComponents.Count == 0;

    private string TabLabel(string name, GameTab tab)
    {
        var n = Context == null ? 0 : TabIds[tab].Count(Context.Installed);
        return n > 0 ? $"{name} · {n}" : name;
    }

    public string TabDescription => Tab switch
    {
        GameTab.ReShade => Loc.T("gd.tab.reshade"),
        GameTab.OptiScaler => Loc.T("gd.tab.opti"),
        GameTab.Dll => Loc.T("gd.tab.dll"),
        _ => Loc.T("gd.tab.other"),
    };

    partial void OnTabChanged(GameTab value)
    {
        OnPropertyChanged(nameof(IsTabReShade)); OnPropertyChanged(nameof(IsTabOpti));
        OnPropertyChanged(nameof(IsTabDll)); OnPropertyChanged(nameof(IsTabOther)); OnPropertyChanged(nameof(TabDescription));
        RebuildComponentList();
    }

    private void RebuildComponentList()
    {
        if (Context == null) return;
        var ctx = Context;
        TabComponents.Clear();
        foreach (var c in TabIds[Tab].Select(ComponentCatalog.Get)
                     .Where(c => ShowAll || Advanced || c.IsRelevant(ctx) || ctx.Installed(c.Id) || ForeignComponents.Contains(c.Id) || LegacyComponents.Contains(c.Id)))
            TabComponents.Add(new ComponentViewModel(this, c, 0));
        foreach (var n in new[] { nameof(TabReShadeLabel), nameof(TabOptiLabel), nameof(TabDllLabel), nameof(TabOtherLabel), nameof(TabEmpty) })
            OnPropertyChanged(n);
    }

    // ── ReShade ↔ OptiScaler compatibility ──
    public bool HasCompat => CompatIssues.Count > 0;
    public bool CanFixCompat => CompatIssues.Any(i => i.Fixable);

    private void UpdateCompat()
    {
        CompatIssues.Clear();
        if (Context != null)
            try { foreach (var i in Compatibility.Check(Context)) CompatIssues.Add(i); }
            catch (Exception ex) when (ex is HdlssException or IOException or UnauthorizedAccessException) { Main.Host.Log.Debug($"Compatibilità: {ex.Message}"); }
        OnPropertyChanged(nameof(HasCompat)); OnPropertyChanged(nameof(CanFixCompat));
    }

    [RelayCommand]
    private async Task FixCompatAsync()
    {
        await FixCompatibilityAsync(ask: true);
        await ReloadAsync(); // banner and library tile follow the new state
    }

    /// <summary>
    /// Puts ReShade on the loader that fits (ReShade64.dll via OptiScaler, or back to dxgi.dll).
    /// Runs on its own after OptiScaler/ReShade change so the two never hook the game twice.
    /// </summary>
    internal async Task FixCompatibilityAsync(bool ask)
    {
        var ctx = Context;
        if (ctx == null) return;
        bool nrFix = Compatibility.Check(ctx).Any(i => i.Fixable && i.Kind == CompatKind.NrAddons);
        await Main.RunAsync(Loc.T(nrFix ? "gd.op.nrCleanup" : "gd.op.compat"), async (progress, cancel) =>
        {
            ctx.Refresh();
            // A Feeder HDLSS5 installed next to DLSS5 NR 12 goes through the card's own removal (preview included).
            if (NrConflicts.NeedsFeederRemoval(ctx))
            {
                var removal = await RemovalFlow.PlanAsync(Main.Host, ctx, FeederComponent.ComponentId, cancel);
                if (!removal.IsEmpty)
                {
                    if (!Dialogs.ConfirmPlan(removal)) throw new OperationCanceledException();
                    await Task.Run(() => Main.Host.Components.Apply(ctx, removal, progress), cancel);
                }
                ctx.Refresh();
            }
            // Loader moves and NR add-on cleanup are separate plans: repeat until nothing is left (bounded).
            for (int round = 0; round < 3; round++)
            {
                var plan = await Task.Run(() => Compatibility.PlanFix(ctx), cancel);
                if (plan == null) return;
                if ((ask || Advanced) && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
                await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
            }
        });
    }

    /// <summary>
    /// RenoDX NR / Feeder include the NR model: a standalone "Modello NR" is removed first so the
    /// install can take the file over (no need to go to the OptiScaler tab).
    /// </summary>
    internal async Task ReleaseStandaloneModelAsync(GameContext ctx, string componentId, IProgress<Hdlss.Core.Diagnostics.OperationProgress> progress, CancellationToken cancel)
    {
        var release = NrRules.ModelToRelease(ctx, componentId);
        if (release == null) return;
        var plan = await RemovalFlow.PlanAsync(Main.Host, ctx, release, cancel);
        plan.Notes.Insert(0, Loc.F("gd.releaseNote", ComponentCatalog.NameOf(release), ComponentCatalog.NameOf(componentId)));
        if (!plan.IsEmpty)
        {
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
        }
        ctx.Refresh();
    }

    /// <summary>Called after any component change: keeps ReShade and OptiScaler compatible, then reloads.</summary>
    internal async Task AfterChangeAsync(string componentId)
    {
        if (componentId is OptiScalerComponent.ComponentId or ReShadeComponent.ComponentId or RenoDxNrComponent.ComponentId)
        {
            Context?.Refresh();
            if (Context != null && Compatibility.Check(Context).Any(i => i.Fixable)) await FixCompatibilityAsync(ask: false);
        }
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RunPrimaryAsync()
    {
        switch (Primary)
        {
            case PrimaryAction.EnableNr: await EnableNrAsync(); break;
            case PrimaryAction.AdoptLegacy: await AdoptAllAsync(); break;
            case PrimaryAction.ImportForeign: await ImportAllAsync(); break;
            case PrimaryAction.Verify: Verify(); break;
            case PrimaryAction.ChooseExe: await EditExeAsync(); break;
        }
    }

    /// <summary>Installs every missing component of the path in order, with one confirmation.</summary>
    private async Task EnableNrAsync()
    {
        var ctx = Context;
        if (ctx == null) return;
        var path = Path;
        var missing = NrAdvisor.Components[path].Where(id => !ctx.Installed(id)).Select(ComponentCatalog.Get).ToList();
        if (missing.Count == 0) return;
        if (!Advanced && !Dialogs.Confirm(
                Loc.F("gd.enable.confirm", Game.Name, Path == NrPath.OptiScaler ? "OptiScaler" : "ReShade", string.Join("\n• ", PlanSteps))))
            return;
        var ok = await Main.RunAsync(Loc.T("gd.enable.op"), async (progress, cancel) =>
        {
            foreach (var component in missing)
            {
                ctx.Refresh();
                await ReleaseStandaloneModelAsync(ctx, component.Id, progress, cancel);
                var plan = await Task.Run(() => Main.Host.Components.PlanInstallAsync(ctx, component, new Dictionary<string, string>(), progress, cancel), cancel);
                if (plan.IsEmpty) continue;
                if (Advanced && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
                await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
            }
        });
        await AfterChangeAsync(path == NrPath.OptiScaler ? OptiScalerComponent.ComponentId : ReShadeComponent.ComponentId);
        if (ok && path == NrPath.OptiScaler) Dialogs.Info(Loc.T("gd.enable.doneOpti"));
        else if (ok) Dialogs.Info(Loc.T("gd.enable.doneReShade"));
    }

    private async Task AdoptAllAsync()
    {
        var ctx = Context;
        if (ctx == null) return;
        var host = Main.Host;
        var data = LegacyInstalls.FindDataFolder(host.Settings.LegacyAppDir, host.Paths.AppDir);
        var items = Legacy.Where(l => l.IsStale || l.CanAdopt && !ctx.Installed(l.Install.AdoptableAs!)).ToList();
        if (data == null && items.Any(l => l.Install.Files.Any(f => f.Before != null)))
        {
            Dialogs.Error(Loc.T("gd.adopt.needBackups"));
            return;
        }
        if (!Dialogs.Confirm(Loc.F("gd.adopt.confirm", items.Count, string.Join("\n• ", items.Select(i => i.Title))))) return;
        var failed = new List<string>();
        await Main.RunAsync(Loc.T("gd.adopt.op"), async (progress, cancel) =>
        {
            foreach (var item in items)
            {
                try
                {
                    ctx.Refresh();
                    var plan = item.IsStale
                        ? LegacyInstalls.PlanForget(item.Install, ctx.State, ctx.StateStamp, ctx.Game.Name)
                        : await Task.Run(() =>
                        {
                            var component = ComponentCatalog.Get(item.Install.AdoptableAs!);
                            return LegacyInstalls.PlanAdopt(item.Install, component.Id, component.Name, data ?? "",
                                host.Services.Store, ctx.State, ctx.StateStamp, ctx.Game.Name);
                        }, cancel);
                    await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
                }
                catch (HdlssException ex) { failed.Add($"{item.Title}: {ex.Message}"); }
            }
        });
        await ReloadAsync();
        Dialogs.Info(failed.Count == 0 ? Loc.T("gd.adopt.ok")
            : Loc.F("gd.adopt.partial", failed.Count, string.Join("\n", failed)));
    }

    /// <summary>Imports every recognised foreign install, one confirmation.</summary>
    private async Task ImportAllAsync()
    {
        var ctx = Context;
        if (ctx == null || Foreign.Count == 0) return;
        var items = Foreign.ToList();
        if (!Dialogs.Confirm(Loc.F("gd.import.confirm", string.Join("\n• ", items.Select(i => i.Title))))) return;
        var failed = new List<string>();
        await Main.RunAsync(Loc.T("gd.import.op"), async (progress, cancel) =>
        {
            foreach (var item in items)
            {
                try
                {
                    ctx.Refresh();
                    var plan = await Task.Run(() => { ComponentService.CheckGlobal(ctx); return ForeignInstalls.PlanImport(ctx, item.Install); }, cancel);
                    if (Advanced && !Dialogs.ConfirmPlan(plan)) continue;
                    await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
                }
                catch (HdlssException ex) { failed.Add($"{item.Title}: {ex.Message}"); }
            }
        });
        await ReloadAsync();
        Dialogs.Info(failed.Count == 0 ? Loc.T("gd.import.ok") : Loc.F("gd.import.partial", failed.Count, string.Join("\n", failed)));
    }

    internal async Task ImportOneAsync(ForeignInstall install)
    {
        var ctx = Context;
        if (ctx == null) return;
        var ok = await Main.RunAsync(Loc.F("gd.import.one", install.ComponentName), async (progress, cancel) =>
        {
            var plan = await Task.Run(() => { ComponentService.CheckGlobal(ctx); return ForeignInstalls.PlanImport(ctx, install); }, cancel);
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
        });
        if (ok) await ReloadAsync();
    }

    [RelayCommand]
    private void Verify()
    {
        if (Context == null) return;
        try
        {
            Context.Refresh();
            var result = Verifier.Check(Context);
            VerifyTitle = result.Title;
            VerifyDetails = string.Join("\n", result.Details);
            VerifyBrushKey = result.Level switch { VerifyLevel.Working => "Accent", VerifyLevel.Failed => "Danger", _ => "Warn" };
            Main.Host.Log.Info($"Verifica {Game.Name}: {result.Title}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HdlssException)
        {
            VerifyTitle = Loc.T("gd.verify.fail");
            VerifyDetails = ex.Message;
            VerifyBrushKey = "Warn";
        }
    }

    /// <summary>Removes the NR component only; ReShade and other add-ons stay.</summary>
    [RelayCommand]
    private async Task DisableAsync()
    {
        var ctx = Context;
        var installed = ctx == null ? null : NrAdvisor.Installed(ctx);
        if (ctx == null || installed == null) return;
        var id = NrAdvisor.Components[installed.Value].Last();
        if (!Advanced && !Dialogs.Confirm(Loc.F("gd.disable.confirm", Game.Name))) return;
        await Main.RunAsync(Loc.T("gd.disable.op"), async (progress, cancel) =>
        {
            var plan = await RemovalFlow.PlanAsync(Main.Host, ctx, id, cancel);
            if (Advanced && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
        });
        VerifyTitle = null;
        await AfterChangeAsync(id);
    }

    /// <summary>A game warning, with a button to the «Eseguibile» dialog when that is where it is solved.</summary>
    private NoticeViewModel WarningNotice(string text) =>
        text.Contains("«Eseguibile", StringComparison.Ordinal) || text.Contains("«Executable & API", StringComparison.Ordinal)
            ? NoticeViewModel.FromText(text, Loc.T("notice.exe"), EditExeCommand)
            : NoticeViewModel.FromText(text);

    [RelayCommand]
    private async Task EditExeAsync()
    {
        if (Dialogs.EditExecutable(Main.Host, Game))
        {
            Game.Exe = "";
            Game.Warnings.Clear();
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void Launch()
    {
        try
        {
            if (Context != null) GameLauncher.Launch(Context);
            else GameLauncher.Launch(Game, Main.Host.Settings);
            Main.Status = Loc.F("gd.launching", Game.Name);
        }
        catch (HdlssException ex) { Dialogs.Error(ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Context?.Folder ?? Game.Root;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task HistoryAsync()
    {
        if (Context != null && Dialogs.History(Main.Host, Context)) await ReloadAsync();
    }
}

public sealed partial class ForeignItemViewModel(GameDetailViewModel owner, ForeignInstall install) : ObservableObject
{
    public ForeignInstall Install { get; } = install;
    public string Title => Loc.F("gd.foreign.item", Install.ComponentName, Install.Source);
    public string Detail => string.Join(", ", Install.Files.Select(f => f.Path).Take(4)) + (Install.Files.Count > 4 ? $" (+{Install.Files.Count - 4})" : "")
                            + (Install.WithBackup > 0 ? Loc.F("gd.foreign.saved", Install.WithBackup) : "");

    [RelayCommand] private Task ImportAsync() => owner.ImportOneAsync(Install);
}

public sealed partial class LegacyItemViewModel(GameDetailViewModel owner, LegacyInstall install) : ObservableObject
{
    public LegacyInstall Install { get; } = install;
    public string Title => Install.Title;
    public string Detail => Loc.F(Install.Files.Count == 1 ? "gd.legacy.file1" : "gd.legacy.files", Install.Files.Count, Install.Marker) + (IsStale ? Loc.T("gd.legacy.stale") : "");
    public bool IsStale { get; } = LegacyInstalls.IsStale(install);
    public bool CanAdopt => Install.AdoptableAs != null && !IsStale;

    [RelayCommand]
    private async Task AdoptAsync()
    {
        var host = owner.Main.Host;
        var ctx = owner.Context;
        if (ctx == null || Install.AdoptableAs == null) return;
        var data = LegacyInstalls.FindDataFolder(host.Settings.LegacyAppDir, host.Paths.AppDir);
        if (data == null && Install.Files.Any(f => f.Before != null))
        {
            Dialogs.Error(Loc.T("gd.legacy.noData"));
            return;
        }
        var component = ComponentCatalog.Get(Install.AdoptableAs);
        var ok = await owner.Main.RunAsync(Loc.T("gd.legacy.opAdopt"), async (progress, cancel) =>
        {
            var plan = await Task.Run(() =>
            {
                ComponentService.CheckGlobal(ctx);
                return LegacyInstalls.PlanAdopt(Install, component.Id, component.Name, data ?? "", host.Services.Store, ctx.State, ctx.StateStamp, ctx.Game.Name);
            }, cancel);
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
        });
        if (ok) await owner.ReloadAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync() => await RunAsync(false);

    [RelayCommand]
    private async Task RestoreForceAsync() => await RunAsync(true);

    private async Task RunAsync(bool force)
    {
        var host = owner.Main.Host;
        var ctx = owner.Context;
        if (ctx == null) return;
        var data = LegacyInstalls.FindDataFolder(host.Settings.LegacyAppDir, host.Paths.AppDir);
        if (data == null)
        {
            Dialogs.Error(Loc.T("gd.legacy.noData2"));
            return;
        }
        var ok = await owner.Main.RunAsync(Loc.T("gd.legacy.opRestore"), async (progress, cancel) =>
        {
            var plan = await Task.Run(() =>
            {
                ComponentService.CheckGlobal(ctx);
                return LegacyInstalls.PlanRestore(Install, data, host.Services.Store, ctx.State, ctx.StateStamp, ctx.Game.Name, force);
            }, cancel);
            if (!Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => host.Components.Apply(ctx, plan, progress), cancel);
        });
        if (ok) await owner.ReloadAsync();
    }
}

