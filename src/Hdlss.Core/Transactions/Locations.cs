using Hdlss.Core.Text;
using Hdlss.Core.IO;

namespace Hdlss.Core.Transactions;

/// <summary>
/// Resolves plan paths. "dxgi.dll" is relative to the game folder; "@cfg/Engine.ini" is
/// relative to the extra root registered under alias "cfg" (for example the Unreal
/// Saved/Config folder in %LOCALAPPDATA%). Both go through <see cref="PathGuard"/>.
/// </summary>
public static class Locations
{
    public static string Resolve(string folder, IReadOnlyDictionary<string, string>? roots, string path)
    {
        if (!path.StartsWith('@')) return PathGuard.Resolve(folder, path);
        int slash = path.IndexOf('/');
        if (slash < 2) throw new UnsafePathException(path);
        var alias = path[1..slash];
        if (roots == null || !roots.TryGetValue(alias, out var root)) throw new HdlssException(Tx.Get("loc.alias", alias, path));
        return PathGuard.Resolve(root, path[(slash + 1)..]);
    }

    /// <summary>The root folder a plan path belongs to (game folder or a registered extra root).</summary>
    public static string RootOf(string folder, IReadOnlyDictionary<string, string>? roots, string path)
    {
        if (!path.StartsWith('@')) return folder;
        var alias = path[1..path.IndexOf('/')];
        return roots != null && roots.TryGetValue(alias, out var root) ? root : folder;
    }

    /// <summary>
    /// Extra roots must be the game install itself, a folder inside it, or a folder inside the
    /// user's local app data. Links are refused only between the allowed base and the root:
    /// a junction higher up (e.g. a Game Pass library on a linked drive) is the user's setup.
    /// </summary>
    public static void ValidateRoot(string root, string gameRoot)
    {
        var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var game = Path.GetFullPath(gameRoot);
        var full = Path.GetFullPath(root);
        string? baseDir = SameOrUnder(full, game) ? game : SameOrUnder(full, local) && !SameDir(full, local) ? local : null;
        if (baseDir == null) throw new HdlssException(Tx.Get("loc.notAllowed", full));
        for (var d = new DirectoryInfo(full); d != null && SameOrUnder(d.FullName, baseDir) && !SameDir(d.FullName, baseDir); d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new HdlssException(Tx.Get("loc.junction", d.FullName));
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool SameOrUnder(string child, string parent) => SameDir(child, parent) || PathGuard.IsUnder(child, parent);
}
