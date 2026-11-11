using System.Windows;
using System.Windows.Threading;
using Hdlss.App.ViewModels;
using Hdlss.App.Views;

namespace Hdlss.App;

public partial class App : Application
{
    private static Mutex? _single;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One instance per data folder: two copies with separate data may run side by side,
        // two windows on the same data (which could race on the same game files) may not.
        var dataKey = Hdlss.Core.IO.FileHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(
            Hdlss.Core.Storage.AppPaths.ForCurrentProcess().Data.ToUpperInvariant()))[..16];
        EarlyLanguage();
        _single = new Mutex(true, @"Local\HDLSS5-v2-" + dataKey, out bool first);
        if (!first)
        {
            MessageBox.Show(Loc.T("app.alreadyOpen"), "HDLSS5", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            _host = new AppHost();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("app.startFail") + ex.Message, "HDLSS5", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Loc.Instance.Language = _host.Settings.Language;
        Views.AccentTheme.Apply(_host.Settings.AccentColor);
        DarkTitleBar.Register();
        DispatcherUnhandledException += OnUnhandled;
        var vm = new MainViewModel(_host);
        var window = new MainWindow { DataContext = vm };
        DarkTitleBar.Attach(window);
        MainWindow = window;
        if (Views.AccentTheme.Current != Views.AccentTheme.Default) window.Icon = Views.AccentTheme.Icon(Views.AccentTheme.Current, 256);
        window.Show();
        _ = vm.InitializeAsync();
    }

    /// <summary>Message boxes shown before the host exists still follow the saved language.</summary>
    private static void EarlyLanguage()
    {
        try
        {
            var paths = Hdlss.Core.Storage.AppPaths.ForCurrentProcess();
            Loc.Instance.Language = Hdlss.Core.Settings.AppSettings.Load(paths).Language;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Hdlss.Core.HdlssException or System.Text.Json.JsonException) { }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // One message at a time, and the same error at most once every 5 seconds: an error that repeats while its
        // message is open must not stack dialogs or flood the log (the app would look frozen).
        var key = e.Exception.GetType().FullName + "|" + e.Exception.Message;
        var now = DateTime.UtcNow;
        bool repeat = key == _lastErrorKey && now - _lastErrorAt < TimeSpan.FromSeconds(5);
        _lastErrorKey = key;
        _lastErrorAt = now;
        if (repeat) return;
        _host?.Log.Error("Errore non gestito", e.Exception);
        if (_showingError) return;
        _showingError = true;
        try { MessageBox.Show(e.Exception.Message, Loc.T("dlg.errorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _showingError = false; }
    }

    private bool _showingError;
    private string? _lastErrorKey;
    private DateTime _lastErrorAt;

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
