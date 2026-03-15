using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Hosted service that overrides the default audio stream selection in Jellyfin
/// to prefer VFQ (French Canadian) audio tracks. Uses two complementary strategies:
///
/// 1. Patches <c>SetDefaultAudioAndSubtitleStreamIndices</c> on the resolved
///    <see cref="IMediaSourceManager"/> so that PlaybackInfo responses already
///    contain the correct default — works for ALL clients before playback starts.
///
/// 2. Listens to <c>PlaybackStart</c> as a fallback and sends a
///    <c>SetAudioStreamIndex</c> command to the client session.
/// </summary>
public class VfqAudioSelectorService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<VfqAudioSelectorService> _logger;

    private static readonly string[] VfqKeywords =
    {
        "vfq",
        "fr-ca",
        "fra-ca",
        "fre-ca",
        "french canadian",
        "français canadien",
        "francais canadien",
        "québécois",
        "quebecois",
        "canadian french",
        "canadien",
        "qc",
    };

    private static readonly Dictionary<string, int> CodecRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["truehd"] = 6,
        ["dts-hd ma"] = 5,
        ["dts-hd"] = 5,
        ["flac"] = 4,
        ["eac3"] = 3,
        ["dts"] = 2,
        ["ac3"] = 1,
        ["aac"] = 0,
    };

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
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableAutoSelect)
            {
                return;
            }

            var session = e.Session;
            if (session is null)
            {
                return;
            }

            var nowPlaying = session.NowPlayingItem;
            if (nowPlaying is null)
            {
                return;
            }

            var mediaStreams = e.Item?.GetMediaStreams();
            if (mediaStreams is null || mediaStreams.Count == 0)
            {
                return;
            }

            var audioStreams = mediaStreams
                .Where(s => s.Type == MediaStreamType.Audio)
                .ToList();

            if (audioStreams.Count == 0)
            {
                return;
            }

            var vfqTracks = audioStreams.Where(IsVfqTrack).ToList();
            if (vfqTracks.Count == 0)
            {
                return;
            }

            var bestTrack = PickBestTrack(vfqTracks, config.PreferHighestQuality);

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

    /// <summary>
    /// Applies VFQ default to a MediaSourceInfo by overriding DefaultAudioStreamIndex.
    /// Called from the patched SetDefaultAudioAndSubtitleStreamIndices.
    /// </summary>
    internal static void ApplyVfqDefault(MediaSourceInfo source, ILogger logger)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableAutoSelect)
        {
            return;
        }

        var audioStreams = source.MediaStreams?
            .Where(s => s.Type == MediaStreamType.Audio)
            .ToList();

        if (audioStreams is null || audioStreams.Count == 0)
        {
            return;
        }

        var vfqTracks = audioStreams.Where(IsVfqTrack).ToList();
        if (vfqTracks.Count == 0)
        {
            return;
        }

        var bestTrack = PickBestTrack(vfqTracks, config.PreferHighestQuality);

        if (source.DefaultAudioStreamIndex == bestTrack.Index)
        {
            return;
        }

        logger.LogInformation(
            "VFQ Auto Selector: overriding default audio from {Old} to {New} ('{Title}', {Codec} {Channels}ch)",
            source.DefaultAudioStreamIndex,
            bestTrack.Index,
            bestTrack.Title ?? bestTrack.DisplayTitle,
            bestTrack.Codec,
            bestTrack.Channels);

        source.DefaultAudioStreamIndex = bestTrack.Index;
    }

    private static MediaStream PickBestTrack(List<MediaStream> vfqTracks, bool preferQuality)
    {
        if (preferQuality && vfqTracks.Count > 1)
        {
            return vfqTracks
                .OrderByDescending(GetCodecRank)
                .ThenByDescending(s => s.Channels ?? 0)
                .First();
        }

        return vfqTracks.First();
    }

    private static bool IsVfqTrack(MediaStream stream)
    {
        var title = stream.Title ?? string.Empty;
        var displayTitle = stream.DisplayTitle ?? string.Empty;
        var language = stream.Language ?? string.Empty;

        foreach (var keyword in VfqKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || displayTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (language.Contains("-ca", StringComparison.OrdinalIgnoreCase)
            || language.Contains("_ca", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static int GetCodecRank(MediaStream stream)
    {
        var codec = stream.Codec ?? string.Empty;
        var profile = stream.Profile ?? string.Empty;

        if (!string.IsNullOrEmpty(profile) && CodecRank.TryGetValue(profile, out var profileRank))
        {
            return profileRank;
        }

        if (CodecRank.TryGetValue(codec, out var codecRank))
        {
            return codecRank;
        }

        return -1;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
