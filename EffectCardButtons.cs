using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TwitchChatAgent;

internal static class EffectCardButtons
{
    public static Button Remove() => Create("\uE74D", Ui.T("Удалить эффект"));

    public static Button Toggle()
    {
        var button = Create("", "");
        button.Margin = new Thickness(0, 0, 6, 0);
        return button;
    }

    public static void SetEnabled(Button button, bool enabled)
    {
        ((TextBlock)button.Content).Text = enabled ? "\uE890" : "\uED1A";
        var name = Ui.T(enabled ? "Отключить эффект" : "Включить эффект");
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
    }

    private static Button Create(string icon, string name)
    {
        var button = new Button { Width = 34, MinHeight = 30, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = name };
        AutomationProperties.SetName(button, name);
        return button;
    }
}
