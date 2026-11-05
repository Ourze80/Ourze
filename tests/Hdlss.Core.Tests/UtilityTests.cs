using System.Text;
using Hdlss.Core.Games;
using Hdlss.Core.Hardware;
using Hdlss.Core.IO;
using Hdlss.Core.Legacy;
using Xunit;

namespace Hdlss.Core.Tests;

public class PathGuardTests
{
    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("CON")]
    [InlineData("sub/nul.txt")]
    [InlineData("trailing. ")]
    [InlineData("")]
    public void Rejects_unsafe_paths(string path) =>
        Assert.Throws<UnsafePathException>(() => PathGuard.Resolve(Path.GetTempPath(), path));

    [Fact]
    public void Normalizes_separators() =>
        Assert.Equal("a/b/c.dll", PathGuard.Normalize("a\\b//c.dll"));
}

public class IniDocumentTests
{
    [Fact]
    public void Preserves_comments_order_and_newlines()
    {
        var text = "; comment\r\n[A]\r\nx=1\r\n\r\n[B]\r\ny=2";
        var doc = IniDocument.Parse(text);
        doc.Set("A", "z", "3");
        Assert.Equal("; comment\r\n[A]\r\nx=1\r\nz=3\r\n\r\n[B]\r\ny=2", doc.ToString());
    }

    [Fact]
    public void Lists_handle_escaped_commas()
    {
        var doc = IniDocument.Parse("[ADDON]\nLoadFromDllMain=a,,b,c\n");
        Assert.Equal(["a,b", "c"], IniDocument.SplitList(doc.Get("ADDON", "LoadFromDllMain")));
        Assert.True(doc.ListAdd("ADDON", "LoadFromDllMain", "d"));
        Assert.False(doc.ListAdd("ADDON", "LoadFromDllMain", "D"));
        Assert.Equal("a,,b,c,d", doc.Get("ADDON", "LoadFromDllMain"));
        Assert.True(doc.ListRemove("ADDON", "LoadFromDllMain", "c"));
        Assert.Equal("a,,b,d", doc.Get("ADDON", "LoadFromDllMain"));
    }

    [Fact]
    public void Global_keys_are_written_before_first_section()
    {
        var doc = IniDocument.Parse("[DLSS5_Feed.fx]\nX=1\n");
        doc.Set("", "Techniques", "A@a.fx");
        Assert.StartsWith("Techniques=A@a.fx", doc.ToString());
        Assert.Equal("A@a.fx", doc.Get("", "Techniques"));
    }

    [Fact]
    public void Keeps_utf16_encoding()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[S]\r\nk=v\r\n", Encoding.Unicode);
            var doc = IniDocument.Load(path);
            doc.Set("S", "k", "w");
            var bytes = doc.ToBytes();
            Assert.Equal(0xFF, bytes[0]);
            Assert.Equal(0xFE, bytes[1]);
            Assert.Contains("k=w", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        finally { File.Delete(path); }
    }
}

public class DetectionTests
{
    [Fact]
    public void Parses_nvidia_smi_output()
    {
        var gpu = GpuInfo.Parse("NVIDIA GeForce RTX 4090, 616.80\n");
        Assert.Equal(40, gpu.Series);
        Assert.Equal("616.80", gpu.Driver);
        Assert.False(gpu.DriverTooOld);
        Assert.True(GpuInfo.Parse("NVIDIA GeForce RTX 5080, 610.10").DriverTooOld);
        Assert.Equal(0, GpuInfo.Parse("AMD Radeon").Series);
    }

    [Fact]
    public void Reads_pe_imports_of_a_system_executable()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var pe = PeFile.Read(cmd);
        Assert.True(pe.Is64);
        Assert.NotEmpty(pe.Imports);
        Assert.DoesNotContain("d3d12.dll", pe.Imports);
        ExecutableDetector.DetectApi(cmd, out bool certain);
        Assert.False(certain);
        Assert.False(PeFile.IsPe64(Path.GetTempFileName()));
    }

    [Fact]
    public void Manual_root_expands_unreal_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "hdlss-tests", Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Proj", "Binaries", "Win64", "Proj-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        Directory.CreateDirectory(Path.Combine(root, "Engine"));
        try { Assert.Equal(root, GameLibrary.ManualRoot(exe)); }
        finally { Directory.Delete(root, true); }
    }
}

public class LegacyTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Restores_a_1x_install_from_old_backups()
    {
        var legacyData = Path.Combine(_f.Root, "old-data");
        var backup = Path.Combine(legacyData, "renodx-nr-backups", "abc", "nvngx_dlssnr.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "old-model");
        _f.GameFile("nvngx_dlssnr.dll", "new-model");
        _f.GameFile("renodx-dlss5.addon64", "addon");
        string H(string s) => FileHash.OfBytes(Encoding.UTF8.GetBytes(s));
        var marker = $$"""
            {"Schema":1,"Game":"g1","Folder":{{System.Text.Json.JsonSerializer.Serialize(_f.Game)}},"Build":"RenoDX NR","Release":"Lecram 10.8","PreviousOwner":"Before RenoDX NR",
             "Files":[{"Name":"nvngx_dlssnr.dll","Before":"{{H("old-model")}}","After":"{{H("new-model")}}","Backup":"renodx-nr-backups/abc/nvngx_dlssnr.dll","Attributes":128},
                      {"Name":"renodx-dlss5.addon64","Before":null,"After":"{{H("addon")}}","Backup":"renodx-nr-backups/abc/renodx-dlss5.addon64","Attributes":128}]}
            """;
        _f.GameFile("hdlss-renodx-nr.json", marker);

        var installs = LegacyInstalls.Find(_f.Game, Diagnostics.Log.Null);
        var install = Assert.Single(installs);
        Assert.StartsWith("RenoDX NR (1.x)", install.Title);

        var plan = LegacyInstalls.PlanRestore(install, legacyData, _f.Store, _f.State(), _f.States.Stamp(_f.Game), "Test", force: false);
        _f.Engine.Apply(plan);
        Assert.Equal("old-model", _f.Read("nvngx_dlssnr.dll"));
        Assert.False(File.Exists(_f.GameFile("renodx-dlss5.addon64")));
        Assert.False(File.Exists(_f.GameFile("hdlss-renodx-nr.json")));
        Assert.True(File.Exists(backup)); // the 1.x data folder is never modified
    }
}
