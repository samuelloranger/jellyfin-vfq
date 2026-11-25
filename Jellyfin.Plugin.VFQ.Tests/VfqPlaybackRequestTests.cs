namespace Jellyfin.Plugin.VFQ.Tests;

public class VfqPlaybackRequestTests
{
    [Fact]
    public void EmptyRequestLeavesTheAudioToTheServer()
    {
        var request = VfqPlaybackRequest.Parse(null, null, null, null);

        Assert.False(request.HasExplicitAudio);
        Assert.Null(request.MediaSourceId);
        Assert.Null(request.UserId);
    }

    [Theory]
    [InlineData("{\"AudioStreamIndex\":2}")]
    [InlineData("{\"audioStreamIndex\":0}")]
    public void BodyAudioIndexIsExplicitInEitherCasing(string body)
    {
        Assert.True(VfqPlaybackRequest.Parse(null, null, null, body).HasExplicitAudio);
    }

    [Fact]
    public void QueryAudioIndexIsExplicit()
    {
        Assert.True(VfqPlaybackRequest.Parse("1", null, null, "{}").HasExplicitAudio);
    }

    [Fact]
    public void NullBodyAudioIndexIsNotExplicit()
    {
        Assert.False(VfqPlaybackRequest.Parse(null, null, null, "{\"AudioStreamIndex\":null}").HasExplicitAudio);
    }

    [Fact]
    public void QueryWinsOverBodyLikeJellyfin()
    {
        var request = VfqPlaybackRequest.Parse(null, "fromquery", "11111111-1111-1111-1111-111111111111", "{\"MediaSourceId\":\"frombody\",\"UserId\":\"x\"}");

        Assert.Equal("fromquery", request.MediaSourceId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.UserId);
    }

    [Fact]
    public void BodyFieldsFillInWhenTheQueryHasNone()
    {
        var request = VfqPlaybackRequest.Parse("", "", "", "{\"mediaSourceId\":\"abc\",\"userId\":\"u\"}");

        Assert.Equal("abc", request.MediaSourceId);
        Assert.Equal("u", request.UserId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"AudioStreamIndex\":1,\"audioStreamIndex\":2}")]
    public void UnreadableBodyCountsAsEmpty(string body)
    {
        Assert.False(VfqPlaybackRequest.Parse(null, null, null, body).HasExplicitAudio);
    }

    [Theory]
    [InlineData("/Items/5f865dd7-d8df-b127-469f-9271e7a3a6a3/PlaybackInfo")]
    [InlineData("/jellyfin/items/5f865dd7d8dfb127469f9271e7a3a6a3/playbackinfo")]
    public void ReadsTheItemIdFromThePath(string path)
    {
        Assert.True(VfqPlaybackRequest.TryGetItemId(path, out var id));
        Assert.Equal(Guid.Parse("5f865dd7-d8df-b127-469f-9271e7a3a6a3"), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/Items/not-a-guid/PlaybackInfo")]
    [InlineData("/Items/5f865dd7-d8df-b127-469f-9271e7a3a6a3")]
    [InlineData("/Videos/5f865dd7-d8df-b127-469f-9271e7a3a6a3/PlaybackInfo")]
    public void RejectsOtherPaths(string? path)
    {
        Assert.False(VfqPlaybackRequest.TryGetItemId(path, out _));
    }
}
