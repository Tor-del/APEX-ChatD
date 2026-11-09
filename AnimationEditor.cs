using System.Windows;
using System.Windows.Controls;

namespace TwitchChatAgent;

internal sealed class AnimationEditor : StackPanel
{
    private readonly ComboBox kind;
    private readonly ComboBox easing;
    private readonly StackPanel options = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly (DockPanel Row, Slider Value) duration, delay, opacity, offsetX, offsetY, scale, originX, originY;
    private readonly int defaultDuration;
    private bool loading = true;
    public MessageAnimation Kind => (MessageAnimation)Math.Max(0, kind.SelectedIndex);
    public MotionProfiles Profiles { get; private set; }
    public event Action? Changed;

    public AnimationEditor(MessageAnimation selected, MotionProfiles? profiles, int defaultDuration, bool leaving = false)
    {
        this.defaultDuration = defaultDuration;
        Profiles = profiles ?? new();
        kind = new ComboBox
        {
            ItemsSource = new[] { Ui.T("Без анимации"), Ui.T(leaving ? "Плавное исчезновение" : "Плавное появление"),
                Ui.T("Вертикальный сдвиг"), Ui.T("Горизонтальный сдвиг"), Ui.T("Масштабирование") },
            SelectedIndex = Enum.IsDefined(selected) ? (int)selected : 0
        };
        Children.Add(kind);
        Children.Add(options);
        duration = AddSlider(Ui.T("Длительность анимации (мс)"), 100, 5000, 20);
        delay = AddSlider(Ui.T("Задержка (мс)"), 0, 2000, 20);
        easing = new ComboBox
        {
            ItemsSource = new[] { Ui.T("Линейная"), Ui.T("Плавное замедление"), Ui.T("Плавное ускорение"), Ui.T("Ускорение и замедление") }
        };
        options.Children.Add(Row(Ui.T("Кривая скорости"), easing));
        opacity = AddSlider(Ui.T("Непрозрачность на краю (%)"), 0, 100);
        offsetX = AddSlider(Ui.T("Смещение по X (пкс)"), -500, 500);
        offsetY = AddSlider(Ui.T("Смещение по Y (пкс)"), -500, 500);
        scale = AddSlider(Ui.T("Масштаб (%)"), 10, 200);
        originX = AddSlider(Ui.T("Точка масштаба X (%)"), 0, 100);
        originY = AddSlider(Ui.T("Точка масштаба Y (%)"), 0, 100);
        easing.SelectionChanged += (_, _) => Store();
        kind.SelectionChanged += (_, _) =>
        {
            Load();
            Changed?.Invoke();
        };
        Load();
    }

    private void Load()
    {
        loading = true;
        var current = Profiles.Get(Kind, defaultDuration);
        options.Visibility = Kind == MessageAnimation.None ? Visibility.Collapsed : Visibility.Visible;
        offsetX.Row.Visibility = offsetY.Row.Visibility =
            Kind is MessageAnimation.Slide or MessageAnimation.Rise ? Visibility.Visible : Visibility.Collapsed;
        scale.Row.Visibility = originX.Row.Visibility = originY.Row.Visibility =
            Kind == MessageAnimation.Zoom ? Visibility.Visible : Visibility.Collapsed;
        duration.Value.Value = current.DurationMs;
        delay.Value.Value = current.DelayMs;
        easing.SelectedIndex = Enum.IsDefined(current.Easing) ? (int)current.Easing : 1;
        opacity.Value.Value = current.Opacity;
        offsetX.Value.Value = current.OffsetX;
        offsetY.Value.Value = current.OffsetY;
        scale.Value.Value = current.Scale;
        originX.Value.Value = current.OriginX;
        originY.Value.Value = current.OriginY;
        loading = false;
    }

    private void Store()
    {
        if (loading || Kind == MessageAnimation.None)
            return;
        Profiles = Profiles.Set(Kind, new MotionOptions((int)duration.Value.Value, (int)delay.Value.Value,
            (MessageEasing)easing.SelectedIndex, (int)opacity.Value.Value, (int)offsetX.Value.Value,
            (int)offsetY.Value.Value, (int)scale.Value.Value, (int)originX.Value.Value, (int)originY.Value.Value));
        Changed?.Invoke();
    }

    private (DockPanel, Slider) AddSlider(string label, int min, int max, int step = 1)
    {
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step,
            IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5, IsMoveToPointEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var amount = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right, Text = $"{slider.Value:0}" };
        slider.ValueChanged += (_, _) =>
        {
            amount.Text = $"{slider.Value:0}";
            Store();
        };
        var row = Row(label, slider, amount);
        options.Children.Add(row);
        return (row, slider);
    }

    private static DockPanel Row(string label, UIElement control, TextBlock? amount = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 144, TextWrapping = TextWrapping.Wrap });
        if (amount is not null)
        {
            DockPanel.SetDock(amount, Dock.Right);
            row.Children.Add(amount);
        }
        row.Children.Add(control);
        return row;
    }
}
