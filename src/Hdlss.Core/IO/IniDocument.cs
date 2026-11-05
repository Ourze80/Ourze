using System.Text;
using System.Text.RegularExpressions;

namespace Hdlss.Core.IO;

/// <summary>
/// Line-preserving INI editor. Comments, ordering, unknown keys, newline style and the
/// original encoding (UTF-8 with/without BOM, UTF-16 LE/BE) are kept untouched.
/// Keys that appear before the first section are addressed with section "".
/// </summary>
public sealed partial class IniDocument
{
    private readonly List<string> _lines;
    private readonly string _newline;
    public Encoding Encoding { get; }

    [GeneratedRegex(@"^\s*\[([^\]\r\n]+)\]\s*$")] private static partial Regex SectionLine();

    private IniDocument(string text, Encoding encoding)
    {
        _newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        _lines = Regex.Split(text, "\r?\n").ToList();
        if (_lines.Count == 1 && _lines[0].Length == 0) _lines.Clear();
        Encoding = encoding;
    }

    public static IniDocument Parse(string text) => new(text, new UTF8Encoding(false));

    public static IniDocument Load(string path)
    {
        if (!File.Exists(path)) return new IniDocument("", new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out int preamble);
        return new IniDocument(encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
    }

    public static Encoding DetectEncoding(byte[] b, out int preamble)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { preamble = 2; return Encoding.Unicode; }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { preamble = 2; return Encoding.BigEndianUnicode; }
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { preamble = 3; return new UTF8Encoding(true); }
        preamble = 0;
        return new UTF8Encoding(false);
    }

    public override string ToString() => string.Join(_newline, _lines);

    public byte[] ToBytes() => [.. Encoding.GetPreamble(), .. Encoding.GetBytes(ToString())];

    public string? Get(string section, string key)
    {
        string? value = null;
        foreach (var (index, inside) in Walk(section))
            if (inside && TryKey(_lines[index], key, out var v)) value = v;
        return value;
    }

    public void Set(string section, string key, string value)
    {
        int insertAt = -1, lastInside = -1;
        bool found = section.Length == 0, replaced = false;
        foreach (var (index, inside) in Walk(section))
        {
            if (!inside) continue;
            found = true;
            if (!IsSection(_lines[index])) lastInside = index;
            else insertAt = index + 1;
            if (TryKey(_lines[index], key, out _)) { _lines[index] = $"{key}={value}"; replaced = true; }
        }
        if (replaced) return;
        string line = $"{key}={value}";
        if (section.Length == 0)
        {
            int firstSection = _lines.FindIndex(IsSection);
            _lines.Insert(firstSection < 0 ? _lines.Count : firstSection, line);
            return;
        }
        if (!found)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add(line);
            return;
        }
        // Insert after the last non-blank line of the section.
        int at = lastInside >= 0 ? lastInside : insertAt - 1;
        while (at >= 0 && at < _lines.Count && _lines[at].Trim().Length == 0 && !IsSection(_lines[at])) at--;
        _lines.Insert(at + 1, line);
    }

    public bool Remove(string section, string key)
    {
        var hits = Walk(section).Where(x => x.Inside && TryKey(_lines[x.Index], key, out _)).Select(x => x.Index).ToList();
        for (int i = hits.Count - 1; i >= 0; i--) _lines.RemoveAt(hits[i]);
        return hits.Count > 0;
    }

    // ReShade list values: comma separated, a literal comma is written as ",,".
    public static List<string> SplitList(string? value)
    {
        var list = new List<string>();
        var item = new StringBuilder();
        value ??= "";
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == ',')
            {
                if (i + 1 < value.Length && value[i + 1] == ',') { item.Append(','); i++; continue; }
                if (item.ToString().Trim().Length > 0) list.Add(item.ToString().Trim());
                item.Clear();
            }
            else item.Append(value[i]);
        }
        if (item.ToString().Trim().Length > 0) list.Add(item.ToString().Trim());
        return list;
    }

    public static string JoinList(IEnumerable<string> items) => string.Join(",", items.Select(x => x.Replace(",", ",,")));

    public bool ListContains(string section, string key, string item) =>
        SplitList(Get(section, key)).Contains(item, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds <paramref name="item"/> to a list value; returns false when already present.</summary>
    public bool ListAdd(string section, string key, string item)
    {
        var list = SplitList(Get(section, key));
        if (list.Contains(item, StringComparer.OrdinalIgnoreCase)) return false;
        list.Add(item);
        Set(section, key, JoinList(list));
        return true;
    }

    public bool ListRemove(string section, string key, string item)
    {
        var current = Get(section, key);
        if (current == null) return false;
        var list = SplitList(current);
        int removed = list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        Set(section, key, JoinList(list));
        return true;
    }

    private IEnumerable<(int Index, bool Inside)> Walk(string section)
    {
        bool inside = section.Length == 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            var m = SectionLine().Match(_lines[i]);
            if (m.Success) inside = string.Equals(m.Groups[1].Value.Trim(), section, StringComparison.OrdinalIgnoreCase);
            yield return (i, inside);
        }
    }

    private static bool IsSection(string line) => SectionLine().IsMatch(line);

    private static bool TryKey(string line, string key, out string value)
    {
        value = "";
        int eq = line.IndexOf('=');
        if (eq <= 0 || IsSection(line)) return false;
        var name = line[..eq].Trim();
        if (name.StartsWith(';') || name.StartsWith('#') || !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return false;
        value = line[(eq + 1)..].Trim();
        return true;
    }
}
