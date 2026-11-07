using Hdlss.Core.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Hdlss.Core.Games;

/// <summary>
/// Xbox app / Game Pass titles: Gaming Services registry roots, XboxGames folders and
/// .GamingRoot markers on every drive. Manifests are parsed with DTDs and resolvers disabled.
/// </summary>
public static partial class XboxLibrary
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder buffer, uint size, uint flags);

    [GeneratedRegex(@"^[A-Za-z0-9.\-_]+![A-Za-z0-9.\-_]+$")] private static partial Regex LaunchIdPattern();

    public static List<Game> Scan(IEnumerable<string> extraLibraries, Log log)
    {
        var entries = new List<(string Root, string Package)>();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root");
            RegistryRoots(key, entries, 0);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Warn("Xbox: registro Gaming Services non leggibile", ex);
        }

        var libraries = new HashSet<string>(extraLibraries, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network) continue;
                libraries.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
                var marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
                if (!File.Exists(marker)) continue;
                var b = File.ReadAllBytes(marker);
                if (b.Length <= 8 || Encoding.ASCII.GetString(b, 0, 4) != "RGBX") continue;
                var rel = Encoding.Unicode.GetString(b, 8, b.Length - 8).TrimEnd('\0');
                var path = Path.GetFullPath(Path.Combine(drive.RootDirectory.FullName, rel));
                if (PathGuard.IsUnder(path, drive.RootDirectory.FullName)) libraries.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log.Debug($"Xbox: unità {drive.Name} ignorata ({ex.Message})");
            }
        }
        foreach (var lib in libraries.Where(Directory.Exists))
        {
            entries.Add((lib, ""));
            foreach (var dir in ExecutableDetector.SafeDirs(lib))
                entries.Add((Directory.Exists(Path.Combine(dir, "Content")) ? Path.Combine(dir, "Content") : dir, ""));
        }

        // Package full names from Gaming Services, by identity name (needed for shell:AppsFolder launch).
        var packages = entries.Where(e => e.Package.Length > 0).GroupBy(e => e.Package.Split('_')[0], StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.First().Package, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<Game>();
        foreach (var (rawRoot, package) in entries)
        {
            try
            {
                var root = FinalPath(rawRoot).TrimEnd('\\');
                if (!Directory.Exists(root) || !seen.Add(root)) continue;
                var game = Parse(root, package, packages);
                if (game != null) games.Add(game);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                log.Warn($"Xbox: {rawRoot}", ex);
            }
        }
        // The same title can appear as protected WindowsApps root and as a writable XboxGames copy.
        return games.GroupBy(g => g.Id).Select(g => g.OrderBy(x => x.ProtectedReason != null).First()).ToList();
    }

    public static Game? Parse(string root, string package, IReadOnlyDictionary<string, string>? packages = null)
    {
        root = Path.GetFullPath(root);
        var appx = Path.Combine(root, "AppxManifest.xml");
        var gdk = Path.Combine(root, "MicrosoftGame.config");
        var manifest = File.Exists(appx) ? ReadXml(appx) : null;
        var config = File.Exists(gdk) ? ReadXml(gdk) : null;
        if (manifest == null && config == null) return null;

        var identity = (manifest ?? config)!.SelectSingleNode("//*[local-name()='Identity']");
        var name = Attr(identity, "Name");
        if (name.Length == 0) return null;
        var family = "";
        if (string.IsNullOrEmpty(package) && packages != null && packages.TryGetValue(name, out var known)) package = known;
        if (!string.IsNullOrEmpty(package))
        {
            int i = package.LastIndexOf('_');
            if (package.StartsWith(name + "_", StringComparison.Ordinal) && i > name.Length) family = name + "_" + package[(i + 1)..];
        }
        var app = manifest?.SelectSingleNode("//*[local-name()='Applications']/*[local-name()='Application']");
        var visual = app?.SelectSingleNode("*[local-name()='VisualElements']");
        var shell = config?.SelectSingleNode("//*[local-name()='ShellVisuals']");
        var title = Attr(shell, "DefaultDisplayName");
        if (title.Length == 0) title = Attr(visual, "DisplayName");
        if (title.Length == 0 && manifest?.SelectSingleNode("//*[local-name()='Properties']/*[local-name()='DisplayName']") is { } n) title = n.InnerText;
        if (title.Length == 0 || title.StartsWith("ms-resource:"))
            title = Path.GetFileName(root) == "Content" ? Directory.GetParent(root)!.Name : name;

        return new Game
        {
            Id = "xbox-" + (family.Length == 0 ? name : family),
            Name = title,
            Root = root,
            Store = GameStore.Xbox,
            LaunchId = family.Length == 0 || app == null ? null : family + "!" + Attr(app, "Id"),
            Cover = BestCover(root, shell, visual),
            Hero = Asset(root, Attr(shell, "SplashScreenImage")),
            ProtectedReason = root.Split('\\').Any(x => x.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
                ? Tx.Get("xbox.protected")
                : null,
        };
    }

    public static string LaunchArgument(Game game)
    {
        if (!LaunchIdPattern().IsMatch(game.LaunchId ?? ""))
            throw new HdlssException(Tx.Get("xbox.noLaunch"));
        return @"shell:AppsFolder\" + game.LaunchId;
    }

    /// <summary>
    /// Some Game Pass executables deny read access; accept them as x64 only when the package
    /// manifest declares x64 and MicrosoftGame.config lists that executable.
    /// </summary>
    public static bool IsDeclaredX64(Game game, string file)
    {
        if (game.Store != GameStore.Xbox || !PathGuard.IsUnder(file, game.Root)) return false;
        try
        {
            var manifest = ReadXml(Path.Combine(game.Root, "AppxManifest.xml"));
            var identity = manifest.SelectSingleNode("//*[local-name()='Identity']");
            if (!Attr(identity, "ProcessorArchitecture").Equals("x64", StringComparison.OrdinalIgnoreCase)) return false;
            var config = ReadXml(Path.Combine(game.Root, "MicrosoftGame.config"));
            if (Attr(config.SelectSingleNode("//*[local-name()='Identity']"), "Name") != Attr(identity, "Name")) return false;
            foreach (XmlNode exe in config.SelectNodes("//*[local-name()='ExecutableList']/*[local-name()='Executable']")!)
            {
                var declared = Attr(exe, "Name");
                if (declared.Length == 0 || Path.IsPathRooted(declared) || Attr(exe, "IsDevOnly").Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(PathGuard.Resolve(game.Root, declared), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) return true;
                // GDK lists the Unreal bootstrap exe, not the protected Win64 Shipping target.
                var rel = Path.GetRelativePath(game.Root, file).Split('\\');
                if (!declared.Contains('\\') && !declared.Contains('/') && rel.Length == 4 && rel[1].Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                    && rel[2].Equals("Win64", StringComparison.OrdinalIgnoreCase)
                    && rel[3].Equals(Path.GetFileNameWithoutExtension(declared) + "-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or UnsafePathException) { }
        return false;
    }

    private static void RegistryRoots(RegistryKey? key, List<(string, string)> entries, int depth)
    {
        if (key == null || depth > 4) return;
        if (key.GetValue("Root") is string root && root.Length > 0) entries.Add((root, key.GetValue("Package") as string ?? ""));
        foreach (var name in key.GetSubKeyNames())
        {
            try { using var child = key.OpenSubKey(name); RegistryRoots(child, entries, depth + 1); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
    }

    private static string FinalPath(string path)
    {
        if (path.StartsWith(@"\\?\")) path = path[4..];
        using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            if (!handle.IsInvalid)
            {
                var buffer = new StringBuilder(32768);
                uint n = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (n > 0 && n < buffer.Capacity) path = buffer.ToString();
            }
        }
        return path.StartsWith(@"\\?\") ? path[4..] : path;
    }

    private static XmlDocument ReadXml(string path)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        doc.Load(reader);
        return doc;
    }

    private static string Attr(XmlNode? node, string name) => node?.Attributes?[name]?.Value ?? "";

    private static string? Asset(string root, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative)) return null;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!PathGuard.IsUnder(path, root)) return null;
        return ExecutableDetector.SafeFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "*" + Path.GetExtension(path))
            .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    /// <summary>Highest-resolution artwork, preferring portrait images (compares pixels, not file size).</summary>
    private static string? BestCover(string root, XmlNode? shell, XmlNode? visual)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in new[] { shell, visual })
            foreach (var key in new[] { "Square150x150Logo", "Square310x310Logo", "SplashScreenImage", "StoreLogo" })
            {
                var asset = Asset(root, Attr(node, key));
                if (asset == null) continue;
                paths.Add(asset);
                foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg" })
                    foreach (var f in ExecutableDetector.SafeFiles(Path.GetDirectoryName(asset)!, ext)) paths.Add(f);
            }
        string? best = null; double bestScore = 0;
        foreach (var file in paths)
        {
            var size = ImageSize(file);
            if (size == null) continue;
            double w = size.Value.Width, h = size.Value.Height;
            double score = Math.Min(Math.Min(h, w / (75.0 / 112.0)), 600) * (w / h < .95 ? 1.6 : 1);
            if (score > bestScore) { bestScore = score; best = file; }
        }
        return best;
    }

    /// <summary>Reads PNG/JPEG dimensions from the header without decoding the image.</summary>
    internal static (int Width, int Height)? ImageSize(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var head = new byte[26];
            if (s.Read(head, 0, 26) < 24) return null;
            if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return (BinaryPrimitivesBE(head, 16), BinaryPrimitivesBE(head, 20));
            if (head[0] != 0xFF || head[1] != 0xD8) return null;
            s.Position = 2;
            var r = new BinaryReader(s);
            while (s.Position < s.Length - 8)
            {
                if (r.ReadByte() != 0xFF) continue;
                byte marker = r.ReadByte();
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                int length = (r.ReadByte() << 8) | r.ReadByte();
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    r.ReadByte();
                    int h = (r.ReadByte() << 8) | r.ReadByte();
                    int w = (r.ReadByte() << 8) | r.ReadByte();
                    return (w, h);
                }
                s.Position += length - 2;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException) { }
        return null;
    }

    private static int BinaryPrimitivesBE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
