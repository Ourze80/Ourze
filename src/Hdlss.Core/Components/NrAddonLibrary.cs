using System.Diagnostics;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Storage;
using Hdlss.Core.Text;

namespace Hdlss.Core.Components;

/// <summary>One locally imported DLSS5 NR (Lecram 12.x) package kept in the private data folder.</summary>
public sealed record NrAddonPackage(Version Version, string Label, string Folder)
{
    public const string Addon64 = "dlss5-nr.addon64";
    public const string Addon32 = "dlss5-nr.addon32";
    public const string HostDir = "dlss5-nr-host";
    public static readonly string[] HostFiles = ["ReShade64.dll", "dlss5-nr.addon64", "nr-host64.exe"];

    /// <summary>Files to install, relative to the ReShade folder, with their source path.</summary>
    public IEnumerable<(string Relative, string Source)> Files()
    {
        yield return (Addon64, Path.Combine(Folder, Addon64));
        var a32 = Path.Combine(Folder, Addon32);
        if (File.Exists(a32)) yield return (Addon32, a32);
        var host = Path.Combine(Folder, HostDir);
        foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            yield return (HostDir + "/" + Path.GetRelativePath(host, file).Replace('\\', '/'), file);
    }
}

/// <summary>Recognizes the DLSS5 NR add-on by its content (PE version info), never by name or hash.</summary>
public static class NrAddonInfo
{
    public const string ProductName = "DLSS5 NR";

    /// <summary>"Lecram 12.9.9" when the file is a DLSS5 NR add-on, otherwise null.</summary>
    public static string? Identify(string addonFile) => Read(addonFile) is { } v ? "Lecram " + Format(v) : null;

