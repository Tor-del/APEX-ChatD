using System.Globalization;

namespace ApexChatd.GitHubUpdates;

internal static class Ui
{
    public static Func<string, string> Translate { get; set; } = text => text;
    public static string T(string text) => Translate(text);

    public static string F(string format, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(format), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, format, args); }
    }
}
