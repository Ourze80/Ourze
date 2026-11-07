using Hdlss.Core.Components;
using Xunit;

namespace Hdlss.Core.Tests;

public class RenoDxUpdateTests
{
    private const string Name = "renodx-game.addon64";
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly DateTime Old = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_hash_is_up_to_date()
    {
        var r = RenoDxHdrComponent.CompareAsset(Name, ShaA, Old.ToString("o"), Name, ShaA, New);
        Assert.Equal(VersionVerdict.UpToDate, r!.Verdict);
    }

    [Fact]
    public void Same_hash_without_recorded_date_is_up_to_date()
    {
        var r = RenoDxHdrComponent.CompareAsset(Name, ShaA, null, Name, ShaA, New);
        Assert.Equal(VersionVerdict.UpToDate, r!.Verdict);
    }

    [Fact]
    public void Different_hash_is_newer_with_dates_and_no_false_downgrade()
    {
        var r = RenoDxHdrComponent.CompareAsset(Name, ShaA, Old.ToString("o"), Name, ShaB, New)!;
        Assert.Equal(VersionVerdict.Newer, r.Verdict);
        Assert.Equal("2026-10-12", r.Present);
        Assert.Equal("2026-11-02", r.Latest);
        Assert.False(VersionCompare.IsDowngrade(r.Present, r.Latest));
    }

    [Fact]
    public void Different_hash_without_date_uses_short_hashes()
    {
        var r = RenoDxHdrComponent.CompareAsset("RENODX-GAME.addon64", ShaA, null, Name, ShaB, New)!;
        Assert.Equal(VersionVerdict.Newer, r.Verdict);
        Assert.Equal("aaaaaaaa", r.Present);
        Assert.Equal("bbbbbbbb", r.Latest);
        Assert.False(VersionCompare.IsDowngrade(r.Present, r.Latest));
    }

    [Fact]
    public void No_asset_hash_compares_recorded_date()
    {
        Assert.Equal(VersionVerdict.Newer, RenoDxHdrComponent.CompareAsset(Name, ShaA, Old.ToString("o"), Name, null, New)!.Verdict);
        Assert.Equal(VersionVerdict.UpToDate, RenoDxHdrComponent.CompareAsset(Name, ShaA, New.ToString("o"), Name, null, New)!.Verdict);
    }

    [Fact]
    public void No_asset_hash_and_no_recorded_date_is_unknown() =>
        Assert.Null(RenoDxHdrComponent.CompareAsset(Name, ShaA, null, Name, null, New));

    [Fact]
    public void Name_mismatch_gives_no_claim()
    {
        Assert.Null(RenoDxHdrComponent.CompareAsset("renodx-other.addon64", ShaA, null, Name, ShaB, New));
        Assert.Null(RenoDxHdrComponent.CompareAsset(null, ShaA, null, Name, ShaB, New));
    }

    [Fact]
    public void Unreadable_installed_file_gives_no_claim() =>
        Assert.Null(RenoDxHdrComponent.CompareAsset(Name, null, Old.ToString("o"), Name, ShaB, New));
}
