using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TwitchChatAgent;

internal sealed class ThemeEditor : Grid
{
    private Grid fields;
    private readonly ComboBox font;
    private readonly CheckBox twitchNameColor;
    private readonly Slider size;
    private readonly TextBox text;
    private readonly TextBox name;
    private readonly TextBox background;
    private readonly Slider rows;
    private readonly Slider? radius;
    private readonly CheckBox? messageBackground;
    private readonly TextBox? messageBackgroundColor, messageBorderColor;
    private readonly Slider? messageRadius, messagePadding, messageBorderWidth;
    private readonly CheckBox hideExpiredMessages;
    private readonly Slider messageTimeout;
    private readonly ComboBox direction;
    private readonly int legacyTransitionDuration;
    private readonly ObsAnimationEditor? entrance, departure, movement;
    private readonly ObsMessageMotion? originalMotion;
    public event Action<ChatTheme>? Changed;
    public ObsMessageMotion? MessageMotion => originalMotion is null ? null : originalMotion with
    {
        Entrance = entrance!.Value, Departure = departure!.Value, Movement = movement!.Value
    };

    public ThemeEditor(ChatTheme theme, FrameworkElement? backgroundMedia = null)
    {
        legacyTransitionDuration = theme.MessageTransitionMs;
        fields = this;
        if (backgroundMedia is null)
            AddFieldColumns(fields);
        else
            BeginSection("Стиль сообщения");
        font = AddFontPicker(theme.Font);
        size = AddSlider(Ui.T("Размер текста"), theme.Size, 10, 96);
        text = AddColor(Ui.T("Цвет текста"), theme.Text);
        name = AddColor(Ui.T("Цвет ника"), theme.Name);
        twitchNameColor = new CheckBox { Content = Ui.T("Использовать цвет ника из Twitch"), IsChecked = theme.UseTwitchNameColor,
            Margin = new Thickness(0, 5, 0, 5) };
        AddRow("", twitchNameColor);
        if (backgroundMedia is not null)
        {
            BeginSection("Фон сообщений");
            var style = theme.MessageStyle ?? new();
            messageBackground = new CheckBox { Content = Ui.T("Показывать фон сообщений"), IsChecked = style.Enabled,
                Margin = new Thickness(0, 0, 0, 4) };
            AddRow("", messageBackground);
            messageBackgroundColor = AddColor(Ui.T("Цвет фона сообщения"), style.Background, messageBackground);
            messageRadius = AddSlider(Ui.T("Скругление сообщения (пиксели)"), style.Radius, 0, 100, messageBackground);
            messagePadding = AddSlider(Ui.T("Внутренний отступ (пиксели)"), style.Padding, 0, 48, messageBackground);
            messageBorderColor = AddColor(Ui.T("Цвет рамки сообщения"), style.BorderColor, messageBackground);
            messageBorderWidth = AddSlider(Ui.T("Толщина рамки (пиксели)"), style.BorderWidth, 0, 16, messageBackground);
            messageBackground.Checked += (_, _) => NotifyChanged();
            messageBackground.Unchecked += (_, _) => NotifyChanged();
            messageBackgroundColor.TextChanged += (_, _) => NotifyChanged();
            messageBorderColor.TextChanged += (_, _) => NotifyChanged();
        }
        if (backgroundMedia is not null)
            BeginSection("Общий фон");
        background = AddColor(Ui.T("Цвет фона"), ChatAppearance.BackgroundColor(theme).ToString());
        if (backgroundMedia is not null)
        {
            AddRow("", backgroundMedia);
            BeginSection("Настройки чата");
        }
        else
            radius = AddSlider(Ui.T("Радиус скругления"), theme.Radius, 0, 100);
        rows = AddSlider(Ui.T("Лимит сообщений"), theme.Rows, 1, 50);
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
            originalMotion = motion;
            var offset = theme.NewestAtTop ? -32 : 32;
            var isNew = theme.MessageMotion is null && !theme.AnimateMessages;
            entrance = new ObsAnimationEditor(motion.Entrance ?? new(theme.MessageTransitionMs, OffsetY: offset),
                isNew ? ObsMessageEffects.None : (ObsMessageEffects)(1 << (int)motion.Enter));
            departure = new ObsAnimationEditor(motion.Departure ?? new(theme.MessageTransitionMs, OffsetY: -offset),
                isNew ? ObsMessageEffects.None : (ObsMessageEffects)(1 << (int)motion.Exit));
            movement = new ObsAnimationEditor(motion.Movement ?? new(motion.MoveDurationMs, Curve: motion.MoveCurve),
                !isNew && motion.AnimateMove ? ObsMessageEffects.Slide : ObsMessageEffects.None, moving: true);
            AddSection("Эффекты появления сообщений", entrance);
            AddSection("Эффекты исчезновения сообщений", departure);
            AddSection("Эффекты движения сообщений", movement);
            foreach (var editor in new[] { entrance, departure, movement })
                editor.Changed += NotifyChanged;
        }
        foreach (var field in new[] { text, name, background })
            field.TextChanged += (_, _) => NotifyChanged();
    }

    private static void AddFieldColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
    }

    private void BeginSection(string title)
    {
        fields = new Grid();
        AddFieldColumns(fields);
        AddSection(title, fields);
    }

    private void AddSection(string title, FrameworkElement content)
    {
        var heading = new TextBlock { Text = Ui.T(title) };
        heading.SetResourceReference(StyleProperty, "SettingsSectionTitle");
        var panel = new StackPanel();
        panel.Children.Add(heading);
        panel.Children.Add(content);
        var section = new Border { Child = panel };
        section.SetResourceReference(StyleProperty, "SettingsSection");
        SetRow(section, RowDefinitions.Count);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(section);
    }

    private ChatTheme Read()
    {
        var family = font.SelectedItem as string ?? "Segoe UI";
        var backdrop = ReadColor(background);
        var messageStyle = messageBackground is null ? null : new ObsMessageStyle(messageBackground.IsChecked == true,
            ReadColor(messageBackgroundColor!).ToString(), (int)messageRadius!.Value, (int)messagePadding!.Value,
            ReadColor(messageBorderColor!).ToString(), (int)messageBorderWidth!.Value);
        var motion = MessageMotion;
        var hasEffects = new[] { motion?.Entrance, motion?.Departure, motion?.Movement }
            .Any(transition => transition?.Items?.Any(item => item.Enabled) == true);
        return new ChatTheme(family, (int)size.Value, ReadColor(text).ToString(), ReadColor(name).ToString(),
            backdrop.ToString(), (int)Math.Round(backdrop.A * 100.0 / 255), (int)rows.Value,
            (int)(radius?.Value ?? 0), twitchNameColor.IsChecked == true, hideExpiredMessages.IsChecked == true,
            (int)messageTimeout.Value, direction.SelectedIndex == 0, hasEffects,
            legacyTransitionDuration, motion, messageStyle);
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
        AddRow(label, panel, showWhen);
        return slider;
    }

    private TextBox AddColor(string label, string value, CheckBox? showWhen = null)
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
        AddRow(label, group, showWhen);
        return field;
    }

    private static TextBox CreateField(string value, int maxLength) =>
        new() { Text = value, Margin = new Thickness(0, 2, 0, 2), MaxLength = maxLength };

    private void AddRow(string label, FrameworkElement field, CheckBox? showWhen = null)
    {
        var row = fields.RowDefinitions.Count;
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        SetRow(caption, row);
        SetRow(field, row);
        if (fields != this && label.Length == 0)
            SetColumnSpan(field, 2);
        else
        {
            SetColumn(field, 1);
            fields.Children.Add(caption);
        }
        fields.Children.Add(field);
        if (showWhen is not null)
        {
            var visibility = new Binding(nameof(CheckBox.IsChecked)) { Source = showWhen, Converter = new BooleanToVisibilityConverter() };
            field.SetBinding(VisibilityProperty, visibility);
            caption.SetBinding(VisibilityProperty, visibility);
        }
    }


}

