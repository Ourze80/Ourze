namespace Hdlss.Core.Storage;

/// <summary>
/// All on-disk locations used by the app. Portable by default (next to the executable);
/// <c>data-location.txt</c> beside the exe can redirect the data folder.
/// </summary>
public sealed class AppPaths
{
    public string AppDir { get; }
    public string Data { get; }
    public string Store => Path.Combine(Data, "store");
    public string Journal => Path.Combine(Data, "journal");
    public string State => Path.Combine(Data, "state");
    public string Cache => Path.Combine(Data, "cache");
    public string HttpCache => Path.Combine(Cache, "http");
    public string Downloads => Path.Combine(Cache, "downloads");
    public string Logs => Path.Combine(Data, "logs");
    public string Temp => Path.Combine(Data, "tmp");
    public string SettingsFile => Path.Combine(Data, "settings.json");
    public string Bundled => Path.Combine(AppDir, "bundled");

    public AppPaths(string appDir, string? dataOverride = null)
    {
        AppDir = Path.GetFullPath(appDir);
        Data = Path.GetFullPath(dataOverride ?? ReadLocation(AppDir) ?? Path.Combine(AppDir, "data-v2"));
    }

    public static AppPaths ForCurrentProcess() => new(AppContext.BaseDirectory);

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Data, Store, Journal, State, Cache, HttpCache, Downloads, Logs, Temp })
            Directory.CreateDirectory(dir);
    }

    private static string? ReadLocation(string appDir)
    {
        var file = Path.Combine(appDir, "data-location.txt");
        if (!File.Exists(file)) return null;
        var line = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (line == null) return null;
        return Path.IsPathRooted(line) ? line : Path.Combine(appDir, line);
    }

    /// <summary>Candidate folders that may hold the bundled NR models (<c>nr-packages</c>).</summary>
    public IEnumerable<string> NrPackageCandidates(string? legacyAppDir)
    {
        yield return Path.Combine(AppDir, "nr-packages");
        if (!string.IsNullOrEmpty(legacyAppDir)) yield return Path.Combine(legacyAppDir, "nr-packages");
        var parent = Directory.GetParent(AppDir)?.FullName;
        if (parent != null) yield return Path.Combine(parent, "nr-packages");
    }
}
