using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TwitchChatAgent;

internal static partial class SpeechTextFilter
{
    [GeneratedRegex(@":(?![0-9]+:)[\p{L}\p{M}\p{N}_+-]+:", RegexOptions.CultureInvariant, 100)]
    private static partial Regex EmoteCodes();

    private const string CommonPunctuationAndSymbols = ".,!?;:…-–—'’‘\"«»“”„()[]{}¿¡/\\%+=$€£₽¥#@&°"
        + "。、！？：；，．・（）［］「」『』【】،؛؟।॥";

    public static string Prepare(ChatMessage message, bool skipEmotes)
    {
        var text = skipEmotes && message.Parts.Any(part => part.EmoteId is not null)
            ? string.Concat(message.Parts.Select(part => part.EmoteId is null ? part.Text : " "))
            : message.Text;
        text = RemoveLinks(text);
        if (skipEmotes)
            text = EmoteCodes().Replace(text, " ");
        return CleanForSpeech(text);
    }

    public static string CleanForSpeech(string text)
    {
        // Use Unicode scalars so letters outside the BMP and accents in any script survive.
        var cleaned = new StringBuilder(text.Length);
        Span<char> buffer = stackalloc char[2];
        var hasContent = false;
        var followsContent = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // Strip invisible formatting, fillers and presentation selectors without splitting a word.
            if ((category == UnicodeCategory.Format && rune.Value != 0x200B) ||
                rune.Value is 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 ||
                rune.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) ||
                category == UnicodeCategory.EnclosingMark)
                continue;

            if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
                hasContent = followsContent = true;
            else if (!(followsContent && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            {
                followsContent = false;
                if (!rune.IsBmp || !CommonPunctuationAndSymbols.Contains((char)rune.Value))
                {
                    if (cleaned.Length > 0 && cleaned[^1] != ' ')
                        cleaned.Append(' ');
                    continue;
                }
            }

            var length = rune.EncodeToUtf16(buffer);
            cleaned.Append(buffer[..length]);
        }
        return hasContent ? cleaned.ToString().TrimEnd() : "";
    }

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

