using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// The parts of a PlaybackInfo request the plugin acts on. Jellyfin reads each field
/// from the query string first and from the posted <c>PlaybackInfoDto</c> second, so
/// <see cref="Parse"/> follows the same order.
/// </summary>
/// <param name="HasExplicitAudio">The client named an audio stream itself.</param>
/// <param name="MediaSourceId">The media source the client asked for, if any.</param>
/// <param name="UserId">The user the client asked for, if any.</param>
internal sealed record VfqPlaybackRequest(bool HasExplicitAudio, string? MediaSourceId, string? UserId)
{
    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads a PlaybackInfo request from its query values and raw body. A body that is
    /// missing or not a JSON object counts as empty.
    /// </summary>
    public static VfqPlaybackRequest Parse(string? queryAudioStreamIndex, string? queryMediaSourceId, string? queryUserId, string? body)
    {
        var (bodyAudio, bodyMediaSourceId, bodyUserId) = ReadBody(body);
        return new VfqPlaybackRequest(
            !string.IsNullOrEmpty(queryAudioStreamIndex) || bodyAudio,
            NonEmpty(queryMediaSourceId) ?? NonEmpty(bodyMediaSourceId),
            NonEmpty(queryUserId) ?? NonEmpty(bodyUserId));
    }

    /// <summary>
    /// Extracts the item id from a <c>/Items/{itemId}/PlaybackInfo</c> path, with or
    /// without a base URL in front.
    /// </summary>
    public static bool TryGetItemId(string? path, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments.Length - 1;
        return last >= 2
            && string.Equals(segments[last], "PlaybackInfo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[last - 2], "Items", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[last - 1], out itemId);
    }

    private static (bool HasAudio, string? MediaSourceId, string? UserId) ReadBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            // JsonObject builds its key lookup on first access, so duplicate keys throw
            // on the reads below rather than in Parse: keep them inside the try.
            if (JsonNode.Parse(body, CaseInsensitive) is not JsonObject dto)
            {
                return default;
            }

            return (dto["AudioStreamIndex"] is not null, ReadString(dto["MediaSourceId"]), ReadString(dto["UserId"]));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Malformed JSON, or the same key twice in different casings.
            return default;
        }
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? NonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
