using System.Globalization;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// The key that opens the in-game menu of ReShade (ReShade.ini [INPUT] KeyOverlay) and OptiScaler
/// (OptiScaler.ini [Menu] ShortcutKey). Values are Windows virtual-key codes.
/// </summary>
public static class MenuKeys
{
    public const string OptionKey = "menukey";
    public const string Default = "default";
    public const int ReShadeDefault = 0x24;     // Home
    public const int OptiScalerDefault = 0x2D;  // Insert

    /// <summary>Keys offered in the drop-down (single keys that games rarely use).</summary>
    public static readonly IReadOnlyList<(int Code, string Name)> Keys =
    [
        (0x24, "Home"), (0x2D, "Ins"), (0x23, "Fine"), (0x2E, "Canc"), (0x21, "Pag ↑"), (0x22, "Pag ↓"),
        (0x13, "Pausa"), (0x91, "Bloc Scorr"),
        (0x70, "F1"), (0x71, "F2"), (0x72, "F3"), (0x73, "F4"), (0x74, "F5"), (0x75, "F6"),
        (0x76, "F7"), (0x77, "F8"), (0x78, "F9"), (0x79, "F10"), (0x7A, "F11"), (0x7B, "F12"),
    ];

    public static string NameOf(int code) => Keys.FirstOrDefault(k => k.Code == code).Name ?? $"0x{code:X2}";

    public static ComponentOption Option(int defaultCode) =>
        new(OptionKey, "Tasto del menu",
            [(Default, $"Predefinito ({NameOf(defaultCode)})"), .. Keys.Where(k => k.Code != defaultCode).Select(k => (Hex(k.Code), k.Name))],
            Default);

    public static string Hex(int code) => $"0x{code:X2}";

    /// <summary>The chosen code, or null for "default" (leave the file as it is).</summary>
    public static int? Parse(string value) =>
        value != Default && value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c) ? c : null;

    /// <summary>Key ReShade will use in this folder: from its ini, Home when unset.</summary>
    public static int ReShadeKey(GameContext ctx)
    {
        var raw = IniDocument.Load(PathGuard.Resolve(ctx.Folder, ctx.Scan.ReShadeConfig)).Get("INPUT", "KeyOverlay");
        var first = raw?.Split(',')[0].Trim();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : ReShadeDefault;
    }

    /// <summary>Key OptiScaler will use in this folder: from its ini, Insert when "auto" or unset.</summary>
    public static int OptiScalerKey(GameContext ctx)
    {
        var raw = IniDocument.Load(PathGuard.Resolve(ctx.Folder, "OptiScaler.ini")).Get("Menu", "ShortcutKey")?.Trim();
        if (raw == null || raw.Equals("auto", StringComparison.OrdinalIgnoreCase)) return OptiScalerDefault;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : OptiScalerDefault;
    }

    /// <summary>
    /// Writes the chosen key. "Default" leaves the file alone, except when an earlier install of this
    /// component changed the key: then the value from before that change is put back.
    /// </summary>
    public static void Apply(PlanBuilder plan, ComponentRecord? existing, string file, string section, string key, string option,
        int defaultCode, Func<int, string> format)
    {
        var code = Parse(option);
        if (code != null) { plan.IniSet(file, section, key, format(code.Value)); return; }
        var earlier = existing?.IniEdits.FirstOrDefault(e => e.Kind == IniEditKind.Set && e.Applied
            && string.Equals(e.File, PathGuard.Normalize(file), StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (earlier != null) plan.IniSet(file, section, key, earlier.Previous ?? format(defaultCode));
    }

    public static string ReShadeFormat(int code) => $"{code},0,0,0";

    public static string ConflictMessage(int code) =>
        $"ReShade e OptiScaler userebbero lo stesso tasto ({NameOf(code)}) per aprire il menu: scegli un tasto diverso in «Tasto del menu».";
}
