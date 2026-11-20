namespace Jellyfin.Plugin.VFQ.Tests;

public class VfqTrackMatcherTests
{
    [Theory]
    [InlineData("VFQ")]
    [InlineData("Français (VFQ) 5.1")]
    [InlineData("vfq ac3")]
    [InlineData("French Canadian")]
    [InlineData("Canadian French")]
    [InlineData("French (Canada)")]
    [InlineData("Français Canada")]
    [InlineData("Français canadien")]
    [InlineData("Québécois")]
    [InlineData("fr-CA")]
    [InlineData("fra_CA")]
    [InlineData("FRE.CA")]
    [InlineData("Français QC")]
    public void MatchesVfqTitles(string title)
    {
        Assert.True(VfqTrackMatcher.IsVfqTrack(title, null, null));
    }

    [Theory]
    [InlineData("French")]
    [InlineData("Français")]
    [InlineData("VFF")]
    [InlineData("Français (France)")]
    [InlineData("English")]
    [InlineData("fre")]
    [InlineData("fr")]
    [InlineData("QCM")]
    [InlineData("AVFQ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- ()")]
    [InlineData(null)]
    public void RejectsNonVfqTitles(string? title)
    {
        Assert.False(VfqTrackMatcher.IsVfqTrack(title, null, null));
    }

    [Fact]
    public void MatchesOnAnyField()
    {
        Assert.True(VfqTrackMatcher.IsVfqTrack(null, "VFQ - AC3 5.1", null));
        Assert.True(VfqTrackMatcher.IsVfqTrack("Track 2", "French", "fr-CA"));
        Assert.False(VfqTrackMatcher.IsVfqTrack("Track 2", "French", "fre"));
    }

    [Theory]
    [InlineData("truehd", null, 6)]
    [InlineData("dts", "DTS-HD MA", 5)]
    [InlineData("dts", "dts-hd", 5)]
    [InlineData("flac", null, 4)]
    [InlineData("EAC3", null, 3)]
    [InlineData("dts", null, 2)]
    [InlineData("dts", "DTS", 2)]
    [InlineData("ac3", "", 1)]
    [InlineData("aac", "LC", 0)]
    [InlineData("opus", null, -1)]
    [InlineData(null, null, -1)]
    [InlineData("", "", -1)]
    public void RanksCodecs(string? codec, string? profile, int expected)
    {
        Assert.Equal(expected, VfqTrackMatcher.CodecRank(codec, profile));
    }
}
