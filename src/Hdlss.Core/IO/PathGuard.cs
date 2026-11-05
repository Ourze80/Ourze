using System.Text.RegularExpressions;

namespace Hdlss.Core.IO;

/// <summary>
/// Resolves package-relative paths inside a root folder and rejects anything that could
/// escape it: absolute paths, "..", reserved device names, trailing dots/spaces and
/// reparse points (symlinks/junctions) anywhere between the root and the target.
/// </summary>
public static partial class PathGuard
{
    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9]|LPT[0-9])(\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Normalizes a relative path to forward slashes and validates every segment.</summary>
    public static string Normalize(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new UnsafePathException(relative);
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new UnsafePathException(relative);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.TrimEnd(' ', '.') != part || ReservedName().IsMatch(part))
                throw new UnsafePathException(relative);
        }
        return string.Join('/', parts);
    }

    /// <summary>Returns the absolute path of <paramref name="relative"/> under <paramref name="root"/>.</summary>
    public static string Resolve(string root, string relative)
    {
        var normalized = Normalize(relative);
        var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(top, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase)) throw new UnsafePathException(relative);

        for (var cursor = full; cursor != null && !SameDirectory(cursor, top); cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new UnsafePathException(relative, "collegamento simbolico o junction non supportato");
        }
        return full;
    }

    public static bool IsUnder(string child, string parent)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(child).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string RelativeTo(string root, string file)
    {
        if (!IsUnder(file, root)) throw new UnsafePathException(file, "fuori dalla cartella del gioco");
        return Normalize(Path.GetRelativePath(root, file));
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}

public sealed class UnsafePathException(string path, string reason = "percorso non sicuro")
    : HdlssException($"Percorso rifiutato ({reason}): {path}");
