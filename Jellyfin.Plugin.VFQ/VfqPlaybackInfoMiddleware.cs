using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// ASP.NET middleware that makes PlaybackInfo pick VFQ audio tracks when available.
/// When the client leaves the audio to the server, it adds the VFQ track to the request
/// so the stream URL Jellyfin builds carries it, then aligns DefaultAudioStreamIndex on
/// sources the client plays directly. A track the client names itself is left alone.
/// Runs in the HTTP pipeline before any client receives the response,
/// so it works universally on all devices.
/// </summary>
public class VfqPlaybackInfoMiddleware
{
    /// <summary>
    /// The largest PlaybackInfo body inspected. A device profile is a few tens of KB.
    /// </summary>
    private const long MaxBodyBytes = 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ILogger<VfqPlaybackInfoMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VfqPlaybackInfoMiddleware"/> class.
    /// </summary>
    public VfqPlaybackInfoMiddleware(RequestDelegate next, ILogger<VfqPlaybackInfoMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Processes the HTTP request. If the request is for PlaybackInfo, pins the VFQ
    /// track on the request and patches the response to match.
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

        var request = await ReadRequestAsync(context).ConfigureAwait(false);
        if (request is null)
        {
            // A body too large or unreadable to inspect: let Jellyfin handle it untouched.
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (await IsUserDisabledAsync(context, config, request).ConfigureAwait(false))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!request.HasExplicitAudio && HttpMethods.IsPost(context.Request.Method))
        {
            PinVfqTrack(context, request, config);
        }

        // Jellyfin registers UseResponseCompression() downstream of this middleware,
        // so without this the buffer below would capture gzip/brotli bytes instead of
        // JSON and the patch would silently no-op. PlaybackInfo payloads are small.
        var originalAcceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.AcceptEncoding = StringValues.Empty;

        // Capture the response body.
        var originalBodyStream = context.Response.Body;
        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            memoryStream.Seek(0, SeekOrigin.Begin);

            if (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
                && context.Response.StatusCode == 200)
            {
                try
                {
                    var json = await JsonNode.ParseAsync(memoryStream).ConfigureAwait(false);
                    if (request.HasExplicitAudio)
                    {
                        // The client chose this track: the fallback must not switch it back.
                        VfqExplicitAudioSessions.Shared.Add(
                            ReadString(GetProp(json, "PlaySessionId")),
                            DateTime.UtcNow);
                    }
                    else if (json is not null && PatchMediaSources(json))
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
        }
        finally
        {
            context.Response.Body = originalBodyStream;
            context.Request.Headers.AcceptEncoding = originalAcceptEncoding;
        }
    }

    /// <summary>
    /// Reads the request, or returns null when its body can't be inspected safely. This runs
    /// before authentication, so only a body of a declared, small size is buffered.
    /// </summary>
    private static async Task<VfqPlaybackRequest?> ReadRequestAsync(HttpContext context)
    {
        string? body = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var length = context.Request.ContentLength;
            if (length is null ? context.Request.Headers.TransferEncoding.Count > 0 : length > MaxBodyBytes)
            {
                return null;
            }

            if (length > 0)
            {
                // Buffer the body so Jellyfin's model binding can read it again after us.
                context.Request.EnableBuffering();
                try
                {
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                    body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or BadHttpRequestException)
                {
                    return null;
                }
                finally
                {
                    context.Request.Body.Position = 0;
                }
            }
        }

        var query = context.Request.Query;
        return VfqPlaybackRequest.Parse(
            query["audioStreamIndex"].ToString(),
            query["mediaSourceId"].ToString(),
            query["userId"].ToString(),
            body);
    }

    /// <summary>
    /// Resolves the user the PlaybackInfo is for the same way Jellyfin does: the
    /// <c>userId</c> query parameter or body field, else the request's token. The
    /// token lookup only runs when some user has opted out.
    /// </summary>
    private async Task<bool> IsUserDisabledAsync(
        HttpContext context,
        Configuration.PluginConfiguration config,
        VfqPlaybackRequest request)
    {
        if (config.DisabledUserIds.Length == 0)
        {
            return false;
        }

        Guid userId;
        if (!Guid.TryParse(request.UserId, out userId))
        {
            var auth = context.RequestServices.GetService<IAuthorizationContext>();
            if (auth is null)
            {
                return false;
            }

            userId = (await auth.GetAuthorizationInfo(context.Request).ConfigureAwait(false)).UserId;
        }

        if (!config.DisabledUserIds.Contains(userId))
        {
            return false;
        }

        _logger.LogDebug("VFQ Auto Selector: skipping PlaybackInfo for disabled user {UserId}", userId);
        return true;
    }

