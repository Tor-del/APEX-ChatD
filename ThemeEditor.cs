using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TwitchChatAgent;

internal sealed class ThemeEditor : Grid
{
    private readonly ComboBox font;
    private readonly CheckBox twitchNameColor;
    private readonly Slider size;
    private readonly TextBox text;
    private readonly TextBox name;
    private readonly TextBox background;
    private readonly Slider rows;
    private readonly Slider radius;
    private readonly CheckBox hideExpiredMessages;
    private readonly Slider messageTimeout;
    private readonly ComboBox direction;
    private readonly CheckBox? animateMessages;
    private static readonly string[] CurveNames = ["Линейная", "Плавное ускорение", "Плавное замедление", "Ускорение и замедление"];
    private readonly int legacyTransitionDuration;
    private readonly Func<ObsTransition>? entrance, departure;
    private readonly ComboBox? enterEffect, exitEffect, moveCurve;
    private readonly CheckBox? animateMove;
    private readonly Slider? moveDuration;
    public event Action<ChatTheme>? Changed;

    public ThemeEditor(ChatTheme theme, UIElement? backgroundMedia = null)
    {
        legacyTransitionDuration = theme.MessageTransitionMs;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        ColumnDefinitions.Add(new ColumnDefinition());
        font = AddFontPicker(theme.Font);
        size = AddSlider(Ui.T("Размер текста"), theme.Size, 10, 96);
        text = AddColor(Ui.T("Цвет текста"), theme.Text);
        name = AddColor(Ui.T("Цвет ника"), theme.Name);
        twitchNameColor = new CheckBox { Content = Ui.T("Использовать цвет ника из Twitch"), IsChecked = theme.UseTwitchNameColor,
            Margin = new Thickness(0, 5, 0, 5) };
        AddRow("", twitchNameColor);
        background = AddColor(Ui.T("Цвет фона"), ChatAppearance.BackgroundColor(theme).ToString());
        if (backgroundMedia is not null)
            AddRow("", backgroundMedia);
        rows = AddSlider(Ui.T("Лимит отображаемых сообщений"), theme.Rows, 1, 50);

        radius = AddSlider(Ui.T("Радиус скругления"), theme.Radius, 0, 100);
        hideExpiredMessages = new CheckBox { Content = Ui.T("Скрывать сообщения по тайм-ауту"), IsChecked = theme.HideExpiredMessages,
            Margin = new Thickness(0, 5, 0, 5) };
        AddRow("", hideExpiredMessages);
        messageTimeout = AddSlider(Ui.T("Тайм-аут (секунды)"), theme.MessageTimeoutSeconds, 1, 300, hideExpiredMessages);
        hideExpiredMessages.Checked += (_, _) => NotifyChanged();
        hideExpiredMessages.Unchecked += (_, _) => NotifyChanged();

        font.SelectionChanged += (_, _) => NotifyChanged();
        twitchNameColor.Checked += (_, _) => NotifyChanged();
        twitchNameColor.Unchecked += (_, _) => NotifyChanged();
        direction = new ComboBox { ItemsSource = new[] { Ui.T("Сверху вниз"), Ui.T("Снизу вверх") },
            SelectedIndex = theme.NewestAtTop ? 0 : 1 };
        AddRow(Ui.T("Направление чата"), direction);
        direction.SelectionChanged += (_, _) => NotifyChanged();
        if (backgroundMedia is not null)
        {
            var motion = theme.MessageMotion ?? new();
            animateMessages = new CheckBox { Content = Ui.T("Анимировать сообщения"), IsChecked = theme.AnimateMessages,
                Margin = new Thickness(0, 8, 0, 4) };
            AddRow("", animateMessages);
            var offset = theme.NewestAtTop ? -32 : 32;
            enterEffect = AddMotionChoice("Появление сообщения", (int)motion.Enter, "Сдвиг", "Масштаб", "Прозрачность");
            enterEffect.Margin = new Thickness(0, 12, 0, 4);
            entrance = AddTransition(enterEffect, motion.Entrance ?? new(theme.MessageTransitionMs, OffsetY: offset));
            exitEffect = AddMotionChoice("Исчезновение сообщения", (int)motion.Exit, "Сдвиг", "Масштаб", "Прозрачность");
            exitEffect.Margin = new Thickness(0, 16, 0, 4);
            departure = AddTransition(exitEffect, motion.Departure ?? new(theme.MessageTransitionMs, OffsetY: -offset));
            animateMove = new CheckBox { Content = Ui.T("Плавно сдвигать оставшиеся строки"), IsChecked = motion.AnimateMove,
                Margin = new Thickness(0, 16, 0, 4) };
            AddRow("", animateMove);
            moveDuration = AddSlider(Ui.T("Длительность сдвига (мс)"), motion.MoveDurationMs, 100, 2000, animateMove);
            moveCurve = AddMotionChoice("Кривая сдвига", (int)motion.MoveCurve, CurveNames);
            animateMove.Checked += (_, _) => NotifyChanged();
            animateMove.Unchecked += (_, _) => NotifyChanged();
            animateMessages.Checked += (_, _) => NotifyChanged();
            animateMessages.Unchecked += (_, _) => NotifyChanged();
        }
        foreach (var field in new[] { text, name, background })
            field.TextChanged += (_, _) => NotifyChanged();
    }

