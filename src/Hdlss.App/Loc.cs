using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace Hdlss.App;

/// <summary>
/// UI strings from embedded i18n/*.json. Bind with {Binding [key], Source={x:Static app:Loc.Instance}};
/// changing <see cref="Language"/> refreshes every binding. Missing keys fall back to Italian, then to the key.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    // Must be initialized before Instance (static initializers run in declaration order).
    public static readonly string[] Languages = ["it", "en"];
    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _tables = [];
    private string _language = "it";

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc()
    {
        var asm = typeof(Loc).Assembly;
        foreach (var lang in Languages)
        {
            using var stream = asm.GetManifestResourceStream($"Hdlss.App.i18n.{lang}.json");
            if (stream == null) continue;
            _tables[lang] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
    }

    /// <summary>Culture of the chosen language, for formatting (the thread culture may be stale, see Tx.Selected).</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("it-IT");

    public string Language
    {
        get => _language;
        set
        {
            _language = Languages.Contains(value) ? value : "it";
            // Core picks its language from the UI culture (its own Tx class): keep both in step.
            var culture = CultureInfo.GetCultureInfo(_language == "en" ? "en-US" : "it-IT");
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Culture = culture;
            Hdlss.Core.Text.Tx.Selected = _language;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    /// <summary>Formats a localized template with the UI culture.</summary>
    public static string F(string key, params object?[] args) => string.Format(Instance.Culture, T(key), args);

    public string this[string key] =>
        _tables.TryGetValue(_language, out var t) && t.TryGetValue(key, out var v) ? v
        : _tables.TryGetValue("it", out var it) && it.TryGetValue(key, out var f) ? f : key;

    public static string T(string key) => Instance[key];
}
