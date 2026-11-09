using System.Globalization;
using ScreenReader.Speech;

namespace TwitchChatAgent;

internal sealed record VoiceLanguage(string Code, string Name);

internal static class EdgeVoiceCatalog
{
    // Fallback voices from LazySkald's LanguageCatalog.
    public static IReadOnlyList<SpeechVoice> Defaults { get; } =
    [
        new("ru-RU-SvetlanaNeural", "Светлана", "ru-RU", "Female"), new("ru-RU-DmitryNeural", "Дмитрий", "ru-RU", "Male"),
        new("en-US-JennyNeural", "Jenny", "en-US", "Female"), new("en-US-GuyNeural", "Guy", "en-US", "Male"),
        new("zh-CN-XiaoxiaoNeural", "Xiaoxiao", "zh-CN", "Female"), new("zh-CN-YunxiNeural", "Yunxi", "zh-CN", "Male"),
        new("ja-JP-NanamiNeural", "Nanami", "ja-JP", "Female"), new("ja-JP-KeitaNeural", "Keita", "ja-JP", "Male"),
        new("es-ES-ElviraNeural", "Elvira", "es-ES", "Female"), new("es-ES-AlvaroNeural", "Alvaro", "es-ES", "Male"),
        new("ko-KR-SunHiNeural", "SunHi", "ko-KR", "Female"), new("ko-KR-InJoonNeural", "InJoon", "ko-KR", "Male"),
        new("fr-FR-DeniseNeural", "Denise", "fr-FR", "Female"), new("fr-FR-HenriNeural", "Henri", "fr-FR", "Male"),
        new("de-DE-KatjaNeural", "Katja", "de-DE", "Female"), new("de-DE-ConradNeural", "Conrad", "de-DE", "Male")
    ];

    public static string LanguageName(string locale)
    {
        try
        {
            return $"{CultureInfo.GetCultureInfo(locale).NativeName} ({locale})";
        }
        catch (CultureNotFoundException)
        {
            return locale;
        }
    }

    public static string LocaleFromVoice(string voice)
    {
        var last = voice.LastIndexOf('-');
        return last > 0 ? voice[..last] : "ru-RU";
    }
}
