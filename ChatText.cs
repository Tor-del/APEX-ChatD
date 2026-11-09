using System.Windows;
using System.Windows.Controls;

namespace TwitchChatAgent;

public static class ChatText
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.RegisterAttached(
        "Message", typeof(object), typeof(ChatText), new PropertyMetadata(null, OnMessageChanged));

    public static void SetMessage(DependencyObject target, object? value) => target.SetValue(MessageProperty, value);
    public static object? GetMessage(DependencyObject target) => target.GetValue(MessageProperty);

    private static void OnMessageChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock row)
            return;
        row.Inlines.Clear();
        if (args.NewValue is ChatMessage message)
            ChatAppearance.FillRow(row, message);
    }
}
