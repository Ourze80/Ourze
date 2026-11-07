using Hdlss.Core.Components;
using Xunit;

namespace Hdlss.Core.Tests;

public class NrCostReaderTests
{
    private const string Real = "19:35:58:629 [12672] | INFO  | [DLSS 5 Neural Rendering] DLSS5 Generic: NR cost: gpu_ms=4.36 frame_ms=19.37 share=0.225 present_ms=4.36 | NR: 4.4 ms of GPU time per frame (estimate: 67 fps without NR, 52 fps with it)";

    [Fact]
    public void Parses_real_line()
    {
        var c = NrCostReader.Parse("noise\n" + Real + "\nmore noise");
        Assert.NotNull(c);
        Assert.Equal(4.36, c!.GpuMs, 3);
        Assert.Equal(67, c.FpsWithout);
        Assert.Equal(52, c.FpsWith);
        Assert.True(c.HasFps);
    }

    [Fact]
    public void Takes_last_line()
    {
        var later = Real.Replace("gpu_ms=4.36", "gpu_ms=3.94").Replace("67 fps", "101 fps").Replace("52 fps", "84 fps");
        var c = NrCostReader.Parse(Real + "\n" + later + "\n");
        Assert.Equal(3.94, c!.GpuMs, 3);
        Assert.Equal(84, c.FpsWith);
    }

    [Fact]
    public void Line_without_estimates_has_no_fps()
    {
        var c = NrCostReader.Parse("x | NR cost: gpu_ms=2.50 frame_ms=10 share=0.2\n");
        Assert.NotNull(c);
        Assert.Equal(2.5, c!.GpuMs, 3);
        Assert.False(c.HasFps);
    }

    [Fact]
    public void Garbage_gives_null()
    {
        Assert.Null(NrCostReader.Parse("NR cost: nothing useful here\nrandom\n"));
        Assert.Null(NrCostReader.Parse(""));
    }

    [Fact]
    public void Missing_file_gives_null()
    {
        Assert.Null(NrCostReader.Read(Path.Combine(Path.GetTempPath(), "hdlss-nonexistent", "ReShade.log")));
    }

    [Fact]
    public void Reads_file_tail()
    {
        using var fx = new Fixture();
        var path = Path.Combine(fx.Game, "ReShade.log");
        File.WriteAllText(path, Real.Replace("gpu_ms=4.36", "gpu_ms=9.99") + "\n" + new string('x', 400_000) + "\n" + Real + "\n");
        var c = NrCostReader.Read(path);
        Assert.Equal(4.36, c!.GpuMs, 3);
    }
}
