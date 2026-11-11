using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Hdlss.Core.Storage;
using Hdlss.Core.Text;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

public enum ComponentStatusKind { NotInstalled, Installed, External, Blocked }

public sealed record ComponentStatus(ComponentStatusKind Kind, string Detail, string? Version = null)
{
    public static ComponentStatus Missing(string? detail = null) => new(ComponentStatusKind.NotInstalled, detail ?? Tx.Get("status.notInstalled"));
}

public enum ComponentCategory { NeuralRendering, Upscaling, Hdr, Shaders, FrameGeneration, Loaders }

/// <param name="Custom">When set, the user may also type a whole number in this range besides the choices.</param>
public sealed record ComponentOption(string Key, string Label, IReadOnlyList<(string Value, string Label)> Choices, string Default,
    OptionRange? Custom = null)
{
    /// <summary>The value as the component reads it: one of the choices, or a valid custom number.</summary>
    public string? Normalize(string? value) =>
        value != null && Choices.Any(c => c.Value == value) ? value : Custom?.Normalize(value);
}

/// <summary>Whole number typed by the user, e.g. "1234", "1234 nit".</summary>
public sealed record OptionRange(int Min, int Max, string Unit)
{
    public string? Normalize(string? text)
    {
        var digits = text?.Replace(Unit, "", StringComparison.OrdinalIgnoreCase).Replace(".", "").Replace(" ", "").Trim();
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n >= Min && n <= Max ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public string Label(string value) => $"{value} {Unit}";
    public string Hint => Tx.Get("opt.range.hint", Min, Max);
}

/// <summary>Shared services handed to components (no static singletons, unlike 1.x).</summary>
public sealed class Services(AppPaths paths, AppSettings settings, ContentStore store, StateStore states,
    TransactionEngine engine, HttpService http, Log log)
{
    public AppPaths Paths { get; } = paths;
    public AppSettings Settings { get; } = settings;
    public ContentStore Store { get; } = store;
    public StateStore States { get; } = states;
    public TransactionEngine Engine { get; } = engine;
    public HttpService Http { get; } = http;
    public Log Log { get; } = log;
    /// <summary>Game covers (custom, store and automatic).</summary>
    public CoverService Covers { get; } = new(paths, http, log);
    /// <summary>DLSS5 NR (Lecram 12.x) packages the user imported; never bundled or downloaded.</summary>
    public NrAddonLibrary NrAddons { get; } = NrAddonLibrary.Create(paths, log);
}

/// <summary>A snapshot of one game folder used for detection and planning.</summary>
public sealed class GameContext
{
    public Game Game { get; }
    public Services Services { get; }
    public GpuInfo Gpu { get; }
    public string Folder { get; }
    public GraphicsApi DetectedApi { get; }
    public GraphicsApi Api { get; }
    /// <summary>False when the API was inferred from folder evidence rather than a single import.</summary>
    public bool ApiCertain { get; }
    public GameState State { get; private set; }
    public string? StateStamp { get; private set; }
    public FolderScan Scan { get; private set; }

    public GameContext(Game game, Services services, GpuInfo gpu, bool background = false)
    {
        _background = background;
        if (!game.HasExe) throw new BlockedException(Tx.Get("ctx.noExe"));
        Game = game;
        Services = services;
        Gpu = gpu;
        Folder = game.Folder;
        DetectedApi = ExecutableDetector.DetectApi(game.Exe, out var certain, game.ModFolder);
        var manual = services.Settings.Profile(game.Id).ApiOverride;
        Api = manual ?? DetectedApi;
        ApiCertain = manual != null || certain;
        State = services.States.Load(game.Id, Folder);
        RenoDxNrComponent.AdoptManualReplacement(services, State, !background);
        StateStamp = services.States.Stamp(Folder);
        Scan = FolderScan.Run(Folder, game.Root);
    }

    private readonly bool _background;

    public void Refresh()
    {
        State = Services.States.Load(Game.Id, Folder);
        RenoDxNrComponent.AdoptManualReplacement(Services, State, !_background);
        StateStamp = Services.States.Stamp(Folder);
        Scan = FolderScan.Run(Folder, Game.Root);
    }

    public bool Installed(string componentId) => State.Get(componentId) != null;

    public PlanBuilder NewPlan(IComponent component, string version) =>
        new(Services.Store, State, StateStamp, Game.Name, component.Id, component.Name, version);
}

public interface IComponent
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>Component ids that must be installed first.</summary>
    IReadOnlyList<string> Requires { get; }
    IReadOnlyList<ComponentOption> Options { get; }
    ComponentCategory Category { get; }
    /// <summary>False hides the component unless "show all" is on (e.g. REFramework on non-RE games).</summary>
    bool IsRelevant(GameContext ctx);

