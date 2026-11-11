using System.Text;

namespace Hdlss.Core.Tests;

/// <summary>Smallest valid x64 PE with a version resource (ProductName / FileVersion), for tests.</summary>
public static class FakePe
{
    public static byte[] Build(string productName, string fileVersion, string? marker = null)
    {
        var rsrc = Resource(productName, fileVersion);
        int rawSize = (rsrc.Length + 0x1FF) & ~0x1FF;
        var file = new byte[0x200 + rawSize + (marker?.Length ?? 0)];
        var w = new BinaryWriter(new MemoryStream(file));
        w.Write((ushort)0x5A4D);
        w.Seek(60, SeekOrigin.Begin); w.Write(64);
        w.Seek(64, SeekOrigin.Begin);
        w.Write(0x00004550u);
        w.Write((ushort)0x8664); w.Write((ushort)1); w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)240); w.Write((ushort)0x2022);
        long opt = w.BaseStream.Position;
        w.Write((ushort)0x20B);
        w.Seek((int)opt + 32, SeekOrigin.Begin); w.Write(0x1000); w.Write(0x200);          // alignments
        w.Seek((int)opt + 48, SeekOrigin.Begin); w.Write((ushort)6); w.Write((ushort)0);   // subsystem version
        w.Seek((int)opt + 56, SeekOrigin.Begin); w.Write(0x1000 + ((rsrc.Length + 0xFFF) & ~0xFFF)); w.Write(0x200);
        w.Seek((int)opt + 68, SeekOrigin.Begin); w.Write((ushort)3);
        w.Seek((int)opt + 108, SeekOrigin.Begin); w.Write(16);
        w.Seek((int)opt + 112 + 2 * 8, SeekOrigin.Begin); w.Write(0x1000); w.Write(rsrc.Length);
        w.Seek((int)opt + 240, SeekOrigin.Begin);
        w.Write(Encoding.ASCII.GetBytes(".rsrc\0\0\0")); w.Write(rsrc.Length); w.Write(0x1000); w.Write(rawSize); w.Write(0x200);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0x40000040);
        w.Seek(0x200, SeekOrigin.Begin); w.Write(rsrc);
        w.Flush();
        if (marker != null) Encoding.ASCII.GetBytes(marker).CopyTo(file, 0x200 + rawSize);
        return file;
    }

    private static byte[] Resource(string product, string version)
    {
        var info = VersionInfo(product, version);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Dir(int idEntries) { w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)0); w.Write((ushort)idEntries); }
        Dir(1); w.Write(16); w.Write(0x80000000u | 24);
        Dir(1); w.Write(1); w.Write(0x80000000u | 48);
        Dir(1); w.Write(0x409); w.Write(72);
        w.Write(0x1000 + 88); w.Write(info.Length); w.Write(0); w.Write(0);
        w.Write(info);
        return ms.ToArray();
    }

    private static byte[] VersionInfo(string product, string version)
    {
        var parts = version.Split('.').Select(p => ushort.Parse(p)).Concat(new ushort[] { 0, 0, 0, 0 }).Take(4).ToArray();
        var fixedInfo = new MemoryStream();
        var f = new BinaryWriter(fixedInfo);
        f.Write(0xFEEF04BDu); f.Write(0x10000);
        f.Write((uint)(parts[0] << 16 | parts[1])); f.Write((uint)(parts[2] << 16 | parts[3]));
        f.Write((uint)(parts[0] << 16 | parts[1])); f.Write((uint)(parts[2] << 16 | parts[3]));
        for (int i = 0; i < 7; i++) f.Write(0);
        var strings = new[]
        {
            Str("ProductName", product), Str("FileVersion", version), Str("ProductVersion", version), Str("FileDescription", product),
        };
        var table = Block("040904B0", 0, 1, [], strings);
        var fileInfo = Block("StringFileInfo", 0, 1, [], [table]);
        return Block("VS_VERSION_INFO", 52, 0, fixedInfo.ToArray(), [fileInfo]);
    }

    private static byte[] Str(string key, string value)
    {
        var data = Encoding.Unicode.GetBytes(value + "\0");
        return Block(key, data.Length / 2, 1, data, []);
    }

    private static byte[] Block(string key, int valueLength, ushort type, byte[] value, byte[][] children)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)valueLength); w.Write(type);
        w.Write(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(ms);
        w.Write(value);
        Pad(ms);
        foreach (var child in children) { w.Write(child); Pad(ms); }
        var bytes = ms.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }

    private static void Pad(MemoryStream ms) { while (ms.Length % 4 != 0) ms.WriteByte(0); }
}
