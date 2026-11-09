using System.Text.RegularExpressions;

namespace TwitchChatAgent;

internal static partial class SpeechTextFilter
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@])(?:(?:(?:https?|ftp)://|www\.)[^\s<>""«»]+|(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:com|org|net|info|online|site|link|app|dev|shop|tech|live|cloud|store|travel|news|game|games|рф|[a-z]{2})(?![\p{L}\p{N}_@-])(?::\d{1,5})?(?:[/\x3F#][^\s<>""«»]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex Links();

    public static string RemoveLinks(string text)
    {
        var cleaned = Links().Replace(text, match =>
        {
            var end = match.Length;
            while (end > 0 && ".,!?;:)]}»".Contains(match.Value[end - 1]))
                end--;
            return match.Value[end..];
        });
        if (cleaned == text)
            return text;
        cleaned = cleaned.Trim();
        return cleaned.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c) || c is '<' or '>') ? "" : cleaned;
    }
}

