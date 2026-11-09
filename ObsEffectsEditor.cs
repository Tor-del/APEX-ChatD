using System.Windows;
using System.Windows.Controls;

namespace TwitchChatAgent;

internal sealed class ObsEffectsEditor : StackPanel
{
    private readonly StackPanel list = new() { Margin = new Thickness(12, 5, 0, 0) };
    public ObsEffectSettings Value { get; private set; }
    public event Action<ObsEffectSettings>? Changed;

    public ObsEffectsEditor(ObsEffectSettings settings)
    {
        Value = settings;
        var enable = Toggle(this, "Постобработка", settings.Enabled, enabled =>
        {
            list.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            Update(Value with { Enabled = enabled });
        });
        enable.Margin = new Thickness(0, 10, 0, 0);
        list.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(list);

        var chromatic = Section("Хроматическая аберрация", settings.Chromatic.Enabled,
            enabled => ChangeChromatic(value => value with { Enabled = enabled }));
        Target(chromatic, settings.Chromatic.Target, target => ChangeChromatic(value => value with { Target = target }));
        Slider(chromatic, "Сдвиг X (пкс)", settings.Chromatic.OffsetX, -80, 80, next => ChangeChromatic(value => value with { OffsetX = next }));
        Slider(chromatic, "Сдвиг Y (пкс)", settings.Chromatic.OffsetY, -80, 80, next => ChangeChromatic(value => value with { OffsetY = next }));
        Toggle(chromatic, "Усиление к краям", settings.Chromatic.Radial, enabled => ChangeChromatic(value => value with { Radial = enabled }));
        Slider(chromatic, "Радиальное смещение (%)", settings.Chromatic.Modulation, 0, 100,
            next => ChangeChromatic(value => value with { Modulation = next }));
        Slider(chromatic, "Интенсивность (%)", settings.Chromatic.Intensity, 0, 100,
            next => ChangeChromatic(value => value with { Intensity = next }));

        var glitch = Section("Глич", settings.Glitch.Enabled, enabled => ChangeGlitch(value => value with { Enabled = enabled }));
        Target(glitch, settings.Glitch.Target, target => ChangeGlitch(value => value with { Target = target }));
        Choices(glitch, "Срабатывание", ["При новом сообщении", "Случайно"], (int)settings.Glitch.Trigger,
            selected => ChangeGlitch(value => value with { Trigger = (GlitchTrigger)selected }));
        Slider(glitch, "Слабое искажение (%)", settings.Glitch.Weak, 0, 100, next => ChangeGlitch(value => value with { Weak = next }));
        Slider(glitch, "Сильное искажение (%)", settings.Glitch.Strong, 0, 100, next => ChangeGlitch(value => value with { Strong = next }));
        Slider(glitch, "Вероятность сильного сбоя (%)", 100 - settings.Glitch.Threshold, 0, 100,
            next => ChangeGlitch(value => value with { Threshold = 100 - next }));
        Slider(glitch, "Мин. длительность (мс)", settings.Glitch.MinDuration, 20, 3000,
            next => ChangeGlitch(value => value with { MinDuration = next }), 20);
        Slider(glitch, "Макс. длительность (мс)", settings.Glitch.MaxDuration, 20, 3000,
            next => ChangeGlitch(value => value with { MaxDuration = next }), 20);
        Slider(glitch, "Мин. интервал (мс)", settings.Glitch.MinInterval, 100, 20000,
            next => ChangeGlitch(value => value with { MinInterval = next }), 100);
        Slider(glitch, "Макс. интервал (мс)", settings.Glitch.MaxInterval, 100, 20000,
            next => ChangeGlitch(value => value with { MaxInterval = next }), 100);
        Slider(glitch, "Размер блоков (%)", settings.Glitch.Columns, 1, 100, next => ChangeGlitch(value => value with { Columns = next }));
        Slider(glitch, "Размер карты шума", settings.Glitch.NoiseSize, 16, 256,
            next => ChangeGlitch(value => value with { NoiseSize = next }), 16);
        Slider(glitch, "Цветовой сдвиг (пкс)", settings.Glitch.ColorShift, 0, 50, next => ChangeGlitch(value => value with { ColorShift = next }));

        var lines = Section("Сканлинии", settings.Scanlines.Enabled,
            enabled => ChangeScanlines(value => value with { Enabled = enabled }));
        Target(lines, settings.Scanlines.Target, target => ChangeScanlines(value => value with { Target = target }));
        Slider(lines, "Плотность (%)", settings.Scanlines.Density, 5, 400, next => ChangeScanlines(value => value with { Density = next }));
        Slider(lines, "Интенсивность (%)", settings.Scanlines.Intensity, 0, 100, next => ChangeScanlines(value => value with { Intensity = next }));
        Slider(lines, "Скорость прокрутки (%)", settings.Scanlines.Speed, -200, 200,
            next => ChangeScanlines(value => value with { Speed = next }));
    }

