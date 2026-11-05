using System.ComponentModel;
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

    public string Language
    {
        get => _language;
        set
        {
            _language = Languages.Contains(value) ? value : "it";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] =>
        _tables.TryGetValue(_language, out var t) && t.TryGetValue(key, out var v) ? v
        : _tables.TryGetValue("it", out var it) && it.TryGetValue(key, out var f) ? f : key;

    public static string T(string key) => Instance[key];
}
