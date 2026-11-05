using Hdlss.Core.Components;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.Net;
using Hdlss.Core.Settings;
using Hdlss.Core.Storage;
using Hdlss.Core.Transactions;

namespace Hdlss.App;

/// <summary>Composition root: builds every service once at startup.</summary>
public sealed class AppHost : IDisposable
{
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public Log Log { get; }
    public Services Services { get; }
    public ComponentService Components { get; }
    public GameLibrary Library { get; }
    public Maintenance Maintenance { get; }
    public Lazy<GpuInfo> Gpu { get; } = new(GpuInfo.Detect);

    public AppHost()
    {
        Paths = AppPaths.ForCurrentProcess();
        Paths.EnsureCreated();
        Log = new Log(Paths.Logs);
        Settings = AppSettings.Load(Paths);
        var store = new ContentStore(Paths.Store);
        var states = new StateStore(Paths.State);
        var engine = new TransactionEngine(store, states, Paths.Journal, Log);
        var http = new HttpService(Paths, Log);
        Services = new Services(Paths, Settings, store, states, engine, http, Log);
        Components = new ComponentService(Services);
        Library = new GameLibrary(Settings, Log);
        Maintenance = new Maintenance(engine, Log);
        Log.Info($"HDLSS5 {typeof(AppHost).Assembly.GetName().Version} · dati: {Paths.Data}");
    }

    public void SaveSettings()
    {
        try { Settings.Save(Paths); }
        catch (IOException ex) { Log.Error("Salvataggio impostazioni", ex); throw; }
    }

    /// <summary>Background housekeeping at startup: journals, unreferenced backups, caches, logs.</summary>
    public Task StartupMaintenanceAsync() => Task.Run(() =>
    {
        try
        {
            Maintenance.Run(Settings.KeepJournalsPerGame);
            Services.Http.TrimDownloads(Settings.DownloadCacheLimitMb << 20);
            Log.Prune(60);
            if (Directory.Exists(Paths.Temp))
                foreach (var dir in Directory.EnumerateDirectories(Paths.Temp)) Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Manutenzione all'avvio", ex);
        }
    });

    public void Dispose() => Services.Http.Dispose();
}
