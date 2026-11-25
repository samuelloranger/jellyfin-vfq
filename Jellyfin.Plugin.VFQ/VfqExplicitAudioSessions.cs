using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Play sessions whose audio track the client picked itself. The middleware records them
/// when it sees a PlaybackInfo request that names an audio stream, and the PlaybackStart
/// fallback leaves those sessions alone instead of switching them back to VFQ.
/// </summary>
internal sealed class VfqExplicitAudioSessions
{
    private const int PruneThreshold = 256;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the instance shared by the middleware and the hosted service.
    /// </summary>
    public static VfqExplicitAudioSessions Shared { get; } = new();

    /// <summary>
    /// Gets the number of sessions currently held.
    /// </summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Records a play session, dropping expired ones once enough have piled up.
    /// </summary>
    public void Add(string? playSessionId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(playSessionId))
        {
            return;
        }

        _sessions[playSessionId] = nowUtc;

        if (_sessions.Count > PruneThreshold)
        {
            foreach (var pair in _sessions)
            {
                if (nowUtc - pair.Value >= Lifetime)
                {
                    _sessions.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Tells whether the client picked the audio of this play session itself.
    /// </summary>
    public bool Contains(string? playSessionId, DateTime nowUtc)
        => !string.IsNullOrEmpty(playSessionId)
            && _sessions.TryGetValue(playSessionId, out var added)
            && nowUtc - added < Lifetime;
}
