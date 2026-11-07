using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Text;
using Xunit;

namespace Hdlss.Core.Tests;

/// <summary>Most tests assert Italian texts: the whole test run uses the Italian UI culture unless a test says otherwise.</summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void ForceItalian()
    {
        var it = new CultureInfo("it-IT");
        CultureInfo.DefaultThreadCurrentUICulture = it;
        CultureInfo.CurrentUICulture = it;
    }

    /// <summary>Runs <paramref name="action"/> with the given UI culture, then restores the previous one.</summary>
    internal static void With(string culture, Action action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try { action(); }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}

public class TxTests
{
    private static Dictionary<string, string> Load(string language)
    {
        var assembly = typeof(Tx).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".{language}.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    private static int PlaceholderCount(string text) =>
        Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(?:[:,][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Count();

    [Fact]
    public void EveryItalianKeyExistsInEnglishAndViceVersa()
    {
        var it = Load("it");
        var en = Load("en");
        Assert.Empty(it.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(it.Keys));
        Assert.NotEmpty(it);
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        var it = Load("it");
        var en = Load("en");
        foreach (var (key, text) in it)
            Assert.True(PlaceholderCount(text) == PlaceholderCount(en[key]), $"Placeholders differ for '{key}'");
    }

    [Fact]
    public void NoEnglishTextIsEmptyOrLeftItalianByAccident()
    {
        var en = Load("en");
        Assert.DoesNotContain(en, p => string.IsNullOrWhiteSpace(p.Value));
        // The button names in «…» are English in English texts.
        Assert.DoesNotContain(en, p => Regex.IsMatch(p.Value, "«(Eseguibile|Installa|Rimuovi|Disattiva|Ripristina|Annulla|Avvia)"));
    }

    [Fact]
    public void WarningsAndBlockersKeepTheTitleSeparator()
    {
        foreach (var language in new[] { "it", "en" })
            foreach (var (key, text) in Load(language))
                if (key.StartsWith("game.warn.") || key.StartsWith("addon.") || key.Contains(".block."))
                    Assert.True(text.Contains(": "), $"'{key}' ({language}) has no \": \" separator");
    }

    [Fact]
    public void ReturnsEnglishUnderEnglishCultureAndItalianUnderItalian()
    {
        TestCulture.With("en-US", () =>
        {
            Assert.Equal("en", Tx.Language);
            Assert.Equal("Not installed", Tx.Get("status.notInstalled"));
            Assert.Equal("Type a number from 100 to 10000", Tx.Get("opt.range.hint", 100, 10000));
        });
        TestCulture.With("it-IT", () =>
        {
            Assert.Equal("it", Tx.Language);
            Assert.Equal("Non installato", Tx.Get("status.notInstalled"));
            Assert.Equal("Scrivi un numero da 100 a 10000", Tx.Get("opt.range.hint", 100, 10000));
        });
        TestCulture.With("fr-FR", () => Assert.Equal("it", Tx.Language));
    }

    [Fact]
    public void MissingEnglishKeyFallsBackToItalianThenToTheKey()
    {
        var it = new Dictionary<string, string> { ["only.it"] = "Solo italiano", ["both"] = "Entrambi" };
        var en = new Dictionary<string, string> { ["both"] = "Both" };
        Assert.Equal("Solo italiano", Tx.Lookup("only.it", "en", en, it));
        Assert.Equal("Both", Tx.Lookup("both", "en", en, it));
        Assert.Equal("Entrambi", Tx.Lookup("both", "it", en, it));
        Assert.Equal("missing.key", Tx.Lookup("missing.key", "en", en, it));
        TestCulture.With("en-US", () => Assert.Equal("some.unknown.key", Tx.Get("some.unknown.key")));
    }

    [Fact]
    public void EnglishWarningsMentionTheExecutableButton()
    {
        TestCulture.With("en-US", () =>
        {
            Assert.Contains("«Executable & API»", Tx.Get("game.warn.noExe"));
            Assert.Contains("«Executable & API»", Tx.Get("game.warn.exeBySize"));
            Assert.Contains("«Executable & API»", Tx.Get("game.warn.exeMissing"));
        });
        TestCulture.With("it-IT", () => Assert.Contains("«Eseguibile»", Tx.Get("game.warn.noExe")));
    }
}
