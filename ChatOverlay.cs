using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TwitchChatAgent;

internal sealed class ChatOverlay : Window
{
    private readonly Border surface = new();
    private readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly Dictionary<Guid, TextBlock> active = new();
    private ChatTheme? lastTheme;
    private readonly StackPanel rows = new() { Margin = new Thickness(12) };
    private readonly Button pin = new()
    {
        Content = "\uE718",
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 16, Width = 34, MinHeight = 30, Padding = new Thickness(3),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
        ToolTip = Ui.T("Закрепить положение")
    };
    public event Action? PinClicked;
    private bool editing;
    private int radius;

    public ChatOverlay(Settings settings)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;
        MinWidth = 180;
        MinHeight = 100;
        Width = settings.Width;
        Height = settings.Height;
        var bounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(settings.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        Top = Math.Clamp(settings.Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));

        scroll.Content = rows;
        System.Windows.Automation.AutomationProperties.SetName(pin, Ui.T("Закрепить положение"));
        pin.Click += (_, _) => PinClicked?.Invoke();
        surface.Child = new Grid { Children = { scroll, pin } };
        surface.SizeChanged += (_, _) => ClipSurface();
        Content = surface;
        PreviewMouseLeftButtonDown += MoveWindow;
    }

    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (!editing || e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is ResizeGrip || pin.IsMouseOver)
            return;
        e.Handled = true;
        DragMove();
    }

    public void SetInteraction(bool editingLayout)
    {
        editing = editingLayout;
        pin.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        rows.Margin = editing ? new Thickness(12, 12, 50, 12) : new Thickness(12);
        Cursor = editing ? Cursors.SizeAll : null;
        ResizeMode = editing ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        const int extendedStyle = -20;
        const int transparent = 0x20;
        const int noActivate = 0x08000000;
        int original = GetWindowLong(handle, extendedStyle);
        int style = original;
        style = editing ? style & ~noActivate : style | noActivate;
        style = editing ? style & ~transparent : style | transparent;
        if (style != original)
            SetWindowLong(handle, extendedStyle, style);
    }

    public void Render(ChatTheme theme, IReadOnlyList<ChatMessage> messages)
    {
        radius = theme.Radius;
        ClipSurface();
        var styleChanged = lastTheme is null || (lastTheme.Font, lastTheme.Size, lastTheme.Text, lastTheme.Name, lastTheme.UseTwitchNameColor) !=
            (theme.Font, theme.Size, theme.Text, theme.Name, theme.UseTwitchNameColor);
        lastTheme = theme;

        var keep = messages.Select(message => message.DisplayId).ToHashSet();
        foreach (var id in active.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            var row = active[id];
            active.Remove(id);
            rows.Children.Remove(row);
        }

        rows.VerticalAlignment = theme.NewestAtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var ordered = theme.NewestAtTop ? messages.Reverse() : messages;
        var index = 0;
        foreach (var message in ordered)
        {
            var isNew = !active.TryGetValue(message.DisplayId, out var row);
            if (isNew || styleChanged)
            {
                if (row is not null)
                    rows.Children.Remove(row);
                active[message.DisplayId] = row = ChatAppearance.CreateRow(theme, message);
            }

            if (rows.Children.IndexOf(row!) != index)
            {
                rows.Children.Remove(row!);
                rows.Children.Insert(index, row!);
            }
            index++;
        }

        if (theme.NewestAtTop)
            scroll.ScrollToHome();
        else
            scroll.ScrollToEnd();
        UpdateBackground();
    }

    private void UpdateBackground()
    {
        if (lastTheme is not { } theme)
            return;
        var background = ChatAppearance.BackgroundColor(theme);
        if (editing)
            background.A = Math.Max((byte)102, background.A);
        surface.Background = !editing && theme.HideExpiredMessages && active.Count == 0
            ? Brushes.Transparent : new SolidColorBrush(background);
    }

    private void ClipSurface()
    {
        var size = surface.RenderSize;
        var corner = Math.Min(radius, Math.Min(size.Width, size.Height) / 2);
        surface.Clip = new RectangleGeometry(new Rect(size), corner, corner);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
