using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TwitchChatAgent;

internal sealed class ObsEffectsEditor : StackPanel
{
    private static readonly string[] Names = ["Хроматическая аберрация", "Глич", "Сканлинии"];
    private readonly StackPanel list = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel cards = new();
    private readonly Button add = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
    public ObsEffectSettings Value { get; private set; }
    public event Action<ObsEffectSettings>? Changed;

    public ObsEffectsEditor(ObsEffectSettings settings)
    {
        var legacy = settings.Items ?? (settings == new ObsEffectSettings() ? [] : Enum.GetValues<ObsPostEffect>());
        Value = settings with { Items = legacy };
        foreach (var kind in Enum.GetValues<ObsPostEffect>().Where(kind => !legacy.Contains(kind)))
            Value = SetEnabled(Value, kind, false);

        var enable = Toggle(this, "Постобработка", settings.Enabled, enabled =>
        {
            list.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            Update(Value with { Enabled = enabled });
        });
        enable.Margin = new Thickness(0, 10, 0, 0);
        list.Visibility = settings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        Children.Add(list);
        list.Children.Add(cards);
        foreach (var kind in legacy) AddCard(kind);

        add.Content = Ui.T("Добавить эффект");
        list.Children.Add(add);
        add.IsEnabled = legacy.Length < Names.Length;
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var kind in Enum.GetValues<ObsPostEffect>().Where(kind => !Value.Items!.Contains(kind)))
            {
                var item = new MenuItem { Header = Ui.T(Names[(int)kind]) };
                item.Click += (_, _) =>
                {
                    var next = Value with { Items = [.. Value.Items!, kind] };
                    next = kind switch
                    {
                        ObsPostEffect.Chromatic => next with { Chromatic = new(Enabled: true) },
                        ObsPostEffect.Glitch => next with { Glitch = new(Enabled: true) },
                        _ => next with { Scanlines = new(Enabled: true) }
                    };
                    Update(next);
                    AddCard(kind, expanded: true);
                };
                menu.Items.Add(item);
            }
            add.ContextMenu = menu;
            menu.IsOpen = true;
        };
    }

    private void AddCard(ObsPostEffect kind, bool expanded = false)
    {
        var enabled = kind switch
        {
            ObsPostEffect.Chromatic => Value.Chromatic.Enabled,
            ObsPostEffect.Glitch => Value.Glitch.Enabled,
            _ => Value.Scanlines.Enabled
        };
        var content = new StackPanel { IsEnabled = enabled };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = EffectCardButtons.Toggle();
        var remove = EffectCardButtons.Remove();
        actions.Children.Add(toggle);
        actions.Children.Add(remove);
        var card = new Expander { Header = Ui.T(Names[(int)kind]), Tag = actions, Content = content,
            IsExpanded = expanded, Margin = new Thickness(0, 0, 0, 5) };
        card.SetResourceReference(StyleProperty, "EffectExpander");
        cards.Children.Add(card);
        void UpdateToggle()
        {
            EffectCardButtons.SetEnabled(toggle, enabled);
            content.IsEnabled = enabled;
        }
        UpdateToggle();
        toggle.Click += (_, _) =>
        {
            enabled = !enabled;
            UpdateToggle();
            Update(SetEnabled(Value, kind, enabled));
        };
        remove.Click += (_, _) =>
        {
            cards.Children.Remove(card);
            Update(SetEnabled(Value, kind, false) with { Items = Value.Items!.Where(item => item != kind).ToArray() });
        };
        switch (kind)
        {
            case ObsPostEffect.Chromatic: AddChromatic(content, Value.Chromatic); break;
            case ObsPostEffect.Glitch: AddGlitch(content, Value.Glitch); break;
            case ObsPostEffect.Scanlines: AddScanlines(content, Value.Scanlines); break;
        }
    }

    private static ObsEffectSettings SetEnabled(ObsEffectSettings value, ObsPostEffect kind, bool enabled) => kind switch
    {
        ObsPostEffect.Chromatic => value with { Chromatic = value.Chromatic with { Enabled = enabled } },
        ObsPostEffect.Glitch => value with { Glitch = value.Glitch with { Enabled = enabled } },
        _ => value with { Scanlines = value.Scanlines with { Enabled = enabled } }
    };


    private void AddChromatic(StackPanel chromatic, ChromaticSettings settings)
    {
        Target(chromatic, settings.Target, target => ChangeChromatic(value => value with { Target = target }));
        Slider(chromatic, "Сдвиг X (пкс)", settings.OffsetX, -80, 80, next => ChangeChromatic(value => value with { OffsetX = next }));
        Slider(chromatic, "Сдвиг Y (пкс)", settings.OffsetY, -80, 80, next => ChangeChromatic(value => value with { OffsetY = next }));
        Toggle(chromatic, "Усиление к краям", settings.Radial, enabled => ChangeChromatic(value => value with { Radial = enabled }));
        Slider(chromatic, "Радиальное смещение (%)", settings.Modulation, 0, 100,
            next => ChangeChromatic(value => value with { Modulation = next }));
        Slider(chromatic, "Интенсивность (%)", settings.Intensity, 0, 100,
            next => ChangeChromatic(value => value with { Intensity = next }));
    }

    private void AddGlitch(StackPanel glitch, GlitchSettings settings)
    {
        Target(glitch, settings.Target, target => ChangeGlitch(value => value with { Target = target }));
        Choices(glitch, "Срабатывание", ["При новом сообщении", "Случайно"], (int)settings.Trigger,
            selected => ChangeGlitch(value => value with { Trigger = (GlitchTrigger)selected }));
        Slider(glitch, "Слабое искажение (%)", settings.Weak, 0, 100, next => ChangeGlitch(value => value with { Weak = next }));
        Slider(glitch, "Сильное искажение (%)", settings.Strong, 0, 100, next => ChangeGlitch(value => value with { Strong = next }));
        Slider(glitch, "Вероятность сильного сбоя (%)", 100 - settings.Threshold, 0, 100,
            next => ChangeGlitch(value => value with { Threshold = 100 - next }));
        Slider(glitch, "Мин. длительность (мс)", settings.MinDuration, 20, 3000,
            next => ChangeGlitch(value => value with { MinDuration = next }), 20);
        Slider(glitch, "Макс. длительность (мс)", settings.MaxDuration, 20, 3000,
            next => ChangeGlitch(value => value with { MaxDuration = next }), 20);
        Slider(glitch, "Мин. интервал (мс)", settings.MinInterval, 100, 20000,
            next => ChangeGlitch(value => value with { MinInterval = next }), 100);
        Slider(glitch, "Макс. интервал (мс)", settings.MaxInterval, 100, 20000,
            next => ChangeGlitch(value => value with { MaxInterval = next }), 100);
        Slider(glitch, "Размер блоков (%)", settings.Columns, 1, 100, next => ChangeGlitch(value => value with { Columns = next }));
        Slider(glitch, "Размер карты шума", settings.NoiseSize, 16, 256,
            next => ChangeGlitch(value => value with { NoiseSize = next }), 16);
        Slider(glitch, "Цветовой сдвиг (пкс)", settings.ColorShift, 0, 50, next => ChangeGlitch(value => value with { ColorShift = next }));
    }

    private void AddScanlines(StackPanel lines, ScanlineSettings settings)
    {
        Target(lines, settings.Target, target => ChangeScanlines(value => value with { Target = target }));
        Slider(lines, "Плотность (%)", settings.Density, 5, 400, next => ChangeScanlines(value => value with { Density = next }));
        Slider(lines, "Интенсивность (%)", settings.Intensity, 0, 100, next => ChangeScanlines(value => value with { Intensity = next }));
        Slider(lines, "Скорость прокрутки (%)", settings.Speed, -200, 200,
            next => ChangeScanlines(value => value with { Speed = next }));
    }

    private void Update(ObsEffectSettings value)
    {
        Value = value;
        add.IsEnabled = Value.Items!.Length < Names.Length;
        Changed?.Invoke(value);
    }

    private void ChangeChromatic(Func<ChromaticSettings, ChromaticSettings> change) => Update(Value with { Chromatic = change(Value.Chromatic) });
    private void ChangeGlitch(Func<GlitchSettings, GlitchSettings> change) => Update(Value with { Glitch = change(Value.Glitch) });
    private void ChangeScanlines(Func<ScanlineSettings, ScanlineSettings> change) => Update(Value with { Scanlines = change(Value.Scanlines) });

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

    internal static void Choices(Panel panel, string label, string[] items, int initial, Action<int> change)
    {
        var picker = new ComboBox { ItemsSource = items.Select(Ui.T).ToArray(), SelectedIndex = Math.Clamp(initial, 0, items.Length - 1) };
        picker.SelectionChanged += (_, _) => change(picker.SelectedIndex);
        panel.Children.Add(Row(label, picker));
    }

    internal static void Slider(Panel panel, string label, int initial, int min, int max, Action<int> change, int step = 1)
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
