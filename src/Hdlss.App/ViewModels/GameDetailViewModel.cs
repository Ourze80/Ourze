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
    public ObservableCollection<string> Warnings { get; } = [];
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
        Hero = Images.Load(Game.Hero ?? Game.Cover, 1600);
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
                TabComponents.Clear(); CompatIssues.Clear(); Legacy.Clear(); PlanSteps.Clear();
                OnPropertyChanged(nameof(HasLegacy)); OnPropertyChanged(nameof(HasPlanSteps));
                SetCard("Cartella protetta di Windows", Game.ProtectedReason, "Warn", PrimaryAction.None);
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
            ExeText = Game.ModFolder == null ? Game.Exe : $"Gioco: {Game.Exe}\nMod: {Game.Folder}";
            Chips.Add(ctx.Api switch { GraphicsApi.DX11 => "DirectX 11", GraphicsApi.DX12 => "DirectX 12", GraphicsApi.Vulkan => "Vulkan", _ => "API sconosciuta" }
                      + (host.Settings.Profile(Game.Id).ApiOverride != null ? " (manuale)" : ctx.Api != GraphicsApi.Unknown && !ctx.ApiCertain ? " (probabile)" : ""));
            Chips.Add(recommendation.HasNativeDlss ? "DLSS presente" : "senza DLSS");
            if (Game.Engine != EngineKind.Unknown) Chips.Add(Game.Engine == EngineKind.Unreal5 ? "Unreal Engine 5" : Game.Engine == EngineKind.Unreal4 ? "Unreal Engine 4" : "Unreal");
            Warnings.Clear();
            foreach (var w in Game.Warnings.Distinct()) Warnings.Add(w);
            if (ctx.Scan.AntiCheat != null) Warnings.Add(ctx.Scan.AntiCheat);

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
            UpdateCompat();
            if (Path != path) Path = path;
            else { RebuildComponentList(); UpdateStatusCard(); }
            Item.Summary = GameSummary.Compute(Game, host.Services);
        }
        catch (HdlssException ex)
        {
            Error = ex.Message;
            TabComponents.Clear();
            SetCard("Serve un'informazione", ex.Message, "Warn", PrimaryAction.ChooseExe, "Scegli l'eseguibile…");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Main.Host.Log.Error($"Lettura di {Game.Name}", ex);
            Error = ex.Message;
            TabComponents.Clear();
        }
        finally { Loading = false; }
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
            var how = installed == NrPath.OptiScaler ? "tramite OptiScaler" : "tramite ReShade";
            var menu = installed == NrPath.OptiScaler ? "premi Insert per aprire OptiScaler" : "premi Home per aprire ReShade";
            SetCard($"DLSS 5 attivo {how}",
                $"Avvia il gioco, {menu} e controlla che Neural Rendering sia acceso. Dopo qualche minuto di gioco premi «Ha funzionato?».",
                "Accent", PrimaryAction.Verify, "Ha funzionato?");
        }
        else if (legacyNr != null)
        {
            SetCard("DLSS 5 installato con HDLSS5 1.x",
                "Funziona già, ma lo gestisce la versione precedente. Portalo sulla 2.0 per verificarlo, aggiornarlo e toglierlo da qui: i file del gioco non cambiano.",
                "Warn", PrimaryAction.AdoptLegacy, "Porta sulla 2.0");
            PlanSteps.Add(legacyNr.Title);
        }
        else if (foreignNr != null)
        {
            SetCard($"DLSS 5 installato da {foreignNr.Install.Source}",
                "Per gestirlo da qui (verifica, aggiornamenti, rimozione sicura) importalo in HDLSS5: i file del gioco non cambiano.",
                "Warn", PrimaryAction.ImportForeign, "Importa in HDLSS5");
            PlanSteps.Add(foreignNr.Title);
        }
        else if (!ctx.Gpu.SupportsNr)
        {
            SetCard("DLSS 5 non disponibile su questo PC", "Serve una scheda NVIDIA RTX 4000 o 5000.", "Warn", PrimaryAction.None);
        }
        else if (ctx.Api == GraphicsApi.Unknown)
        {
            SetCard("Prima scegli la grafica del gioco",
                "Non riesco a capire se il gioco usa DirectX 11, DirectX 12 o Vulkan: indicalo e poi potrai installare DLSS 5.",
                "Warn", PrimaryAction.ChooseExe, "Imposta la grafica…");
        }
        else
        {
            SetCard("DLSS 5 non ancora installato", MethodExplanation, "Text", PrimaryAction.EnableNr,
                Method == NrMethod.OptiScaler ? "Installa DLSS 5 con OptiScaler" : "Installa DLSS 5 con ReShade");
            foreach (var id in NrAdvisor.Components[Path].Where(id => !ctx.Installed(id)))
                PlanSteps.Add(ComponentCatalog.NameOf(id));
            PlanSteps.Add($"Modello DLSS 5 per {ctx.Gpu.Name.Replace("NVIDIA GeForce ", "")}");
        }
        OnPropertyChanged(nameof(HasPlanSteps));
        UpdateBanner(legacyNr, foreignNr);
    }

    /// <summary>Secondary notice: other 1.x / foreign components that can be taken over.</summary>
    private void UpdateBanner(LegacyItemViewModel? shownLegacy, ForeignItemViewModel? shownForeign)
    {
        var ctx = Context!;
        var legacy = Legacy.Where(l => (l.IsStale || l.CanAdopt && !ctx.Installed(l.Install.AdoptableAs!)) && l != shownLegacy).ToList();
        var foreign = Foreign.Where(f => f != shownForeign).ToList();
        if (legacy.Count > 0)
        {
            BannerText = $"Altri {legacy.Count} componenti installati con HDLSS5 1.x (ReShade, HDR, loader…). Portali sulla 2.0 per gestirli da qui.";
            BannerAction = PrimaryAction.AdoptLegacy;
            BannerLabel = "Porta sulla 2.0";
        }
        else if (foreign.Count > 0)
        {
            BannerText = $"Altri {foreign.Count} componenti installati da un altro programma ({string.Join(", ", foreign.Select(f => f.Install.ComponentName))}). Importali per gestirli da qui.";
            BannerAction = PrimaryAction.ImportForeign;
            BannerLabel = "Importa in HDLSS5";
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
    private NrPath ReShadePath => Context == null ? NrPath.Native
        : Context.Api == GraphicsApi.DX12 && Recommendation?.HasNativeDlss == true ? NrPath.Native : NrPath.Feeder;

    public string ReShadeDescription => ReShadePath == NrPath.Native
        ? "Add-on RenoDX NR per ReShade, usa il DLSS del gioco. Si accende dal menu ReShade (tasto Home)."
        : "Add-on per ReShade con Feeder: per giochi senza DLSS o non DirectX 12. Sperimentale. Si accende dal menu ReShade (tasto Home).";
    public string OptiDescription => "OptiScaler sostituisce l'upscaler del gioco e applica DLSS 5 (build F5). Si regola dal menu OptiScaler (tasto Insert). Per DirectX 11/12.";

    private string MethodExplanation => Method == NrMethod.OptiScaler
        ? "Metodo scelto: OptiScaler. Dopo l'installazione attiva DLSS nelle opzioni grafiche del gioco e apri il menu OptiScaler (Insert)."
        : ReShadePath == NrPath.Native
            ? "Metodo scelto: ReShade con RenoDX NR. Il gioco ha già DLSS: è il metodo più stabile. Dopo l'installazione apri ReShade (Home) e accendi Neural Rendering."
            : "Metodo scelto: ReShade con Feeder, perché il gioco non ha DLSS o non usa DirectX 12. È sperimentale: possono comparire scie leggere.";

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
    public string TabOtherLabel => TabLabel("Altro", GameTab.Other);
    public bool TabEmpty => TabComponents.Count == 0;

    private string TabLabel(string name, GameTab tab)
    {
        var n = Context == null ? 0 : TabIds[tab].Count(Context.Installed);
        return n > 0 ? $"{name} · {n}" : name;
    }

    public string TabDescription => Tab switch
    {
        GameTab.ReShade => "ReShade è il caricatore degli add-on. Qui c'è tutto ciò che passa da ReShade: DLSS 5 (RenoDX NR o Feeder), HDR, generazione fotogrammi e shader.",
        GameTab.OptiScaler => "OptiScaler sostituisce l'upscaler del gioco. Build F5 = DLSS 5 dentro OptiScaler (usa il modello NR della scheda DLL); build Standard = solo upscaling. Se usi anche ReShade (HDR, add-on), HDLSS5 lo fa caricare da OptiScaler: niente doppio aggancio.",
        GameTab.Dll => "Le DLL NVIDIA: il modello DLSS 5 (nvngx_dlssnr.dll, usato sia da ReShade sia da OptiScaler), DLSS e Streamline del gioco. Puoi aggiornarle o tornare alle originali. Per tutti i giochi insieme usa la pagina DLL a sinistra.",
        _ => "Strumenti per motori specifici: HDR per Unreal, UE4SS, ASI Loader, REFramework.",
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
    private Task FixCompatAsync() => FixCompatibilityAsync(ask: true);

    /// <summary>
    /// Puts ReShade on the loader that fits (ReShade64.dll via OptiScaler, or back to dxgi.dll).
    /// Runs on its own after OptiScaler/ReShade change so the two never hook the game twice.
    /// </summary>
    internal async Task FixCompatibilityAsync(bool ask)
    {
        var ctx = Context;
        if (ctx == null) return;
        await Main.RunAsync("Compatibilità ReShade · OptiScaler", async (progress, cancel) =>
        {
            ctx.Refresh();
            var plan = await Task.Run(() => Compatibility.PlanFix(ctx), cancel);
            if (plan == null) return;
            if ((ask || Advanced) && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
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
        var plan = await Task.Run(() => Main.Host.Components.PlanRemoval(ctx, release), cancel);
        plan.Notes.Insert(0, $"Il «{ComponentCatalog.NameOf(release)}» installato a parte viene tolto: {ComponentCatalog.NameOf(componentId)} include già il modello e lo reinstalla subito dopo.");
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
        if (componentId is OptiScalerComponent.ComponentId or ReShadeComponent.ComponentId)
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
                $"Installo DLSS 5 su {Game.Name} tramite {(Path == NrPath.OptiScaler ? "OptiScaler" : "ReShade")}.\n\nVerrà installato:\n• {string.Join("\n• ", PlanSteps)}\n\n" +
                "Ogni file sostituito viene salvato prima e puoi annullare tutto con «Disattiva» o dalla cronologia. Chiudi il gioco prima di continuare."))
            return;
        var ok = await Main.RunAsync("Attivazione del Neural Rendering", async (progress, cancel) =>
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
        if (ok && path == NrPath.OptiScaler) Dialogs.Info("Fatto. Avvia il gioco, attiva DLSS nelle opzioni grafiche e premi Insert per aprire OptiScaler: controlla che Neural Rendering sia acceso.\n\nDopo qualche minuto di gioco torna qui e premi «Ha funzionato?».");
        else if (ok) Dialogs.Info("Fatto. Avvia il gioco, premi Home per aprire ReShade e verifica che Neural Rendering sia acceso.\n\nDopo qualche minuto di gioco torna qui e premi «Ha funzionato?».");
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
            Dialogs.Error("Per portare i componenti sulla 2.0 servono i backup della 1.x.\n\nApri Impostazioni e indica la cartella di HDLSS5 1.x (quella che contiene «data»).");
            return;
        }
        if (!Dialogs.Confirm($"Porto sulla 2.0 {items.Count} componenti installati con la 1.x:\n• {string.Join("\n• ", items.Select(i => i.Title))}\n\nI file del gioco non cambiano: la 2.0 li prende solo in gestione.")) return;
        var failed = new List<string>();
        await Main.RunAsync("Passaggio alla 2.0", async (progress, cancel) =>
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
        Dialogs.Info(failed.Count == 0 ? "Fatto: tutti i componenti ora sono gestiti dalla 2.0."
            : $"Completato con {failed.Count} eccezioni (restano gestiti dalla 1.x):\n\n{string.Join("\n", failed)}");
    }

    /// <summary>Imports every recognised foreign install, one confirmation.</summary>
    private async Task ImportAllAsync()
    {
        var ctx = Context;
        if (ctx == null || Foreign.Count == 0) return;
        var items = Foreign.ToList();
        if (!Dialogs.Confirm($"Importo in HDLSS5:\n• {string.Join("\n• ", items.Select(i => i.Title))}\n\nI file del gioco non cambiano: HDLSS5 li prende solo in gestione. " +
                             "Se il programma originale ha lasciato un backup, «Rimuovi» rimetterà quello.")) return;
        var failed = new List<string>();
        await Main.RunAsync("Importazione in HDLSS5", async (progress, cancel) =>
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
        Dialogs.Info(failed.Count == 0 ? "Fatto: i componenti ora sono gestiti da HDLSS5." : $"Completato con {failed.Count} eccezioni:\n\n{string.Join("\n", failed)}");
    }

    internal async Task ImportOneAsync(ForeignInstall install)
    {
        var ctx = Context;
        if (ctx == null) return;
        var ok = await Main.RunAsync($"Importazione · {install.ComponentName}", async (progress, cancel) =>
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
            VerifyTitle = "Non riesco a leggere i log del gioco.";
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
        if (!Advanced && !Dialogs.Confirm($"Disattivo il Neural Rendering su {Game.Name}?\n\nVengono rimessi i file originali. ReShade e le altre mod restano installati.")) return;
        await Main.RunAsync("Disattivazione del Neural Rendering", async (progress, cancel) =>
        {
            var plan = await Task.Run(() => Main.Host.Components.PlanRemoval(ctx, id), cancel);
            if (Advanced && !Dialogs.ConfirmPlan(plan)) throw new OperationCanceledException();
            await Task.Run(() => Main.Host.Components.Apply(ctx, plan, progress), cancel);
        });
        VerifyTitle = null;
        await AfterChangeAsync(id);
    }

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
            Main.Status = $"Avvio di {Game.Name}…";
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
    public string Title => $"{Install.ComponentName} · da {Install.Source}";
    public string Detail => string.Join(", ", Install.Files.Select(f => f.Path).Take(4)) + (Install.Files.Count > 4 ? $" (+{Install.Files.Count - 4})" : "")
                            + (Install.WithBackup > 0 ? $" · {Install.WithBackup} originali salvati" : "");

    [RelayCommand] private Task ImportAsync() => owner.ImportOneAsync(Install);
}

public sealed partial class LegacyItemViewModel(GameDetailViewModel owner, LegacyInstall install) : ObservableObject
{
    public LegacyInstall Install { get; } = install;
    public string Title => Install.Title;
    public string Detail => $"{Install.Files.Count} file · {Install.Marker}" + (IsStale ? " · superato: i file sono cambiati dopo la 1.x" : "");
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
            Dialogs.Error("Cartella dati di HDLSS5 1.x non trovata (serve per i backup). Indicala in Impostazioni.");
            return;
        }
        var component = ComponentCatalog.Get(Install.AdoptableAs);
        var ok = await owner.Main.RunAsync("Adozione installazione 1.x", async (progress, cancel) =>
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
            Dialogs.Error("Cartella dati di HDLSS5 1.x non trovata. Indicala in Impostazioni → Cartella di HDLSS5 1.x.");
            return;
        }
        var ok = await owner.Main.RunAsync("Ripristino installazione 1.x", async (progress, cancel) =>
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