    /// <summary>
    /// Adds the VFQ track to the request, so the stream URL Jellyfin builds plays it.
    /// Jellyfin only applies an audio index to the media source the request names, so
    /// the source is named too when the item has a single one.
    /// </summary>
    private void PinVfqTrack(HttpContext context, VfqPlaybackRequest request, Configuration.PluginConfiguration config)
    {
        if (!VfqPlaybackRequest.TryGetItemId(context.Request.Path.Value, out var itemId))
        {
            return;
        }

        try
        {
            var libraryManager = context.RequestServices.GetService<ILibraryManager>();
            var mediaSourceManager = context.RequestServices.GetService<IMediaSourceManager>();
            var item = libraryManager?.GetItemById(itemId);
            if (item is null || mediaSourceManager is null)
            {
                return;
            }

            var sources = mediaSourceManager.GetStaticMediaSources(item, false);

            // shortcut: an item with several versions and no version named in the request
            // keeps Jellyfin's own audio pick; naming one here would hide the others from
            // the client. Upgrade if multi-version VFQ items show up.
            var source = request.MediaSourceId is null
                ? (sources.Count == 1 ? sources[0] : null)
                : sources.FirstOrDefault(s => string.Equals(s.Id, request.MediaSourceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return;
            }

            var best = VfqTrackMatcher.PickBest(source.MediaStreams, config.PreferHighestQuality);
            if (best is null)
            {
                return;
            }

            // Query values take precedence over the posted body in Jellyfin's PlaybackInfo.
            context.Request.QueryString = new QueryString(VfqPlaybackRequest.PinQuery(
                context.Request.QueryString.Value,
                best.Index,
                request.MediaSourceId is null ? source.Id : null));

            _logger.LogInformation(
                "VFQ Auto Selector: requesting '{Title}' (index {Index}, {Codec} {Channels}ch) for '{Name}'",
                best.Title ?? best.DisplayTitle,
                best.Index,
                best.Codec,
                best.Channels,
                item.Name);
        }
        catch (Exception ex)
        {
            // Never break playback: Jellyfin then answers with its own pick.
            _logger.LogWarning(ex, "VFQ Auto Selector: could not pin the VFQ track for item {ItemId}", itemId);
        }
    }

    private bool PatchMediaSources(JsonNode root)
    {
        var mediaSources = GetProp(root, "MediaSources")?.AsArray();
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

            // The stream URL already fixes the audio the server sends. Pointing the
            // default elsewhere would show one track and play another; PinVfqTrack is
            // what puts VFQ in that URL.
            if (!string.IsNullOrEmpty(ReadString(GetProp(source, "TranscodingUrl"))))
            {
                continue;
            }

            var streams = GetProp(source, "MediaStreams")?.AsArray();
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

                var type = GetProp(stream, "Type")?.GetValue<string>();
                if (!string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = GetProp(stream, "Title")?.GetValue<string>() ?? string.Empty;
                var displayTitle = GetProp(stream, "DisplayTitle")?.GetValue<string>() ?? string.Empty;
                var language = GetProp(stream, "Language")?.GetValue<string>() ?? string.Empty;
                var index = GetProp(stream, "Index")?.GetValue<int>() ?? -1;

                if (index < 0 || !VfqTrackMatcher.IsVfqTrack(title, displayTitle, language))
                {
                    continue;
                }

                vfqTracks.Add((
                    index,
                    title.Length > 0 ? title : displayTitle,
                    GetProp(stream, "Codec")?.GetValue<string>(),
                    GetProp(stream, "Profile")?.GetValue<string>(),
                    GetProp(stream, "Channels")?.GetValue<int>() ?? 0));
            }

            if (vfqTracks.Count == 0)
            {
                continue;
            }

            var best = preferQuality && vfqTracks.Count > 1
                ? vfqTracks
                    .OrderByDescending(t => VfqTrackMatcher.CodecRank(t.Codec, t.Profile))
                    .ThenByDescending(t => t.Channels)
                    .First()
                : vfqTracks.First();

            var currentDefault = GetProp(source, "DefaultAudioStreamIndex")?.GetValue<int>();
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

            source[ResolveKey(source, "DefaultAudioStreamIndex")] = best.Index;
            modified = true;
        }

        return modified;
    }

    /// <summary>
    /// Reads a property, tolerating either casing. Jellyfin serializes PascalCase by
    /// default but will emit camelCase when a client asks for the
    /// <c>application/json; profile="CamelCase"</c> output formatter.
    /// </summary>
    private static JsonNode? GetProp(JsonNode? node, string name)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (obj.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Resolves the key actually present on the object so writes replace the existing
    /// property instead of adding a second one with the wrong casing.
    /// </summary>
    private static string ResolveKey(JsonNode node, string name)
    {
        if (node is not JsonObject obj || obj.ContainsKey(name))
        {
            return name;
        }

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return name;
    }
}
