using System.Text.RegularExpressions;

namespace Hdlss.Core.Components;

public enum VersionVerdict { UpToDate, Newer, Unknown }

/// <summary>
/// Compares the version label recorded at install time with the one an install would bring now.
/// Labels come from different sources ("6.8.0 (da 1.x)", "v10.0.2 · dinput8.dll", a commit hash,
/// "importato"), so this is tolerant: same text, same numbers, or a hash prefix count as up to date.
/// </summary>
public static partial class VersionCompare
{
    [GeneratedRegex(@"\d+(?:\.\d+)+")] private static partial Regex Numbers();
    [GeneratedRegex(@"^[0-9a-f]{7,40}$", RegexOptions.IgnoreCase)] private static partial Regex Hash();
    [GeneratedRegex(@"^(.+?)-\d+-g[0-9a-f]{7,40}$", RegexOptions.IgnoreCase)] private static partial Regex Describe();

    public static VersionVerdict Compare(string? installed, string latest)
    {
        var have = Clean(installed);
        var want = Clean(latest);
        if (have.Length == 0) return VersionVerdict.Unknown;
        if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return VersionVerdict.UpToDate;
        // "v3.0.1-1188-gf885e8d2" (git describe): a development build N commits after that tag.
        // It is ahead of the same tag, never "updated" back to it.
        if (Describe().Match(have) is { Success: true } dev)
        {
            var tag = dev.Groups[1].Value;
            return string.Equals(tag, want, StringComparison.OrdinalIgnoreCase) ? VersionVerdict.UpToDate : Compare(tag, want);
        }
        if (Hash().IsMatch(have) && Hash().IsMatch(want))
            return have.StartsWith(want, StringComparison.OrdinalIgnoreCase) || want.StartsWith(have, StringComparison.OrdinalIgnoreCase)
                ? VersionVerdict.UpToDate : VersionVerdict.Newer;
        var a = Numbers().Matches(have).Select(m => m.Value).ToList();
        var b = Numbers().Matches(want).Select(m => m.Value).ToList();
        if (a.Count == 0) return VersionVerdict.Unknown;              // "importato", "da 1.x"
        if (a.Count != b.Count) return VersionVerdict.Newer;
        for (int i = 0; i < a.Count; i++)
        {
            int c = CompareNumbers(a[i], b[i]);
            if (c < 0) return VersionVerdict.Newer;
            if (c > 0) return VersionVerdict.UpToDate;                // installed is newer (deliberate choice)
        }
        // Same numbers: a different text part (variant, build) means the install differs, unless the
        // recorded label is just the number (1.x adoption records "10.0.2" for "v10.0.2 · dinput8.dll").
        var rest = Text(have);
        return rest.Length == 0 || string.Equals(rest, Text(want), StringComparison.OrdinalIgnoreCase)
            ? VersionVerdict.UpToDate : VersionVerdict.Newer;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is not newer than <paramref name="installed"/> by their dotted numbers
    /// (installed is ahead or equal, e.g. "v3.0.1-1188-g..." vs "v3.0.1"): installing it would be a reinstall or downgrade.
    /// False when the numbers can't be compared (hashes, "importato").
    /// </summary>
    public static bool IsDowngrade(string? installed, string latest)
    {
        var have = Clean(installed);
        var want = Clean(latest);
        if (have.Length == 0) return false;
        // A git-describe build (v3.0.1-1195-g…) is ahead of its own tag.
        var dev = Describe().Match(have) is { Success: true } m;
        if (dev) have = Describe().Match(have).Groups[1].Value;
        var a = Numbers().Matches(have).Select(x => x.Value).ToList();
        var b = Numbers().Matches(want).Select(x => x.Value).ToList();
        if (a.Count == 0 || b.Count == 0) return false;
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            int c = CompareNumbers(a[i], b[i]);
            if (c != 0) return c > 0;
        }
        // Same numbers: only a dev build is ahead; a different variant ("F5 · v1.2.0" → "Standard · v1.2.0") is not a downgrade.
        return dev;
    }

    private static string Text(string label) =>
        string.Concat(Numbers().Replace(label, " ").Split([' ', '·', 'v', 'V'], StringSplitOptions.RemoveEmptyEntries));

    private static string Clean(string? label)
    {
        var s = (label ?? "").Trim();
        foreach (var suffix in new[] { "(da 1.x)", "(importato)", "· importato", "importato", "da 1.x" })
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) s = s[..^suffix.Length].Trim();
        return s;
    }

    /// <summary>Compares dotted numbers, ignoring trailing zero/extra parts: 6.8.0.2155 equals 6.8.0.</summary>
    private static int CompareNumbers(string x, string y)
    {
        var p = x.Split('.').Select(long.Parse).ToArray();
        var q = y.Split('.').Select(long.Parse).ToArray();
        int n = Math.Min(p.Length, q.Length);
        for (int i = 0; i < n; i++)
            if (p[i] != q[i]) return p[i].CompareTo(q[i]);
        return 0;
    }
}
