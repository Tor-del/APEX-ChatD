using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TwitchChatAgent;

internal enum QueueAction { Pause = 1, Skip, Clear, ClearDisplayed }

internal sealed record QueueShortcut(uint Modifiers, uint Key)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsMouse => Key is 4 or 5 or 6;

    public override string ToString() => string.Join(" + ", new[]
    {
        (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null,
        (Modifiers & 4) != 0 ? "Shift" : null, (Modifiers & 8) != 0 ? "Win" : null,
        Key switch { 4 => Ui.T("Средняя кнопка мыши"), 5 => "Mouse X1", 6 => "Mouse X2",
            _ => ((System.Windows.Forms.Keys)Key).ToString() }
    }.Where(part => part is not null));
}

internal sealed record QueueBindings(QueueShortcut? Pause = null, QueueShortcut? Skip = null,
    QueueShortcut? Clear = null, QueueShortcut? ClearDisplayed = null)
{
    public QueueShortcut? Get(QueueAction action) => action switch
    {
        QueueAction.Pause => Pause,
        QueueAction.Skip => Skip,
        QueueAction.Clear => Clear,
        QueueAction.ClearDisplayed => ClearDisplayed,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    public QueueBindings Set(QueueAction action, QueueShortcut? key) => action switch
    {
        QueueAction.Pause => this with { Pause = key },
        QueueAction.Skip => this with { Skip = key },
        QueueAction.Clear => this with { Clear = key },
        QueueAction.ClearDisplayed => this with { ClearDisplayed = key },
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

internal sealed class QueueHotkeys : IDisposable
{
    private readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    private readonly Thread thread;
    private readonly Dispatcher worker;
    private readonly HwndSource source;
    private readonly MouseProc mouseProc;
    private readonly Dictionary<int, QueueShortcut> registered = new();
    private readonly HashSet<uint> consumedMouse = new();
    private IntPtr mouseHook;
    private bool suspended;
    private bool disposed;
    private IReadOnlyDictionary<int, QueueShortcut> bindings = new Dictionary<int, QueueShortcut>();
    public event Action<int>? Triggered;

    public QueueHotkeys()
    {
        mouseProc = OnMouse;
        var ready = new TaskCompletionSource<(Dispatcher, HwndSource)>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                using var window = new HwndSource(new HwndSourceParameters("APEX Chatd Hotkeys") { ParentWindow = new IntPtr(-3) });
                window.AddHook(OnMessage);
                ready.SetResult((Dispatcher.CurrentDispatcher, window));
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "APEX Chatd hotkeys" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        (worker, source) = ready.Task.GetAwaiter().GetResult();
    }

    public string[] Configure(IReadOnlyDictionary<int, QueueShortcut> value) => worker.Invoke(() =>
    {
        bindings = value;
        return Apply();
    });

    public string[] Suspend(bool value) => worker.Invoke(() =>
    {
        if (suspended == value)
            return Array.Empty<string>();
        suspended = value;
        return Apply();
    });

    public void Validate(int action, QueueShortcut shortcut) => worker.Invoke(() =>
    {
        if (!IsValid(shortcut))
            throw new ArgumentException(Ui.T("Эта клавиша зарезервирована или не подходит для сочетания."));
        if (bindings.Any(other => other.Key != action && other.Value == shortcut))
            throw new ArgumentException(Ui.T("Это сочетание уже назначено другой команде."));
        if (shortcut.IsMouse || registered.GetValueOrDefault(action) == shortcut)
            return;

        const int probeId = 0;
        if (!RegisterHotKey(source.Handle, probeId, shortcut.Modifiers | 0x4000, shortcut.Key))
            throw new InvalidOperationException(Busy(shortcut));
        UnregisterHotKey(source.Handle, probeId);
    });

    private static bool IsValid(QueueShortcut shortcut) => shortcut.Modifiers <= 15 &&
        shortcut.Key is > 0 and < 255 and not (1 or 2 or 3 or 0x10 or 0x11 or 0x12 or 0x1B or 0x5B or 0x5C or 0x7B or >= 0xA0 and <= 0xA5);

    private static string Busy(QueueShortcut shortcut) => Ui.F("Windows не смогла зарегистрировать сочетание {0}.", shortcut);

    private string[] Apply()
    {
        Unregister();
        if (suspended)
            return [];

        var errors = new List<string>();
        foreach (var (action, key) in bindings)
        {
            if (!IsValid(key) || registered.ContainsValue(key))
            {
                errors.Add(Busy(key));
                continue;
            }
            if (key.IsMouse)
            {
                if (mouseHook == IntPtr.Zero)
                    mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
                if (mouseHook == IntPtr.Zero)
                {
                    errors.Add(Busy(key));
                    continue;
                }
            }
            else if (!RegisterHotKey(source.Handle, action, key.Modifiers | 0x4000, key.Key))
            {
                errors.Add(Busy(key));
                continue;
            }
            registered[action] = key;
        }
        return errors.ToArray();
    }

    private void Unregister()
    {
        foreach (var (action, key) in registered)
            if (!key.IsMouse)
                UnregisterHotKey(source.Handle, action);
        registered.Clear();
        consumedMouse.Clear();
        if (mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(mouseHook);
        mouseHook = IntPtr.Zero;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312)
            return IntPtr.Zero;
        var action = wParam.ToInt32();
        if (registered.TryGetValue(action, out var shortcut) && !shortcut.IsMouse)
        {
            handled = true;
            Dispatch(action);
        }
        return IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var kind = message.ToInt32();
            uint key = kind switch
            {
                0x0207 or 0x0208 => 4,
                0x020B or 0x020C => (Marshal.ReadInt32(data, 8) >> 16) == 1 ? 5u : 6u,
                _ => 0
            };
            if (key != 0)
            {
                if (kind is 0x0208 or 0x020C)
                {
                    if (consumedMouse.Remove(key))
                        return new IntPtr(1);
                }
                else
                {
                    var mods = (Down(0x12) ? 1u : 0) | (Down(0x11) ? 2u : 0) |
                        (Down(0x10) ? 4u : 0) | (Down(0x5B) || Down(0x5C) ? 8u : 0);
                    foreach (var (action, shortcut) in registered)
                    {
                        if (shortcut.Key != key || shortcut.Modifiers != mods)
                            continue;
                        consumedMouse.Add(key);
                        Dispatch(action);
                        return new IntPtr(1);
                    }
                }
            }
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }

    private void Dispatch(int action) => ui.BeginInvoke(new Action(() =>
    {
        if (!disposed)
            Triggered?.Invoke(action);
    }));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        worker.Invoke(() => { Unregister(); worker.BeginInvokeShutdown(DispatcherPriority.Send); });
        thread.Join();
    }

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    private delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
