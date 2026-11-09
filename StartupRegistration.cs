using Microsoft.Win32;

namespace TwitchChatAgent;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "APEX Chatd";
    private const string LegacyValueName = "TwitchChatAgent";
    public const string TrayArgument = "--tray";
    public const string AutostartArgument = "--autostart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return (key?.GetValue(ValueName) ?? key?.GetValue(LegacyValueName)) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            existing?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "APEX Chatd.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException(Ui.T("Для автозапуска нужен файл APEX Chatd.exe."), path);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{path}\" {AutostartArgument}", RegistryValueKind.String);
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }
}
