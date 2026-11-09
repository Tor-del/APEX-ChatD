using System.Windows;

namespace TwitchChatAgent;

public partial class App : Application
{
    private Mutex? instance;
    internal ThemeManager Themes { get; }
    internal bool RestartRequested { get; set; }

    public App()
    {
        Themes = new ThemeManager(this);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try { Ui.Language = Settings.Load().Language; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

        var autostart = e.Args.Contains(StartupRegistration.AutostartArgument, StringComparer.OrdinalIgnoreCase);
        var startInTray = e.Args.Contains(StartupRegistration.TrayArgument, StringComparer.OrdinalIgnoreCase);
        instance = new Mutex(true, @"Local\TwitchChatAgent", out var first);
        if (!first)
        {
            instance.Dispose();
            instance = null;
            if (!startInTray && !autostart)
                MessageBox.Show(Ui.T("APEX Chatd уже работает. Открой его через значок в трее."), "APEX Chatd");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Contains("--show-settings", StringComparer.OrdinalIgnoreCase))
            window.Show();
        else if (!startInTray)
            window.ShowAtStartup(autostart);
        await window.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Themes.Dispose();
        instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
        if (RestartRequested)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(AppContext.BaseDirectory, "APEX Chatd.exe")) { UseShellExecute = true, Arguments = "--show-settings" });
    }
}