    /// <summary>The add-on's file version when it is a DLSS5 NR add-on.</summary>
    public static Version? Read(string addonFile)
    {
        try
        {
            if (!File.Exists(addonFile)) return null;
            var info = FileVersionInfo.GetVersionInfo(addonFile);
            if (!string.Equals(info.ProductName?.Trim(), ProductName, StringComparison.OrdinalIgnoreCase)) return null;
            return Parse(info.FileVersion) ?? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static Version? Parse(string? text)
    {
        var m = Regex.Match(text ?? "", @"\d+(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>12.9.9 (a zero revision is omitted).</summary>
    public static string Format(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(v.Build < 0 ? 2 : 3);
}

/// <summary>
/// Private copies of DLSS5 NR packages the user imported (folder or .zip). The paid add-on is never
/// bundled or downloaded: it lives in <c>data\nr-addons\&lt;version&gt;</c> only.
/// </summary>
public sealed class NrAddonLibrary(AppPaths paths, Log log)
{
    private static NrAddonLibrary? _current;

    /// <summary>
    /// Library of the running app, so the RenoDX NR card can list the imported variant in its
    /// drop-down (options have no game context). Plans and rules use <c>ctx.Services.NrAddons</c>.
    /// </summary>
    public static NrAddonLibrary? Current => _current;

    internal static NrAddonLibrary Create(AppPaths paths, Log log) => _current = new NrAddonLibrary(paths, log);

    public string Root => Path.Combine(paths.Data, "nr-addons");

    /// <summary>Imported packages, newest first.</summary>
    public IReadOnlyList<NrAddonPackage> Packages
    {
        get
        {
            if (!Directory.Exists(Root)) return [];
            // Read from disk only when the folder changed (a version added or removed): the UI asks often.
            var stamp = Directory.GetLastWriteTimeUtc(Root);
            lock (_gate)
            {
                if (_cache != null && stamp == _cacheStamp) return _cache;
                var list = new List<NrAddonPackage>();
                foreach (var dir in Directory.EnumerateDirectories(Root).Where(d => !d.Contains(".tmp-")))
                    if (Validate(dir, out var package, out _)) list.Add(package!);
                _cacheStamp = stamp;
                return _cache = list.OrderByDescending(p => p.Version).ToList();
            }
        }
    }

    private readonly object _gate = new();
    private IReadOnlyList<NrAddonPackage>? _cache;
    private DateTime _cacheStamp;

    public NrAddonPackage? Newest => Packages.FirstOrDefault();

    public NrAddonPackage Import(string folderOrZip)
    {
        string? temp = null;
        try
        {
            string source;
            if (File.Exists(folderOrZip))
            {
                if (!folderOrZip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new HdlssException(Tx.Get("nra.err.notSource"));
                temp = Path.Combine(paths.Temp, "nr-import-" + Guid.NewGuid().ToString("N"));
                try { Archives.Extract(folderOrZip, temp); }
                catch (InvalidDataException) { throw new HdlssException(Tx.Get("nra.err.badZip")); }
                source = temp;
            }
            else if (Directory.Exists(folderOrZip)) source = folderOrZip;
            else throw new HdlssException(Tx.Get("nra.err.missing", folderOrZip));

            var root = FindRoot(source) ?? throw new HdlssException(Tx.Get("nra.err.noAddon", NrAddonPackage.Addon64));
            var version = Check(root, out var problem) ?? throw new HdlssException(problem!);

            var existing = Packages.FirstOrDefault(p => p.Version == version);
            if (existing != null) return existing; // same version again: nothing to do

            var target = Path.Combine(Root, NrAddonInfo.Format(version));
            Directory.CreateDirectory(Root);
            var staging = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                CopyTree(root, staging);
                if (Directory.Exists(target)) Directory.Delete(target, true); // leftover, not a valid package
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            lock (_gate) _cache = null;
            var package = new NrAddonPackage(version, "Lecram " + NrAddonInfo.Format(version), target);
            log.Info($"Importato DLSS5 NR {package.Label}");
            return package;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HdlssException(Tx.Get("nra.err.io", ex.Message), ex);
        }
        finally
        {
            if (temp != null) try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The folder holding dlss5-nr.addon64: the folder itself or one inside it (zip with a top folder).</summary>
    private static string? FindRoot(string folder)
    {
        // The user picked dlss5-nr-host itself (its addon64): the package is the parent folder.
        if (string.Equals(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), NrAddonPackage.HostDir, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { } parent) folder = parent;
        if (File.Exists(Path.Combine(folder, NrAddonPackage.Addon64))) return folder;
        foreach (var dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            if (File.Exists(Path.Combine(dir, NrAddonPackage.Addon64))) return dir;
        return null;
    }

    private static bool Validate(string dir, out NrAddonPackage? package, out string? problem)
    {
        package = null;
        var version = Check(dir, out problem);
        if (version == null) return false;
        package = new NrAddonPackage(version, "Lecram " + NrAddonInfo.Format(version), dir);
        return true;
    }

    /// <summary>The add-on version when the folder is a complete DLSS5 NR package, otherwise null and why.</summary>
    private static Version? Check(string dir, out string? problem)
    {
        problem = null;
        var addon = Path.Combine(dir, NrAddonPackage.Addon64);
        if (!File.Exists(addon)) { problem = Tx.Get("nra.err.noAddon", NrAddonPackage.Addon64); return null; }
        if (!PeFile.IsPe64(addon)) { problem = Tx.Get("nra.err.notX64", NrAddonPackage.Addon64); return null; }
        var version = NrAddonInfo.Read(addon);
        if (version == null) { problem = Tx.Get("nra.err.product", NrAddonPackage.Addon64, NrAddonInfo.ProductName); return null; }
        foreach (var name in NrAddonPackage.HostFiles)
            if (!File.Exists(Path.Combine(dir, NrAddonPackage.HostDir, name)))
            { problem = Tx.Get("nra.err.noHost", NrAddonPackage.HostDir + "\\" + name); return null; }
        return version;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var dest = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo d) CopyTree(d.FullName, dest);
            else File.Copy(entry.FullName, dest);
        }
    }
}