    private ChatTheme Read()
    {
        var family = font.SelectedItem as string ?? "Segoe UI";
        var backdrop = ReadColor(background);
        return new ChatTheme(family, (int)size.Value, ReadColor(text).ToString(), ReadColor(name).ToString(),
            backdrop.ToString(), (int)Math.Round(backdrop.A * 100.0 / 255), (int)rows.Value,
            (int)radius.Value, twitchNameColor.IsChecked == true, hideExpiredMessages.IsChecked == true,
            (int)messageTimeout.Value, direction.SelectedIndex == 0, animateMessages?.IsChecked == true,
            legacyTransitionDuration,
            enterEffect is null ? null : new ObsMessageMotion((ObsMessageEffect)enterEffect.SelectedIndex,
                (ObsMessageEffect)exitEffect!.SelectedIndex, animateMove!.IsChecked == true,
                (int)moveDuration!.Value, (ObsMoveCurve)moveCurve!.SelectedIndex, entrance!(), departure!()));
    }

    private Func<ObsTransition> AddTransition(ComboBox effect, ObsTransition profile)
    {
        var duration = AddSlider(Ui.T("Длительность (мс)"), profile.DurationMs, 100, 5000);
        var delay = AddSlider(Ui.T("Задержка (мс)"), profile.DelayMs, 0, 2000);
        var curve = AddMotionChoice("Кривая перехода", (int)profile.Curve, CurveNames);
        var parameters = new List<(UIElement Element, ObsMessageEffect Effect)>();
        Slider Parameter(string label, int value, int min, int max, ObsMessageEffect kind)
        {
            var first = Children.Count;
            var slider = AddSlider(Ui.T(label), value, min, max);
            foreach (UIElement element in Children.Cast<UIElement>().Skip(first))
                parameters.Add((element, kind));
            return slider;
        }

        var x = Parameter("Сдвиг X (пкс)", profile.OffsetX, -500, 500, ObsMessageEffect.Slide);
        var y = Parameter("Сдвиг Y (пкс)", profile.OffsetY, -500, 500, ObsMessageEffect.Slide);
        var scale = Parameter("Масштаб на краю (%)", profile.Scale, 0, 200, ObsMessageEffect.Scale);
        var originX = Parameter("Точка масштаба X (%)", profile.OriginX, 0, 100, ObsMessageEffect.Scale);
        var originY = Parameter("Точка масштаба Y (%)", profile.OriginY, 0, 100, ObsMessageEffect.Scale);
        var opacity = Parameter("Непрозрачность на краю (%)", profile.Opacity, 0, 100, ObsMessageEffect.Fade);
        void UpdateVisibility()
        {
            foreach (var (element, kind) in parameters)
                element.Visibility = effect.SelectedIndex == (int)kind ? Visibility.Visible : Visibility.Collapsed;
        }
        effect.SelectionChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
        return () => new((int)duration.Value, (int)delay.Value, (ObsMoveCurve)curve.SelectedIndex,
            (int)x.Value, (int)y.Value, (int)scale.Value, (int)originX.Value, (int)originY.Value, (int)opacity.Value);
    }

