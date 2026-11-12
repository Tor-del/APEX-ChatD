using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ApexChatd.Plugins;

namespace ApexChatd.GitHubUpdates;

public sealed class UpdaterPlugin : Plugin
{
    private sealed record Preferences
    {
        public bool AutoCheckUpdates { get; set; } = true;
        public string? IgnoredUpdateVersion { get; set; }
        public string? DeferredUpdateVersion { get; set; }
        public DateTimeOffset? UpdateReminderAfter { get; set; }
    }

    private readonly DispatcherTimer updateTimer = new();
    private readonly GitHubUpdater updater = new();
    private IPluginContext context = null!;
    private CancellationToken updateLifetime;
    private Preferences settings = new();
    private Button CheckUpdatesButton = null!;
    private Button RollbackButton = null!;
    private TextBlock UpdateStatus = null!;
    private StackPanel panel = null!;
    private bool checkingUpdates;
    private bool exiting => updateLifetime.IsCancellationRequested;
    private string SettingsPath => Path.Combine(context.DataDirectory, "settings.json");

    public override void Register(IPluginContext context)
    {
        this.context = context;
        var russian = System.Globalization.CultureInfo.GetCultureInfo(context.Language).TwoLetterISOLanguageName == "ru";
        Ui.Translate = text => context.Translate(text, text, russian ? null : "en");
        if (File.Exists(SettingsPath))
            settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllBytes(SettingsPath)) ?? new();
        context.RegisterTab(Ui.T("Обновления"), () => panel);
    }

    public override Task StartAsync(CancellationToken stopping)
    {
        updateLifetime = stopping;
        panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = Ui.F("Версия {0}", context.AppVersion.ToString(3)), Margin = new Thickness(0, 0, 0, 8) });
        var auto = new CheckBox { Content = Ui.T("Автоматически проверять обновления"), IsChecked = settings.AutoCheckUpdates };
        auto.Checked += ChangeAutomaticChecks;
        auto.Unchecked += ChangeAutomaticChecks;
        panel.Children.Add(auto);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        CheckUpdatesButton = new Button { Content = Ui.T("Проверить обновления") };
        CheckUpdatesButton.Click += CheckUpdatesClicked;
        actions.Children.Add(CheckUpdatesButton);
        RollbackButton = new Button { Content = Ui.T("Откатиться к предыдущей версии"), Visibility = Visibility.Collapsed };
        RollbackButton.Click += RollbackClicked;
        actions.Children.Add(RollbackButton);
        panel.Children.Add(actions);
        UpdateStatus = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(UpdateStatus);
        try
        {
            if (FindPreviousVersion(GitHubUpdater.FindInstallation()) is not null)
                RollbackButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            context.Log(ex.Message);
        }
        updateTimer.Tick += CheckOnTimer;
        ScheduleUpdateChecks();
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        updateTimer.Stop();
        updateTimer.Tick -= CheckOnTimer;
        return Task.CompletedTask;
    }

    private Version? FindPreviousVersion(string? directory) => GitHubUpdater.FindPreviousVersion(directory, context.AppVersion);

    private void ChangeAutomaticChecks(object sender, RoutedEventArgs e)
    {
        settings.AutoCheckUpdates = ((CheckBox)sender).IsChecked == true;
        SaveSettings(UpdateStatus);
        ScheduleUpdateChecks();
    }

    private async void CheckOnTimer(object? sender, EventArgs e)
    {
        updateTimer.Interval = TimeSpan.FromHours(4);
        await CheckForUpdatesAsync(manual: false);
    }

    private void Save()
    {
        Directory.CreateDirectory(context.DataDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    private void SaveSettings(TextBlock status)
    {
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = Ui.F("Не удалось сохранить настройки: {0}", ex.Message);
        }
    }

    private void ScheduleUpdateChecks()
    {
        updateTimer.Stop();
        if (!settings.AutoCheckUpdates || exiting) return;
        updateTimer.Interval = TimeSpan.FromSeconds(5);
        updateTimer.Start();
    }

    private async void CheckUpdatesClicked(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdates || exiting) return;
        checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = false;
        RollbackButton.IsEnabled = false;
        var installing = false;
        var ct = updateLifetime;
        try
        {
            if (manual) UpdateStatus.Text = Ui.T("Проверяем обновления…");
            var release = await updater.CheckAsync(context.AppVersion, ct);
            if (exiting || (!manual && !settings.AutoCheckUpdates)) return;
            if (release is null)
            {
                UpdateStatus.Text = manual ? Ui.T("Установлена актуальная версия.") : "";
                return;
            }

            var version = release.Version.ToString(3);
            if (!manual && (settings.IgnoredUpdateVersion == version ||
                settings.DeferredUpdateVersion == version && settings.UpdateReminderAfter > DateTimeOffset.UtcNow))
                return;
            var installDirectory = GitHubUpdater.FindInstallation();
            var prompt = new UpdatePrompt(context, release.Version, installDirectory is not null);
            UpdateStatus.Text = Ui.F("Доступна версия APEX Chatd {0}.", version);
            prompt.ShowDialog();
            if (exiting) return;
            switch (prompt.Decision)
            {
                case UpdateDecision.Ignore:
                    settings.IgnoredUpdateVersion = version;
                    SaveSettings(UpdateStatus);
                    return;
                case UpdateDecision.Later:
                    settings.DeferredUpdateVersion = version;
                    settings.UpdateReminderAfter = DateTimeOffset.UtcNow.AddDays(1);
                    SaveSettings(UpdateStatus);
                    return;
            }
            if (installDirectory is null)
            {
                Process.Start(new ProcessStartInfo(release.Page.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            installing = true;
            var installer = await DownloadInstallerAsync(release, ct);
            ct.ThrowIfCancellationRequested();
            GitHubUpdater.LaunchInstaller(installer, installDirectory);
            await context.RequestExitAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!exiting)
        {
            UpdateStatus.Text = Ui.F(installing ? "Не удалось обновить APEX Chatd: {0}" : "Не удалось проверить обновления: {0}", ex.Message);
            if (installing)
                MessageBox.Show(UpdateStatus.Text, Ui.T("Обновление APEX Chatd"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            checkingUpdates = false;
            if (!exiting) CheckUpdatesButton.IsEnabled = RollbackButton.IsEnabled = true;
        }
    }

    private Task<string> DownloadInstallerAsync(GitHubUpdater.Release release, CancellationToken ct)
    {
        UpdateStatus.Text = Ui.F("Загружаем обновление… {0}%", 0);
        if (!context.MainWindow.IsVisible || context.MainWindow.WindowState == WindowState.Minimized)
            context.Notify(Ui.T("Обновление APEX Chatd"), UpdateStatus.Text);
        return updater.DownloadAsync(release, new Progress<int>(percent =>
        {
            if (!exiting) UpdateStatus.Text = Ui.F("Загружаем обновление… {0}%", percent);
        }), ct);
    }

    private async void RollbackClicked(object sender, RoutedEventArgs e)
    {
        if (checkingUpdates || exiting) return;
        checkingUpdates = true;
        CheckUpdatesButton.IsEnabled = RollbackButton.IsEnabled = false;
        var ct = updateLifetime;
        try
        {
            var directory = GitHubUpdater.FindInstallation();
            var previous = FindPreviousVersion(directory);
            if (directory is null || previous is null)
            {
                RollbackButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (MessageBox.Show(context.MainWindow, Ui.F("Вернуться к версии {0}? После установки приложение перезапустится.", previous.ToString(3)),
                Ui.T("Откат APEX Chatd"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var release = await updater.FindRollbackAsync(previous, ct)
                ?? throw new InvalidOperationException(Ui.T("Предыдущая версия больше недоступна на GitHub."));
            var installer = await DownloadInstallerAsync(release, ct);
            ct.ThrowIfCancellationRequested();
            var ignored = settings.IgnoredUpdateVersion;
            settings.IgnoredUpdateVersion = context.AppVersion.ToString(3);
            try
            {
                Save();
                GitHubUpdater.LaunchInstaller(installer, directory);
            }
            catch
            {
                settings.IgnoredUpdateVersion = ignored;
                SaveSettings(UpdateStatus);
                throw;
            }
            await context.RequestExitAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!exiting)
        {
            UpdateStatus.Text = Ui.F("Не удалось выполнить откат: {0}", ex.Message);
        }
        finally
        {
            checkingUpdates = false;
            if (!exiting) CheckUpdatesButton.IsEnabled = RollbackButton.IsEnabled = true;
        }
    }
}
