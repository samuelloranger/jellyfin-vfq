namespace Jellyfin.Plugin.VFQ.Tests;

public class VfqExplicitAudioSessionsTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RemembersARecordedSession()
    {
        var sessions = new VfqExplicitAudioSessions();
        sessions.Add("abc", Now);

        Assert.True(sessions.Contains("abc", Now.AddMinutes(90)));
        Assert.False(sessions.Contains("other", Now));
    }

    [Fact]
    public void IgnoresMissingIds()
    {
        var sessions = new VfqExplicitAudioSessions();
        sessions.Add(null, Now);
        sessions.Add(string.Empty, Now);

        Assert.Equal(0, sessions.Count);
        Assert.False(sessions.Contains(null, Now));
    }

    [Fact]
    public void ForgetsSessionsAfterTwelveHours()
    {
        var sessions = new VfqExplicitAudioSessions();
        sessions.Add("abc", Now);

        Assert.False(sessions.Contains("abc", Now.AddHours(12)));
    }

    [Fact]
    public void PrunesExpiredSessionsOnceManyPileUp()
    {
        var sessions = new VfqExplicitAudioSessions();
        for (var i = 0; i < 256; i++)
        {
            sessions.Add($"old{i}", Now);
        }

        sessions.Add("new", Now.AddDays(1));

        Assert.Equal(1, sessions.Count);
        Assert.True(sessions.Contains("new", Now.AddDays(1)));
    }
}
