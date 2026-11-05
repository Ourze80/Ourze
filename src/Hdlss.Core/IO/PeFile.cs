using System.Diagnostics;
using System.Text;

namespace Hdlss.Core.IO;

/// <summary>Minimal, bounds-checked PE reader: machine type, imports and version strings.</summary>
public sealed class PeFile
{
    public bool IsPe { get; private init; }
    public bool Is64 { get; private init; }
    public IReadOnlyList<string> Imports { get; private init; } = [];

    public static bool IsPe64(string path) => File.Exists(path) && Read(path, readImports: false).Is64;

    public static PeFile Read(string path, bool readImports = true)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            if (stream.Length < 64 || r.ReadUInt16() != 0x5A4D) return new PeFile();
            stream.Position = 60;
            int pe = r.ReadInt32();
            if (pe < 64 || pe > stream.Length - 24) return new PeFile();
            stream.Position = pe;
            if (r.ReadUInt32() != 0x4550) return new PeFile();
            ushort machine = r.ReadUInt16();
            bool is64 = machine == 0x8664;
            if (!readImports) return new PeFile { IsPe = true, Is64 = is64 };

            int sectionCount = r.ReadUInt16();
            stream.Position = pe + 20;
            int optionalSize = r.ReadUInt16();
            long optional = pe + 24;
            stream.Position = optional;
            int magic = r.ReadUInt16();
            long directories = optional + (magic == 0x20B ? 112 : 96);
            stream.Position = directories + 1 * 8;
            uint importRva = r.ReadUInt32();
            stream.Position = directories + 13 * 8;
            uint delayRva = r.ReadUInt32();

            var sections = new List<(uint Va, uint Size, uint Raw)>();
            stream.Position = optional + optionalSize;
            for (int i = 0; i < Math.Min(sectionCount, 96); i++)
            {
                r.ReadBytes(8);
                uint vsize = r.ReadUInt32(), va = r.ReadUInt32(), rawSize = r.ReadUInt32(), raw = r.ReadUInt32();
                r.ReadBytes(16);
                sections.Add((va, Math.Max(vsize, rawSize), raw));
            }
            long Offset(uint rva)
            {
                foreach (var s in sections)
                    if (rva >= s.Va && rva - s.Va < s.Size) return s.Raw + (rva - s.Va);
                throw new InvalidDataException();
            }

            var imports = new List<string>();
            // Regular imports (20-byte descriptors, name at +12) and delay-load imports (32-byte, name at +4).
            foreach (var (rva, size, nameOffset) in new[] { (importRva, 20, 12), (delayRva, 32, 4) })
            {
                if (rva == 0) continue;
                long table;
                try { table = Offset(rva); } catch (InvalidDataException) { continue; }
                for (int i = 0; i < 1024; i++)
                {
                    stream.Position = table + i * size + nameOffset;
                    uint nameRva = r.ReadUInt32();
                    if (nameRva == 0) break;
                    stream.Position = Offset(nameRva);
                    var bytes = new List<byte>(32);
                    for (int n = 0; n < 256; n++) { byte c = r.ReadByte(); if (c == 0) break; bytes.Add(c); }
                    imports.Add(Encoding.ASCII.GetString(bytes.ToArray()).ToLowerInvariant());
                }
            }
            return new PeFile { IsPe = true, Is64 = is64, Imports = imports };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            return new PeFile();
        }
    }

    /// <summary>"ProductName FileDescription" of a file, or empty.</summary>
    public static string Identity(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return $"{v.ProductName} {v.FileDescription}".Trim();
        }
        catch (FileNotFoundException) { return ""; }
    }

    public static Version? FileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Streaming search for an ASCII marker inside a binary (never loads the file whole).</summary>
    public static bool ContainsAscii(string path, string value)
    {
        var needle = Encoding.ASCII.GetBytes(value);
        if (needle.Length == 0) return true;
        var buffer = new byte[(1 << 16) + needle.Length];
        int carry = 0;
        using var stream = File.OpenRead(path);
        int count;
        while ((count = stream.Read(buffer, carry, 1 << 16)) > 0)
        {
            int total = carry + count;
            if (buffer.AsSpan(0, total).IndexOf(needle) >= 0) return true;
            carry = Math.Min(needle.Length - 1, total);
            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
        }
        return false;
    }
}