    ComponentStatus Detect(GameContext ctx);
    /// <summary>Reasons why installation is impossible right now (empty = can install).</summary>
    IReadOnlyList<string> Blockers(GameContext ctx);
    Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel);
    /// <summary>
    /// The version label an install with these options would record right now (same format as
    /// <see cref="ComponentRecord.Version"/>), without downloading packages. Null = cannot tell.
    /// </summary>
    Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel);
    /// <summary>
    /// Installed-or-not, how what is on disk compares with the latest available. Null = cannot tell.
    /// For components that update the game's own files (DLSS, Streamline, NR model) this also
    /// answers when HDLSS5 never touched them.
    /// </summary>
    Task<UpdateCheck?> CheckUpdateAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel);
    /// <summary>
    /// Why nothing can be installed for this game (e.g. no mod in the catalog), or null when it can
    /// or when it can't be known (offline). Checked before showing "Installa".
    /// </summary>
    Task<string?> UnavailableReasonAsync(GameContext ctx, CancellationToken cancel);
    /// <summary>True when <see cref="UnavailableReasonAsync"/> actually checks something (online catalog…).</summary>
    bool ChecksAvailability { get; }
    /// <summary>True when the files can already be in the game (DLLs shipped by the game): check them even if not managed.</summary>
    bool ChecksGameFiles { get; }
}

/// <summary>Result of a version check: what is there, what is available, and the verdict.</summary>
public sealed record UpdateCheck(string Present, string Latest, VersionVerdict Verdict);

public abstract class ComponentBase : IComponent
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual IReadOnlyList<string> Requires => [];
    public virtual IReadOnlyList<ComponentOption> Options => [];
    public virtual ComponentCategory Category => ComponentCategory.NeuralRendering;
    public virtual bool IsRelevant(GameContext ctx) => true;
    /// <summary>Loaded by ReShade: blocks removing a managed ReShade while installed.</summary>
    public virtual bool NeedsReShade => false;

    public virtual ComponentStatus Detect(GameContext ctx)
    {
        var record = ctx.State.Get(Id);
        return record != null
            ? new ComponentStatus(ComponentStatusKind.Installed, Tx.Get("status.installed", record.Version, record.UpdatedAt), record.Version)
            : DetectExternal(ctx) ?? ComponentStatus.Missing();
    }

    /// <summary>Recognizes a copy installed by other tools (or HDLSS 1.x).</summary>
    protected virtual ComponentStatus? DetectExternal(GameContext ctx) => null;

    public IReadOnlyList<string> Blockers(GameContext ctx)
    {
        var list = new List<string>();
        foreach (var req in Requires.Where(r => !ctx.Installed(r)))
            list.Add(Tx.Get("comp.requires", ComponentCatalog.NameOf(req)));
        CheckBlockers(ctx, list);
        return list;
    }

    protected virtual void CheckBlockers(GameContext ctx, List<string> blockers) { }

    /// <summary>For a managed component: the file another program has since replaced (null when none). Hashes files.</summary>
    public virtual string? TakenOverBy(GameContext ctx) => null;

    public abstract Task<ChangePlan> PlanInstallAsync(GameContext ctx, IReadOnlyDictionary<string, string> options,
        IProgress<OperationProgress>? progress, CancellationToken cancel);

    public virtual Task<string?> LatestVersionAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel) =>
        Task.FromResult<string?>(null);

    public virtual bool ChecksGameFiles => false;

    /// <summary>Version label of what is on disk right now (same format as the latest), when readable from the files.</summary>
    protected virtual string? PresentVersion(GameContext ctx) => null;

    public virtual async Task<UpdateCheck?> CheckUpdateAsync(GameContext ctx, IReadOnlyDictionary<string, string> options, CancellationToken cancel)
    {
        var latest = await LatestVersionAsync(ctx, options, cancel);
        if (latest == null) return null;
        // The files themselves are the truth when they can be read: labels recorded by older
        // versions ("Lecram 10.8 (da 1.x)") may lack parts the current label has.
        var present = PresentVersion(ctx);
        if (present != null)
        {
            var fromFiles = VersionCompare.Compare(present, latest);
            if (fromFiles != VersionVerdict.Unknown) return new(present, latest, fromFiles);
        }
        var recorded = ctx.State.Get(Id)?.Version;
        if (recorded != null) return new(recorded, latest, VersionCompare.Compare(recorded, latest));
        return present != null ? new(present, latest, VersionVerdict.Unknown) : null;
    }

    public virtual Task<string?> UnavailableReasonAsync(GameContext ctx, CancellationToken cancel) => Task.FromResult<string?>(null);
    public virtual bool ChecksAvailability => false;

    protected string Option(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var v) ? v : Options.First(o => o.Key == key).Default;
}

/// <summary>Components that can also install a file the user downloaded manually.</summary>
public interface ILocalImport
{
    string ImportLabel { get; }
    string ImportFilter { get; }
    Task<ChangePlan> PlanImportAsync(GameContext ctx, string file, IProgress<OperationProgress>? progress, CancellationToken cancel);
}