    private ComboBox AddMotionChoice(string label, int selected, params string[] choices)
    {
        var picker = new ComboBox { ItemsSource = choices.Select(Ui.T).ToArray(),
            SelectedIndex = Math.Clamp(selected, 0, choices.Length - 1) };
        AddRow(Ui.T(label), picker);
        picker.SelectionChanged += (_, _) => NotifyChanged();
        return picker;
    }

    private void NotifyChanged()
    {
        ChatTheme theme;
        try
        {
            theme = Read();
        }
        catch (FormatException)
        {
            return;
        }
        Changed?.Invoke(theme);
    }

    private static Color ReadColor(TextBox field) => ChatAppearance.TryColor(field.Text.Trim(), out var color)
        ? color : throw new FormatException(Ui.T("Цвет должен быть в формате #RRGGBB или #AARRGGBB."));

    private ComboBox AddFontPicker(string value)
    {
        var items = Fonts.SystemFontFamilies.Select(family => family.Source).Append(value)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var picker = new ComboBox { ItemsSource = items, IsTextSearchEnabled = true, Margin = new Thickness(0, 2, 0, 2) };
        picker.SelectedItem = items.First(name => string.Equals(name, value, StringComparison.CurrentCultureIgnoreCase));
        System.Windows.Automation.AutomationProperties.SetName(picker, Ui.T("Шрифт"));
        AddRow(Ui.T("Шрифт"), picker);
        return picker;
    }
    private Slider AddSlider(string label, int value, int min, int max, CheckBox? showWhen = null)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value, TickFrequency = 1,
            IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 5,
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var amount = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Text = $"{(int)slider.Value}" };
        slider.ValueChanged += (_, _) =>
        {
            amount.Text = $"{(int)slider.Value}";
            NotifyChanged();
        };
        var panel = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(amount, Dock.Right);
        panel.Children.Add(amount);
        panel.Children.Add(slider);
        var caption = AddRow(label, panel);
        if (showWhen is not null)
        {
            var visibility = new Binding(nameof(CheckBox.IsChecked)) { Source = showWhen, Converter = new BooleanToVisibilityConverter() };
            panel.SetBinding(VisibilityProperty, visibility);
            caption.SetBinding(VisibilityProperty, visibility);
        }
        return slider;
    }

    private TextBox AddColor(string label, string value)
    {
        var field = CreateField(value, 9);
        field.MinWidth = 100;
        field.ToolTip = Ui.T("Цвет в формате #RRGGBB или #AARRGGBB");
        var swatch = new Border { Width = 22, Height = 16, CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        swatch.SetResourceReference(Border.BorderBrushProperty, "FluentStroke");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatch);
        content.Children.Add(new TextBlock { Text = Ui.T("Выбрать цвет") });
        var button = new Button { Content = content, Margin = new Thickness(8, 2, 0, 2), Padding = new Thickness(10, 4, 10, 4) };
        System.Windows.Automation.AutomationProperties.SetName(button, Ui.F("Выбрать: {0}", label.ToLowerInvariant()));

        void UpdateSwatch()
        {
            var hex = field.Text.Trim();
            swatch.Background = ChatAppearance.TryColor(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
        }

        field.TextChanged += (_, _) => UpdateSwatch();
        UpdateSwatch();
        button.Click += (_, _) =>
        {
            var hex = field.Text.Trim();
            if (Window.GetWindow(this) is not { } owner)
                return;
            var initial = ChatAppearance.TryColor(hex, out var color) ? color : Colors.White;
            var dialog = new ColorPickerWindow(owner, label, initial);
            if (dialog.ShowDialog() == true)
                field.Text = dialog.SelectedColor.ToString();
        };

        var group = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        group.Children.Add(button);
        group.Children.Add(field);
        AddRow(label, group);
        return field;
    }

    private static TextBox CreateField(string value, int maxLength) =>
        new() { Text = value, Margin = new Thickness(0, 2, 0, 2), MaxLength = maxLength };

    private TextBlock AddRow(string label, UIElement field)
    {
        var row = RowDefinitions.Count;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        SetRow(caption, row);
        SetRow(field, row);
        SetColumn(field, 1);
        Children.Add(caption);
        Children.Add(field);
        return caption;
    }


}

