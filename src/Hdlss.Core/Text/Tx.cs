using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Hdlss.Core.Text;

/// <summary>
/// User-visible text of Core in Italian and English, chosen by <see cref="CultureInfo.CurrentUICulture"/>.
/// This is a pure lookup into read-only embedded tables (it.json, en.json), not mutable application
/// state, so it does not break the "no static singletons" rule. A key missing in English falls back to
/// Italian, then to the key itself. Placeholders are {0}, {1}…; log lines are not translated.
/// </summary>
public static class Tx
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> It = new(() => Load("it"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> En = new(() => Load("en"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile string? _selected;

    /// <summary>
    /// The language the app chose ("it"/"en"). Set explicitly because the UI culture does not survive an
    /// async method that changed it: bindings read later on the UI thread would see the old language.
    /// </summary>
    public static string? Selected { get => _selected; set => _selected = value is "en" or "it" ? value : null; }

    /// <summary><see cref="Selected"/>, or "en" when the UI culture is English, otherwise "it".</summary>
    public static string Language => _selected ??
        (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "it");

    internal static IReadOnlyDictionary<string, string> Table(string language) => language == "en" ? En.Value : It.Value;

    public static string Get(string key) => Lookup(key, Language, En.Value, It.Value);

    /// <summary>English when asked for and present, else Italian, else the key itself.</summary>
    internal static string Lookup(string key, string language, IReadOnlyDictionary<string, string> en, IReadOnlyDictionary<string, string> it)
    {
        if (language == "en" && en.TryGetValue(key, out var english)) return english;
        return it.TryGetValue(key, out var italian) ? italian : key;
    }

    public static string Get(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var assembly = typeof(Tx).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".{language}.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
