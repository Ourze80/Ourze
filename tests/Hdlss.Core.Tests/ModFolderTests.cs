using Hdlss.Core.Games;
using Xunit;

namespace Hdlss.Core.Tests;

public class ModFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hdlss-modfolder-" + Guid.NewGuid().ToString("N")[..8]);

    public ModFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(params string[] parts) { var d = Path.Combine([_root, .. parts]); Directory.CreateDirectory(d); return d; }

    private static string File(string path, long size)
    {
        using var f = new FileStream(path, FileMode.Create);
        f.SetLength(size);
        return path;
    }

    [Fact]
    public void LauncherStubOutsideBinaries_UsesProjectBinaries()
    {
        // platform\steam\Game.exe is a stub that starts Game\Binaries\Win64\Game-Win64-Shipping.exe
        var exe = File(Path.Combine(Dir("platform", "steam"), "Game.exe"), 1_000);
        Dir("Game", "Content", "Paks");
        var bin = Dir("Game", "Binaries", "Win64");
        File(Path.Combine(bin, "Game-Win64-Shipping.exe"), 50_000);
        Dir("Engine", "Binaries", "Win64");
        Assert.Equal(bin, ExecutableDetector.ModFolderFor(_root, exe));
    }

    [Fact]
    public void GameExeOutsideBinaries_KeepsExeFolder()
    {
        // Phantom Blade Zero: platform\steam\PBZ.exe is the game; ProjectPBZ\Binaries\Win64 only has tools
        var exe = File(Path.Combine(Dir("platform", "steam"), "PBZ.exe"), 50_000);
        File(Path.Combine(Dir("steam"), "PBZ.exe"), 1_000);
        Dir("ProjectPBZ", "Content", "Paks");
        var bin = Dir("ProjectPBZ", "Binaries", "Win64");
        File(Path.Combine(bin, "8za_v2.exe"), 5_000);
        File(Path.Combine(bin, "CheckEnv.exe"), 100);
        Dir("Engine", "Binaries", "Win64");
        Assert.Null(ExecutableDetector.ModFolderFor(_root, exe));
        Assert.Equal(Path.Combine(_root, "ProjectPBZ"), ExecutableDetector.UnrealProject(_root));
    }

    [Fact]
    public void Pick_is_certain_when_one_exe_dwarfs_the_others()
    {
        var game = File(Path.Combine(Dir("platform", "steam"), "PBZ.exe"), 30L * 1024 * 1024);
        File(Path.Combine(Dir("steam"), "PBZ.exe"), 1024 * 1024);
        Assert.Equal((game, true), ExecutableDetector.Pick(_root, _ => true));
    }

    [Fact]
    public void Pick_by_size_is_a_guess_when_sizes_are_close()
    {
        var big = File(Path.Combine(Dir("a"), "Game.exe"), 30L * 1024 * 1024);
        File(Path.Combine(Dir("b"), "GameDX11.exe"), 25L * 1024 * 1024);
        Assert.Equal((big, false), ExecutableDetector.Pick(_root, _ => true));
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
