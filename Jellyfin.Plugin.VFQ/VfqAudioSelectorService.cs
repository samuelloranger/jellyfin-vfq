using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Fallback strategy for VFQ (French Canadian) audio selection. The primary path is
/// <see cref="VfqPlaybackInfoMiddleware"/>, which rewrites the PlaybackInfo response
/// before any client sees it. This service listens to <c>PlaybackStart</c> and, when the
/// session did not end up on a VFQ track, sends a <c>SetAudioStreamIndex</c> command to
/// the client session, unless the client picked that session's audio itself.
/// </summary>
public class VfqAudioSelectorService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<VfqAudioSelectorService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VfqAudioSelectorService"/> class.
    /// </summary>
    public VfqAudioSelectorService(
        ISessionManager sessionManager,
        IMediaSourceManager mediaSourceManager,
        ILogger<VfqAudioSelectorService> logger)
    {
        _sessionManager = sessionManager;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _logger.LogInformation("VFQ Auto Selector: started listening for playback events");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _logger.LogInformation("VFQ Auto Selector: stopped");
        return Task.CompletedTask;
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            // Wait briefly so PlayState.AudioStreamIndex is populated before we check it.
            // Without this, the session may not yet reflect the client's actual audio selection,
            // causing a redundant SetAudioStreamIndex command that interrupts early playback.
            await Task.Delay(750).ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableAutoSelect)
            {
                return;
            }

            var session = e.Session;
            if (session is null || config.DisabledUserIds.Contains(session.UserId))
            {
                return;
            }

            var nowPlaying = session.NowPlayingItem;
            if (nowPlaying is null)
            {
                return;
            }

            if (e.Item is null || string.IsNullOrEmpty(e.MediaSourceId))
            {
                return;
            }

            var mediaSource = await _mediaSourceManager
                .GetMediaSource(e.Item, e.MediaSourceId, null, false, CancellationToken.None)
                .ConfigureAwait(false);

            var audioStreams = mediaSource?.MediaStreams?
                .Where(s => s.Type == MediaStreamType.Audio)
                .ToList();

            if (audioStreams is null || audioStreams.Count == 0)
            {
                return;
            }

            var bestTrack = VfqTrackMatcher.PickBest(audioStreams, config.PreferHighestQuality);
            if (bestTrack is null)
            {
                return;
            }

            if (VfqExplicitAudioSessions.Shared.Contains(e.PlaySessionId, DateTime.UtcNow))
            {
                _logger.LogDebug(
                    "VFQ Auto Selector: the client picked the audio of '{Name}' itself, leaving it",
                    nowPlaying.Name);
                return;
            }

            var currentAudioIndex = session.PlayState?.AudioStreamIndex;
            if (currentAudioIndex == bestTrack.Index)
            {
                _logger.LogDebug(
                    "VFQ Auto Selector: VFQ track already selected for '{Name}'",
                    nowPlaying.Name);
                return;
            }

            _logger.LogInformation(
                "VFQ Auto Selector: switching to '{Title}' (index {Index}, {Codec} {Channels}ch) for '{Name}'",
                bestTrack.Title ?? bestTrack.DisplayTitle,
                bestTrack.Index,
                bestTrack.Codec,
                bestTrack.Channels,
                nowPlaying.Name);

            var command = new GeneralCommand(new Dictionary<string, string>
            {
                ["Index"] = bestTrack.Index.ToString(),
            })
            {
                Name = GeneralCommandType.SetAudioStreamIndex,
            };

            await _sessionManager.SendGeneralCommand(
                session.Id,
                session.Id,
                command,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VFQ Auto Selector: error during playback start handling");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
