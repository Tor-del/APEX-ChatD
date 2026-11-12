using System.Windows;
using System.Windows.Controls;

using ApexChatd.Plugins;

namespace ApexChatd.GitHubUpdates;

internal enum UpdateDecision { Later, Install, Ignore }

internal sealed class UpdatePrompt : Window
{
    public UpdateDecision Decision { get; private set; }

    public UpdatePrompt(IPluginContext context, Version version, bool canInstall)
    {
        var owner = context.MainWindow;
        var hasOwner = owner.IsVisible && owner.WindowState != WindowState.Minimized;
        if (hasOwner) Owner = owner;
        else Topmost = true;
        Title = Ui.T("Обновление APEX Chatd");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "FluentPanel");
        SetResourceReference(ForegroundProperty, "FluentText");
        SourceInitialized += (_, _) => context.ApplyWindowTheme(this);

        var layout = new StackPanel { Margin = new Thickness(24) };
        layout.Children.Add(new TextBlock { Text = Ui.F("Доступна версия APEX Chatd {0}.", version.ToString(3)),
            FontSize = 16, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(new TextBlock
        {
            Text = Ui.T(canInstall ? "После установки приложение запустится автоматически." : "Установщик можно загрузить со страницы релиза."),
            Margin = new Thickness(0, 8, 0, 16)
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Add(canInstall ? "Установить" : "Открыть релиз", UpdateDecision.Install);
        Add("Напомнить позже", UpdateDecision.Later);
        Add("Больше не напоминать", UpdateDecision.Ignore);
        layout.Children.Add(actions);
        Content = layout;

        void Add(string label, UpdateDecision decision)
        {
            var button = new Button { Content = Ui.T(label), IsCancel = decision == UpdateDecision.Later };
            button.Click += (_, _) => { Decision = decision; Close(); };
            actions.Children.Add(button);
        }
    }
}
