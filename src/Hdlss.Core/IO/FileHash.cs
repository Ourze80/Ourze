using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Hdlss.Core.IO;

/// <summary>
/// SHA-256 helpers. File hashes are memoized on (path, size, mtime) so repeated checks of
/// large DLLs (the NR model is ~270 MB) cost nothing until the file actually changes.
/// </summary>
public static class FileHash
{
    private sealed record Key(string Path, long Length, DateTime Written);
    private static readonly ConcurrentDictionary<Key, string> Cache = new();

    public static string? OfFileOrNull(string path) => File.Exists(path) ? OfFile(path) : null;

    public static string OfFile(string path)
    {
        var info = new FileInfo(path);
        var key = new Key(info.FullName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Cache[key] = hash;
        return hash;
    }

    /// <summary>Forgets cached hashes of a path. Call after writing it: File.Copy keeps the source mtime.</summary>
    public static void Invalidate(string path)
    {
        var upper = Path.GetFullPath(path).ToUpperInvariant();
        foreach (var key in Cache.Keys.Where(k => k.Path == upper).ToList()) Cache.TryRemove(key, out _);
    }

    public static string OfBytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
