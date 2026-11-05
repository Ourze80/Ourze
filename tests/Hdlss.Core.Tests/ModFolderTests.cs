using Hdlss.Core.Games;
using Xunit;

namespace Hdlss.Core.Tests;

public class ModFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdlss-modfolder-" + Guid.NewGuid().ToString("N")[..8]);

    public ModFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(params string[] parts) { var d = Path.Combine([_root, .. parts]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void ExeOutsideBinaries_UsesProjectBinaries()
    {
        // Phantom Blade Zero layout: platform\steam\PBZ.exe loads DLLs from ProjectPBZ\Binaries\Win64
        var exe = Path.Combine(Dir("platform", "steam"), "PBZ.exe");
        Dir("ProjectPBZ", "Content", "Paks");
        var bin = Dir("ProjectPBZ", "Binaries", "Win64");
        Dir("Engine", "Binaries", "Win64");
        Assert.Equal(bin, ExecutableDetector.ModFolderFor(_root, exe));
    }

    [Fact]
    public void ExeInsideBinaries_KeepsExeFolder()
    {
        Dir("Proj", "Content", "Paks");
        var exe = Path.Combine(Dir("Proj", "Binaries", "Win64"), "Proj-Win64-Shipping.exe");
        Assert.Null(ExecutableDetector.ModFolderFor(_root, exe));
    }

    [Fact]
    public void NotUnreal_KeepsExeFolder()
    {
        var exe = Path.Combine(Dir("bin"), "game.exe");
        Dir("Tools", "Binaries", "Win64"); // no Content\Paks: not an Unreal project
        Assert.Null(ExecutableDetector.ModFolderFor(_root, exe));
    }

    [Theory]
    [InlineData("8za_v2.exe", true)]
    [InlineData("7z.exe", true)]
    [InlineData("CheckEnv.exe", true)]
    [InlineData("crashpad_handler.exe", true)]
    [InlineData("EpicWebHelper.exe", true)]
    [InlineData("PBZ.exe", false)]
    [InlineData("Game-Win64-Shipping.exe", false)]
    public void ServiceExecutables(string name, bool service) =>
        Assert.Equal(service, ExecutableDetector.IsServiceExecutable(name));
}
