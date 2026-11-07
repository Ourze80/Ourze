using Hdlss.Core.Text;
using Hdlss.Core.IO;

namespace Hdlss.Core.Storage;

/// <summary>
/// Content-addressed blob store (<c>store/ab/abcdef…</c>). Every original file the app
/// replaces and every payload it installs is stored exactly once, keyed by SHA-256.
/// Ten installs of the same 270 MB model cost 270 MB, not 2.7 GB.
/// </summary>
public sealed class ContentStore(string root)
{
    public string Root { get; } = root;

    public string PathOf(string hash)
    {
        if (!FileHash.IsSha256(hash)) throw new ArgumentException("Hash SHA-256 non valido.", nameof(hash));
        hash = hash.ToLowerInvariant();
        return Path.Combine(Root, hash[..2], hash);
    }

    public bool Contains(string hash) => File.Exists(PathOf(hash));

    /// <summary>Copies a file into the store and returns its hash (no-op if already present).</summary>
    public string Put(string file)
    {
        var hash = FileHash.OfFile(file);
        var target = PathOf(hash);
        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) return hash;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.Copy(file, temp, overwrite: true);
        if (!FileHash.Equal(FileHash.OfFile(temp), hash))
        {
            File.Delete(temp);
            throw new IOException(Tx.Get("store.changedDuringCopy", file));
        }
        File.Move(temp, target, overwrite: true);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        return hash;
    }

    public string Put(byte[] data)
    {
        var hash = FileHash.OfBytes(data);
        var target = PathOf(hash);
        if (File.Exists(target)) return hash;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, target, overwrite: true);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        return hash;
    }

    /// <summary>Returns the blob path after re-verifying its content.</summary>
    public string Open(string hash)
    {
        var path = PathOf(hash);
        if (!File.Exists(path)) throw new HdlssException(Tx.Get("store.backupMissing", hash[..12]));
        if (!FileHash.Equal(FileHash.OfFile(path), hash)) throw new HdlssException(Tx.Get("store.backupCorrupt", hash[..12]));
        return path;
    }

    public IEnumerable<string> AllHashes()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(Root))
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (FileHash.IsSha256(name)) yield return name;
            }
    }

    /// <summary>Deletes every blob not in <paramref name="keep"/>. Returns (files, bytes) freed.</summary>
    public (int Files, long Bytes) Collect(IReadOnlySet<string> keep)
    {
        int files = 0; long bytes = 0;
        foreach (var hash in AllHashes().ToList())
        {
            if (keep.Contains(hash)) continue;
            var path = PathOf(hash);
            var size = new FileInfo(path).Length;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            files++; bytes += size;
        }
        foreach (var tmp in Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*.tmp", SearchOption.AllDirectories).ToList() : [])
            File.Delete(tmp);
        return (files, bytes);
    }

    public long SizeBytes() =>
        Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
}