    private void Update(ObsEffectSettings value)
    {
        Value = value;
        Changed?.Invoke(value);
    }

    private void ChangeChromatic(Func<ChromaticSettings, ChromaticSettings> change) => Update(Value with { Chromatic = change(Value.Chromatic) });
    private void ChangeGlitch(Func<GlitchSettings, GlitchSettings> change) => Update(Value with { Glitch = change(Value.Glitch) });
    private void ChangeScanlines(Func<ScanlineSettings, ScanlineSettings> change) => Update(Value with { Scanlines = change(Value.Scanlines) });

    private StackPanel Section(string title, bool enabled, Action<bool> change)
    {
        var content = new StackPanel();
        var toggle = CreateToggle(enabled, change);
        System.Windows.Automation.AutomationProperties.SetName(toggle, Ui.T(title));
        toggle.ToolTip = Ui.T(title);
        var card = new Expander { Header = Ui.T(title), Content = content, Tag = toggle, Margin = new Thickness(0, 4, 0, 4) };
        card.SetResourceReference(StyleProperty, "EffectExpander");
        list.Children.Add(card);
        return content;
    }

    private static void Target(Panel panel, EffectTarget target, Action<EffectTarget> change) =>
        Choices(panel, "Слой", ["Чат", "Фон", "Чат и фон"], (int)target, selected => change((EffectTarget)selected));

    private static CheckBox Toggle(Panel panel, string label, bool initial, Action<bool> change)
    {
        var box = CreateToggle(initial, change);
        box.Content = Ui.T(label);
        panel.Children.Add(box);
        return box;
    }

    private static CheckBox CreateToggle(bool initial, Action<bool> change)
    {
        var box = new CheckBox { IsChecked = initial };
        box.Checked += (_, _) => change(true);
        box.Unchecked += (_, _) => change(false);
        return box;
    }

    private static void Choices(Panel panel, string label, string[] items, int initial, Action<int> change)
    {
        var picker = new ComboBox { ItemsSource = items.Select(Ui.T).ToArray(), SelectedIndex = Math.Clamp(initial, 0, items.Length - 1) };
        picker.SelectionChanged += (_, _) => change(picker.SelectedIndex);
        panel.Children.Add(Row(label, picker));
    }

    private static void Slider(Panel panel, string label, int initial, int min, int max, Action<int> change, int step = 1)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, TickFrequency = step,
            IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5, IsMoveToPointEnabled = true };
        var amount = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, Text = $"{slider.Value:0}" };
        slider.ValueChanged += (_, _) =>
        {
            amount.Text = $"{slider.Value:0}";
            change((int)slider.Value);
        };
        panel.Children.Add(Row(label, slider, amount));
    }

    private static DockPanel Row(string label, UIElement input, TextBlock? amount = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        row.Children.Add(new TextBlock { Text = Ui.T(label), Width = 185, TextWrapping = TextWrapping.Wrap });
        if (amount is not null)
        {
            DockPanel.SetDock(amount, Dock.Right);
            row.Children.Add(amount);
        }
        System.Windows.Automation.AutomationProperties.SetName(input, Ui.T(label));
        row.Children.Add(input);
        return row;
    }
}
