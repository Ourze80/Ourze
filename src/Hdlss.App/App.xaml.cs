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
        _single = new Mutex(true, @"Local\HDLSS5-v2-" + dataKey, out bool first);
        if (!first)
        {
            MessageBox.Show("HDLSS5 è già aperto.", "HDLSS5", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            _host = new AppHost();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossibile avviare HDLSS5:\n" + ex.Message, "HDLSS5", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Loc.Instance.Language = _host.Settings.Language;
        DarkTitleBar.Register();
        DispatcherUnhandledException += OnUnhandled;
        var vm = new MainViewModel(_host);
        var window = new MainWindow { DataContext = vm };
        DarkTitleBar.Attach(window);
        MainWindow = window;
        window.Show();
        _ = vm.InitializeAsync();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Log.Error("Errore non gestito", e.Exception);
        MessageBox.Show(e.Exception.Message, "HDLSS5 · errore", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
