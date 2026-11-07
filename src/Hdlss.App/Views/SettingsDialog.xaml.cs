using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hdlss.Core.Legacy;

namespace Hdlss.App.Views;

public partial class SettingsDialog : Window
{
    private readonly AppHost _host;

    public SettingsDialog(AppHost host)
    {
        InitializeComponent();
        _host = host;
        LanguageBox.ItemsSource = new[] { "it · Italiano", "en · English" };
        LanguageBox.SelectedIndex = host.Settings.Language == "en" ? 1 : 0;
        LegacyDir.Text = host.Settings.LegacyAppDir ?? "";
        Libraries.Text = string.Join(Environment.NewLine, host.Settings.ExtraSteamLibraries);
        LegacyDir.TextChanged += (_, _) => UpdateLegacyInfo();
        UpdateLegacyInfo();
        DataInfo.Text = Loc.F("set.data.info", host.Paths.Data, (host.Services.Store.SizeBytes() / 1048576.0).ToString("N0", Loc.Instance.Culture));
        InitColors();
    }

    // ── App color: live preview, saved on «Salva», reverted on «Annulla» ──

    private static readonly string[] Presets =
        ["#3DDBFE", "#4F8DFF", "#9D7BFF", "#E05CFF", "#FF5FA2", "#FF6B5B", "#FF9F43", "#FFD23D", "#8BE04E", "#2ED3A0", "#E8EAF0"];
    private Color _original;
    private bool _syncing;

    private void InitColors()
    {
        _original = AccentTheme.Current;
        var hue = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int h = 0; h <= 360; h += 60) hue.GradientStops.Add(new GradientStop(AccentTheme.FromHsv(h, 1, 1), h / 360.0));
        HueSlider.Background = hue;
        foreach (var hex in Presets)
        {
            AccentTheme.TryParse(hex, out var c);
            var swatch = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(0), ToolTip = hex,
                Background = AccentTheme.Brush(c), BorderBrush = AccentTheme.Brush(AccentTheme.Scale(c, 0.6)), Cursor = Cursors.Hand,
                Template = SwatchTemplate(),
            };
            swatch.Click += (_, _) => SetColor(c);
            Swatches.Children.Add(swatch);
        }
        Closed += (_, _) => { if (DialogResult != true) AccentTheme.Apply(_original); };
        SetColor(_original);
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void SetColor(Color c, bool fromSliders = false)
    {
        AccentTheme.Apply(c);
        _syncing = true;
        var (h, s, v) = AccentTheme.ToHsv(c);
        if (!fromSliders)
        {
            if (s > 0) HueSlider.Value = h;
            SatSlider.Value = s * 100;
            ValSlider.Value = Math.Max(v * 100, ValSlider.Minimum);
        }
        double hue = HueSlider.Value, sat = SatSlider.Value / 100, val = ValSlider.Value / 100;
        SatSlider.Background = Gradient(AccentTheme.FromHsv(hue, 0, val), AccentTheme.FromHsv(hue, 1, val));
        ValSlider.Background = Gradient(AccentTheme.FromHsv(hue, sat, 0.25), AccentTheme.FromHsv(hue, sat, 1));
        HexBox.Text = AccentTheme.ToHex(c);
        HexError.Visibility = Visibility.Collapsed;
        DarkWarning.Visibility = AccentTheme.Luminance(c) < 0.12 ? Visibility.Visible : Visibility.Collapsed;
        _syncing = false;
    }

    private static LinearGradientBrush Gradient(Color from, Color to) => new(from, to, 0);

    private void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !IsLoaded) return;
        SetColor(AccentTheme.FromHsv(HueSlider.Value, SatSlider.Value / 100, ValSlider.Value / 100), fromSliders: true);
    }

    private void Hex_Commit(object sender, RoutedEventArgs e)
    {
        if (AccentTheme.TryParse(HexBox.Text, out var c)) { if (c != AccentTheme.Current) SetColor(c); else HexError.Visibility = Visibility.Collapsed; }
        else HexError.Visibility = Visibility.Visible;
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Hex_Commit(sender, e); e.Handled = true; }
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e) => SetColor(AccentTheme.Default);

    private void UpdateLegacyInfo()
    {
        var data = LegacyInstalls.FindDataFolder(string.IsNullOrWhiteSpace(LegacyDir.Text) ? null : LegacyDir.Text.Trim(), _host.Paths.AppDir);
        LegacyInfo.Text = data == null
            ? Loc.T("set.legacy.none")
            : Loc.F("set.legacy.found", data);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = Dialogs.PickFolder(LegacyDir.Text);
        if (folder != null) LegacyDir.Text = folder;
    }

    private async void Maintenance_Click(object sender, RoutedEventArgs e)
    {
        MaintenanceInfo.Text = Loc.T("set.cleaning");
        try
        {
            var report = await Task.Run(() => _host.Maintenance.Run(_host.Settings.KeepJournalsPerGame));
            MaintenanceInfo.Text = report.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MaintenanceInfo.Text = ex.Message;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.Paths.Data}\"") { UseShellExecute = true });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        s.Language = LanguageBox.SelectedIndex == 1 ? "en" : "it";
        s.AccentColor = AccentTheme.Current == AccentTheme.Default ? null : AccentTheme.ToHex(AccentTheme.Current);
        s.LegacyAppDir = string.IsNullOrWhiteSpace(LegacyDir.Text) ? null : LegacyDir.Text.Trim();
        s.ExtraSteamLibraries = Libraries.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _host.SaveSettings();
        Loc.Instance.Language = s.Language;
        DialogResult = true;
    }
}
