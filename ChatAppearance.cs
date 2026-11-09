using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TwitchChatAgent;

internal static class ChatAppearance
{
    public static ChatMessage Sample => new(Ui.T("Зритель"), Ui.T("Привет! Так будет выглядеть сообщение чата."),
        NameColor: "#00C896");

    public static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (value is not { Length: 7 or 9 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            return false;
        color = Color.FromArgb(value.Length == 7 ? (byte)255 : (byte)(hex >> 24),
            (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        return true;
    }

    public static Color BackgroundColor(ChatTheme theme)
    {
        TryColor(theme.Background, out var color);
        if (theme.Background.Length == 7)
            color.A = (byte)Math.Round(Math.Clamp(theme.Opacity, 0, 100) * 255 / 100.0);
        return color;
    }

    public static TimeSpan MessageLifetime(ChatTheme theme) =>
        TimeSpan.FromSeconds(Math.Clamp(theme.MessageTimeoutSeconds, 1, 300));

    public static ChatMessage[] VisibleMessages(ChatTheme theme, IEnumerable<ChatMessage> messages, DateTimeOffset now)
    {
        if (theme.HideExpiredMessages)
        {
            var cutoff = now - MessageLifetime(theme);
            messages = messages.Where(message => message.ReceivedAt > cutoff);
        }

        return messages.TakeLast(theme.Rows).ToArray();
    }

    public static TextBlock CreateRow(ChatTheme theme, ChatMessage message)
    {
        TryColor(theme.Text, out var textColor);
        TryColor(theme.Name, out var nameColor);
        if (theme.UseTwitchNameColor && message.NameColor is { Length: 7 } && TryColor(message.NameColor, out var twitchColor))
            nameColor = twitchColor;

        var row = new TextBlock
        {
            FontFamily = new FontFamily(theme.Font),
            FontSize = theme.Size,
            Foreground = new SolidColorBrush(textColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };
        FillRow(row, message, new SolidColorBrush(nameColor));
        return row;
    }

    public static void FillRow(TextBlock row, ChatMessage message, Brush? nameColor = null)
    {
        var name = new Run(message.Name + ": ") { FontWeight = FontWeights.SemiBold };
        if (nameColor is not null)
            name.Foreground = nameColor;
        row.Inlines.Add(name);
        if (message.Parts.Length == 0)
        {
            row.Inlines.Add(new Run(message.Text));
            return;
        }

        foreach (var part in message.Parts)
            row.Inlines.Add(part.EmoteId is null ? new Run(part.Text) : CreateEmote(part));
    }

    private static Span CreateEmote(ChatMessagePart part)
    {
        var span = new Span(new Run(part.Text));
        var bitmap = new BitmapImage();
        void ShowImage(object? sender, EventArgs args)
        {
            if (bitmap.PixelWidth == 0)
                return;

            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, ToolTip = part.Text };
            System.Windows.Automation.AutomationProperties.SetName(image, part.Text);
            image.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding(nameof(TextBlock.FontSize)) { Source = span });
            image.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding(nameof(TextBlock.FontSize)) { Source = span });
            span.Inlines.Clear();
            span.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }

        try
        {
            bitmap.BeginInit();
            bitmap.UriSource = new Uri($"https://static-cdn.jtvnw.net/emoticons/v2/{part.EmoteId}/static/dark/2.0");
            bitmap.DownloadCompleted += ShowImage;
            bitmap.EndInit();
            if (!bitmap.IsDownloading)
                ShowImage(null, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            // Leave the emote name visible when its image cannot be loaded.
        }
        return span;
    }
}
