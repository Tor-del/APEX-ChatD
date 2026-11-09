using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TwitchChatAgent;

internal sealed class ColorPickerWindow : Window
{
    private readonly TextBox hex = new() { MaxLength = 9 };
    private readonly Border swatch = new() { Height = 32, CornerRadius = new CornerRadius(4) };
    private readonly Button confirm = new() { Content = Ui.T("Выбрать"), IsDefault = true };
    private readonly Slider[] channels = new Slider[4];
    private readonly TextBlock[] amounts = new TextBlock[4];
    private bool updating;

    public Color SelectedColor { get; private set; }

    public ColorPickerWindow(Window owner, string title, Color initial)
    {
        Owner = owner;
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "FluentPanel");
        SetResourceReference(ForegroundProperty, "FluentText");
        SourceInitialized += (_, _) => ((App)Application.Current).Themes.ApplyTitleBar(this);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new Border { Child = swatch, Background = Checkerboard(), CornerRadius = new CornerRadius(4) });
        panel.Children.Add(new TextBlock { Text = Ui.T("#RRGGBB или #AARRGGBB"), Margin = new Thickness(0, 12, 0, 4) });
        panel.Children.Add(hex);
        var labels = new[] { Ui.T("Альфа (непрозрачность)"), Ui.T("Красный"), Ui.T("Зелёный"), Ui.T("Синий") };
        for (int index = 0; index < labels.Length; index++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var caption = new TextBlock { Text = labels[index], Width = 148 };
            var amount = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right };
            var slider = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true,
                SmallChange = 1, LargeChange = 16, IsMoveToPointEnabled = true };
            System.Windows.Automation.AutomationProperties.SetName(slider, labels[index]);
            DockPanel.SetDock(amount, Dock.Right);
            row.Children.Add(caption);
            row.Children.Add(amount);
            row.Children.Add(slider);
            channels[index] = slider;
            amounts[index] = amount;
            slider.ValueChanged += (_, _) =>
            {
                if (!updating)
                    SetColor(Color.FromArgb((byte)channels[0].Value, (byte)channels[1].Value,
                        (byte)channels[2].Value, (byte)channels[3].Value), updateHex: true);
            };
            panel.Children.Add(row);
        }

        var palette = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var preset in new[] { Colors.White, Colors.Black, Colors.Gray, Colors.Red, Colors.Orange,
                     Colors.Gold, Colors.LimeGreen, Colors.Cyan, Colors.DodgerBlue, Colors.MediumPurple })
        {
            var button = new Button { Width = 27, MinHeight = 25, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 5),
                Background = new SolidColorBrush(preset), ToolTip = preset.ToString() };
            button.Click += (_, _) => SetColor(Color.FromArgb(SelectedColor.A, preset.R, preset.G, preset.B), updateHex: true);
            palette.Children.Add(button);
        }
        panel.Children.Add(palette);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(confirm);
        actions.Children.Add(new Button { Content = Ui.T("Отмена"), IsCancel = true, Margin = new Thickness(0) });
        panel.Children.Add(actions);
        Content = panel;

        hex.TextChanged += (_, _) =>
        {
            if (updating)
                return;
            if (ChatAppearance.TryColor(hex.Text.Trim(), out var color))
                SetColor(color, updateHex: false);
            else
                confirm.IsEnabled = false;
        };
        SetColor(initial, updateHex: true);
    }

    private void SetColor(Color color, bool updateHex)
    {
        updating = true;
        try
        {
            SelectedColor = color;
            swatch.Background = new SolidColorBrush(color);
            var values = new[] { color.A, color.R, color.G, color.B };
            for (int index = 0; index < values.Length; index++)
            {
                channels[index].Value = values[index];
                amounts[index].Text = values[index].ToString();
            }
            if (updateHex)
                hex.Text = color.ToString();
            confirm.IsEnabled = true;
        }
        finally
        {
            updating = false;
        }
    }

    private static DrawingBrush Checkerboard()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(Brushes.Silver, null, new GeometryGroup
        {
            Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) }
        }));
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16) };
    }
}
