using System.Windows;
using System.Windows.Controls;

namespace TwitchChatAgent;

internal sealed class ClosePrompt : Window
{
    private readonly CheckBox remember = new() { Content = Ui.T("Запомнить выбор"), Margin = new Thickness(0, 16, 0, 16) };
    public bool Remember => remember.IsChecked == true;

    public ClosePrompt(Window owner)
    {
        Owner = owner;
        Title = Ui.T("Закрыть APEX Chatd");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "FluentPanel");
        SetResourceReference(ForegroundProperty, "FluentText");
        SourceInitialized += (_, _) => ((App)Application.Current).Themes.ApplyTitleBar(this);

        var layout = new StackPanel { Margin = new Thickness(24) };
        layout.Children.Add(new TextBlock { Text = Ui.T("Точно закрыть APEX Chatd?"), FontSize = 15 });
        layout.Children.Add(remember);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var confirm = new Button { Content = Ui.T("Закрыть") };
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(confirm);
        actions.Children.Add(new Button { Content = Ui.T("Отмена"), IsCancel = true });
        layout.Children.Add(actions);
        Content = layout;
    }

}
