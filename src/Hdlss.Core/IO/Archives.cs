using Hdlss.Core.Text;
using System.Diagnostics;
using System.IO.Compression;

namespace Hdlss.Core.IO;

/// <summary>Bounded, path-safe archive extraction (zip natively, 7z via Windows tar.exe).</summary>
public static class Archives
{
    public const long DefaultEntryLimit = 512L << 20;
    public const long DefaultTotalLimit = 2L << 30;

    /// <summary>Extracts every regular entry accepted by <paramref name="filter"/> into <paramref name="destination"/>.</summary>
    public static List<string> Extract(string archive, string destination, Func<string, bool>? filter = null,
        long entryLimit = DefaultEntryLimit, long totalLimit = DefaultTotalLimit)
    {
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new HdlssException(Tx.Get("arc.destNotEmpty"));
        return archive.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            ? ExtractWithTar(archive, destination, filter, entryLimit, totalLimit)
            : ExtractZip(File.OpenRead(archive), destination, filter, entryLimit, totalLimit);
    }

    public static List<string> ExtractZip(Stream stream, string destination, Func<string, bool>? filter, long entryLimit, long totalLimit)
    {
        var written = new List<string>();
        long total = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 5000) throw new HdlssException(Tx.Get("arc.tooMany"));
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || entry.Length == 0 && name.EndsWith('/')) continue;
            if (filter != null && !filter(name)) continue;
            if (entry.Length > entryLimit || (total += entry.Length) > totalLimit) throw new HdlssException(Tx.Get("arc.entryTooBig", name));
            var target = PathGuard.Resolve(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = File.Create(target))
                CopyBounded(input, output, entryLimit);
            written.Add(PathGuard.Normalize(name));
        }
        return written;
    }

    /// <summary>Reads a ZIP appended to a self-extracting executable (ReShade setup) without running it.</summary>
    public static Stream OpenAppendedZip(string exe, long maxBytes = 100L << 20)
    {
        var bytes = File.ReadAllBytes(exe);
        if (bytes.Length > maxBytes) throw new HdlssException(Tx.Get("arc.tooBig"));
        if (bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z') return new MemoryStream(bytes, writable: false);
        for (int i = bytes.Length - 22; i >= 0; i--)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B || bytes[i + 2] != 5 || bytes[i + 3] != 6) continue;
            long offset = i - (long)BitConverter.ToUInt32(bytes, i + 12) - BitConverter.ToUInt32(bytes, i + 16);
            if (offset >= 0 && offset < bytes.Length - 4 && bytes[offset] == 0x50 && bytes[offset + 1] == 0x4B && bytes[offset + 2] == 3 && bytes[offset + 3] == 4)
                return new MemoryStream(bytes, (int)offset, bytes.Length - (int)offset, writable: false);
        }
        throw new HdlssException(Tx.Get("arc.notFound"));
    }

    private static List<string> ExtractWithTar(string archive, string destination, Func<string, bool>? filter, long entryLimit, long totalLimit)
    {
        var listing = RunTar($"-tvf \"{archive}\"", 4 << 20);
        var names = new List<string>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line[0] == 'd') continue;
            if (line[0] != '-') throw new HdlssException(Tx.Get("arc.links"));
        }
        foreach (var raw in RunTar($"-tf \"{archive}\"", 4 << 20).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.StartsWith("./") ? raw[2..] : raw;
            if (name.EndsWith('/') || name.Length == 0) continue;
            PathGuard.Normalize(name);
            if (filter == null || filter(name)) names.Add(name);
        }
        if (names.Count > 5000) throw new HdlssException(Tx.Get("arc.tooMany"));
        long total = 0;
        foreach (var name in names)
        {
            var target = PathGuard.Resolve(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
            total += TarEntry(archive, name, output, Math.Min(entryLimit, totalLimit - total));
        }
        return names.Select(PathGuard.Normalize).ToList();
    }

    private static string TarExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    private static string RunTar(string args, long limit)
    {
        using var output = new MemoryStream();
        RunTar(args, output, limit);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static long TarEntry(string archive, string name, Stream output, long limit) =>
        RunTar($"-xOf \"{archive}\" -- \"{name}\"", output, limit);

    private static long RunTar(string args, Stream output, long limit)
    {
        if (args.Count(c => c == '"') % 2 != 0) throw new HdlssException(Tx.Get("arc.badPath"));
        var psi = new ProcessStartInfo(TarExe, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new HdlssException(Tx.Get("arc.noTar"));
        var error = p.StandardError.ReadToEndAsync();
        using var timer = new Timer(_ => { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } }, null, 120_000, Timeout.Infinite);
        long copied = CopyBounded(p.StandardOutput.BaseStream, output, limit);
        p.WaitForExit();
        if (p.ExitCode != 0) throw new HdlssException(Tx.Get("arc.extractFailed", error.Result.Trim()));
        return copied;
    }

    private static long CopyBounded(Stream input, Stream output, long limit)
    {
        var buffer = new byte[1 << 17];
        long total = 0; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit) throw new HdlssException(Tx.Get("arc.entryOver"));
            output.Write(buffer, 0, read);
        }
        return total;
    }
}
