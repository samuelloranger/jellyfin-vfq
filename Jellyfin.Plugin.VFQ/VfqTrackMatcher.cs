using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Shared VFQ track detection helpers used by both playback interception paths.
/// </summary>
internal static class VfqTrackMatcher
{
    private static readonly string[] VfqKeywords =
    {
        "vfq",
        "fr ca",
        "fra ca",
        "fre ca",
        "french canadian",
        "french canada",
        "francais canadien",
        "francais canada",
        "quebecois",
        "quebec",
        "canadian french",
        "canadien",
        "qc",
    };

    private static readonly string[] FrenchKeywords =
    {
        "francais",
        "french",
    };

    private static readonly string[] CanadaKeywords =
    {
        "canada",
        "canadian",
        "canadien",
        "quebec",
        "quebecois",
        "qc",
    };

    public static bool IsVfqTrack(MediaStream stream)
        => IsVfqTrack(stream.Title, stream.DisplayTitle, stream.Language);

    public static bool IsVfqTrack(string? title, string? displayTitle, string? language)
        => IsVfqText(title) || IsVfqText(displayTitle) || IsVfqText(language);

    private static bool IsVfqText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);

        foreach (var keyword in VfqKeywords)
        {
            if (MatchesKeyword(normalized, tokenSet, keyword))
            {
                return true;
            }
        }

        return FrenchKeywords.Any(tokenSet.Contains) && CanadaKeywords.Any(tokenSet.Contains);
    }

    private static bool MatchesKeyword(string normalized, HashSet<string> tokenSet, string keyword)
    {
        if (keyword.Contains(' ', StringComparison.Ordinal))
        {
            return normalized.Contains(keyword, StringComparison.Ordinal);
        }

        return tokenSet.Contains(keyword);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasSpace = false;
                continue;
            }

            if (previousWasSpace)
            {
                continue;
            }

            builder.Append(' ');
            previousWasSpace = true;
        }

        return builder.ToString().Trim();
    }
}
