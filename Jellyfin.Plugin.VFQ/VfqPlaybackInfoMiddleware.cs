using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// ASP.NET middleware that intercepts PlaybackInfo API responses and overrides
/// DefaultAudioStreamIndex to point to VFQ audio tracks when available.
/// Runs in the HTTP pipeline before any client receives the response,
/// so it works universally on all devices.
/// </summary>
public class VfqPlaybackInfoMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<VfqPlaybackInfoMiddleware> _logger;

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
    /// Initializes a new instance of the <see cref="VfqPlaybackInfoMiddleware"/> class.
    /// </summary>
    public VfqPlaybackInfoMiddleware(RequestDelegate next, ILogger<VfqPlaybackInfoMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request. If the request is for PlaybackInfo,
    /// intercepts the response to override the default audio stream.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is null
            || !path.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableAutoSelect)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Capture the response body.
        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await _next(context).ConfigureAwait(false);

        memoryStream.Seek(0, SeekOrigin.Begin);

        if (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
            && context.Response.StatusCode == 200)
        {
            try
            {
                var json = await JsonNode.ParseAsync(memoryStream).ConfigureAwait(false);
                if (json is not null && PatchMediaSources(json))
                {
                    memoryStream.SetLength(0);
                    using var writer = new Utf8JsonWriter(memoryStream);
                    json.WriteTo(writer);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (JsonException)
            {
                // Not valid JSON — pass through unchanged.
            }
        }

        memoryStream.Seek(0, SeekOrigin.Begin);
        context.Response.ContentLength = memoryStream.Length;
        await memoryStream.CopyToAsync(originalBodyStream).ConfigureAwait(false);
        context.Response.Body = originalBodyStream;
    }

    private bool PatchMediaSources(JsonNode root)
    {
        var mediaSources = root["MediaSources"]?.AsArray();
        if (mediaSources is null)
        {
            return false;
        }

        var config = Plugin.Instance?.Configuration;
        var preferQuality = config?.PreferHighestQuality ?? true;
        var modified = false;

        foreach (var source in mediaSources)
        {
            if (source is null)
            {
                continue;
            }

            var streams = source["MediaStreams"]?.AsArray();
            if (streams is null)
            {
                continue;
            }

            var vfqTracks = new List<(int Index, string? Title, string? Codec, string? Profile, int Channels)>();

            foreach (var stream in streams)
            {
                if (stream is null)
                {
                    continue;
                }

                var type = stream["Type"]?.GetValue<string>();
                if (type != "Audio")
                {
                    continue;
                }

                var title = stream["Title"]?.GetValue<string>() ?? string.Empty;
                var displayTitle = stream["DisplayTitle"]?.GetValue<string>() ?? string.Empty;
                var language = stream["Language"]?.GetValue<string>() ?? string.Empty;
                var index = stream["Index"]?.GetValue<int>() ?? -1;

                if (index < 0 || !IsVfqTrack(title, displayTitle, language))
                {
                    continue;
                }

                vfqTracks.Add((
                    index,
                    title.Length > 0 ? title : displayTitle,
                    stream["Codec"]?.GetValue<string>(),
                    stream["Profile"]?.GetValue<string>(),
                    stream["Channels"]?.GetValue<int>() ?? 0));
            }

            if (vfqTracks.Count == 0)
            {
                continue;
            }

            var best = preferQuality && vfqTracks.Count > 1
                ? vfqTracks
                    .OrderByDescending(t => GetCodecRankValue(t.Codec, t.Profile))
                    .ThenByDescending(t => t.Channels)
                    .First()
                : vfqTracks.First();

            var currentDefault = source["DefaultAudioStreamIndex"]?.GetValue<int>();
            if (currentDefault == best.Index)
            {
                continue;
            }

            _logger.LogInformation(
                "VFQ Auto Selector: overriding DefaultAudioStreamIndex from {Old} to {New} ('{Title}', {Codec} {Channels}ch)",
                currentDefault,
                best.Index,
                best.Title,
                best.Codec,
                best.Channels);

            source["DefaultAudioStreamIndex"] = best.Index;
            modified = true;
        }

        return modified;
    }

    private static bool IsVfqTrack(string title, string displayTitle, string language)
    {
        foreach (var keyword in VfqKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || displayTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return language.Contains("-ca", StringComparison.OrdinalIgnoreCase)
            || language.Contains("_ca", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetCodecRankValue(string? codec, string? profile)
    {
        if (!string.IsNullOrEmpty(profile) && CodecRank.TryGetValue(profile, out var profileRank))
        {
            return profileRank;
        }

        if (!string.IsNullOrEmpty(codec) && CodecRank.TryGetValue(codec, out var codecRank))
        {
            return codecRank;
        }

        return -1;
    }
}
