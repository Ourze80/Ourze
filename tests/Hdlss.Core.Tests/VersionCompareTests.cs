using Hdlss.Core.Components;
using Xunit;

namespace Hdlss.Core.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("6.6.2", "6.6.2", VersionVerdict.UpToDate)]
    [InlineData("6.6.1", "6.6.2", VersionVerdict.Newer)]
    [InlineData("6.8.0.2155 (da 1.x)", "6.8.0", VersionVerdict.UpToDate)]       // 1.x recorded the file version
    [InlineData("6.8.0.2155 (importato)", "6.8.0", VersionVerdict.UpToDate)]    // imported from another tool
    [InlineData("6.7.3.1000 (importato)", "6.8.0", VersionVerdict.Newer)]
    [InlineData("6.7.0.100 (da 1.x)", "6.8.0", VersionVerdict.Newer)]
    [InlineData("10.0.2 (da 1.x)", "v10.0.2 · dinput8.dll", VersionVerdict.UpToDate)]
    [InlineData("v10.0.1 · dinput8.dll", "v10.0.2 · dinput8.dll", VersionVerdict.Newer)]
    [InlineData("b1ada21b27666aa0703c6f9b9d83c389e92063a1 (da 1.x)", "b1ada21b27", VersionVerdict.UpToDate)]
    [InlineData("c0ffee0000", "b1ada21b27", VersionVerdict.Newer)]
    [InlineData("importato", "v3.0.1", VersionVerdict.Unknown)]
    [InlineData("da 1.x", "Lecram", VersionVerdict.Unknown)]
    [InlineData("Lecram 10.8 · 310.9.16.0 · RTX 4000 · Compat FP16", "Lecram 10.8 · 310.9.16.0 · RTX 4000 · Compat FP16", VersionVerdict.UpToDate)]
    [InlineData("F5 · v1.2.0", "Standard · v1.2.0", VersionVerdict.Newer)]
    [InlineData("Streamline 2.15.0", "Streamline 2.14.16", VersionVerdict.UpToDate)]   // newer than offered: leave it
    [InlineData("v3.0.1-1188-gf885e8d2 (da 1.x)", "v3.0.1", VersionVerdict.UpToDate)]  // dev build after the tag: no downgrade
    [InlineData("v3.0.1-1188-gf885e8d2", "v3.1.0", VersionVerdict.Newer)]
    public void Compares_labels_from_any_source(string installed, string latest, VersionVerdict expected) =>
        Assert.Equal(expected, VersionCompare.Compare(installed, latest));
}
