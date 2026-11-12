using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TwitchChatAgent;

public partial class MainWindow
{
    private readonly Dictionary<int, (PluginManager.Shortcut Shortcut, TextBox Field)> pluginShortcutFields = [];

    private void RefreshPluginShortcuts()
    {
        PluginShortcutList.Children.Clear();
        pluginShortcutFields.Clear();
        var shortcuts = plugins?.Shortcuts.ToArray() ?? [];
        if (shortcuts.Length == 0) return;

        var heading = new TextBlock
        {
            Text = P("Горячие клавиши плагинов", "Plugin shortcuts"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 0)
        };
        PluginShortcutList.Children.Add(heading);
        foreach (var shortcut in shortcuts)
        {
            // Win32 IDs are temporary. Settings use the stable plugin and command IDs.
            var id = 100 + pluginShortcutFields.Count;
            var title = $"{shortcut.PluginName} · {shortcut.Title}";
            PluginShortcutList.Children.Add(new TextBlock
            {
                Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8)
            });
            var field = new TextBox { Tag = id, IsReadOnly = true };
            AutomationProperties.SetName(field, title);
            field.PreviewKeyDown += ShortcutKeyDown;
            field.PreviewMouseDown += ShortcutMouseDown;
            field.GotKeyboardFocus += ShortcutFocused;
            field.LostKeyboardFocus += ShortcutUnfocused;
            PluginShortcutList.Children.Add(field);
            pluginShortcutFields.Add(id, (shortcut, field));
        }
        ConfigureHotkeys();
        UpdateShortcutFields();
    }

    private int? ShortcutId(TextBox field) => field.Tag switch
    {
        int id when pluginShortcutFields.TryGetValue(id, out var entry) && entry.Field == field => id,
        string name when Enum.TryParse<QueueAction>(name, out var action) && Enum.IsDefined(action) => (int)action,
        _ => null
    };

    private void ConfigureHotkeys()
    {
        if (hotkeys is null) return;
        var bindings = new Dictionary<int, QueueShortcut>();
        foreach (var action in Enum.GetValues<QueueAction>())
            if (settings.SpeechHotkeys.Get(action) is { } key)
                bindings.Add((int)action, key);
        foreach (var (id, entry) in pluginShortcutFields)
            if (settings.PluginHotkeys.GetValueOrDefault(entry.Shortcut.Id) is { } key)
                bindings.Add(id, key);
        HotkeyStatus.Text = string.Join(Environment.NewLine, hotkeys.Configure(bindings));
    }

    private async void OnShortcutTriggered(int id)
    {
        if (pluginShortcutFields.TryGetValue(id, out var entry))
        {
            try { await entry.Shortcut.Invoke(); }
            catch (OperationCanceledException) when (exiting) { }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
                if (!exiting)
                    HotkeyStatus.Text = $"{entry.Shortcut.PluginName} · {entry.Shortcut.Title}: {ex.GetBaseException().Message}";
            }
            return;
        }
        OnQueueAction((QueueAction)id);
    }
}
