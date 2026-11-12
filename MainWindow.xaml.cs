using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TwitchChatAgent;

public partial class MainWindow : Window
{
    private Settings settings;
    private readonly ObsServer obs = new();
    private readonly SpeechQueue speech;
    private readonly QueueHotkeys? hotkeys;
    private bool loadingAudioDevices;
    private readonly List<ChatMessage> recent = new();
    private readonly ThemeEditor desktopTheme;
    private readonly ThemeEditor obsTheme;
    private readonly TextBlock obsBackgroundName = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button clearObsBackground = new();
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Forms.ToolStripMenuItem trayQueue;
    private ChatOverlay? overlay;
    private CancellationTokenSource? twitch;
    private bool editingOverlay;
    private bool exiting;
    private bool obsChanging;
    private bool moderating;
    private TwitchModeration? moderation;
    private bool reconnectRequested;
    private bool? streamLive;
    private TwitchModeration? checkingLive;
    private readonly DispatcherTimer overlayExpiryTimer = new();
    private readonly DispatcherTimer placementTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer updateTimer = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private readonly GitHubUpdater updater = new();
    private bool checkingUpdates;

    private bool closePromptOpen;
    private int tests;

    public MainWindow()
    {
        string? loadError = null;
        try
        {
            settings = Settings.Load();
        }
        catch (Exception ex)
        {
            settings = new();
            loadError = ex.Message;
        }

        Ui.Language = settings.Language;
        InitializeComponent();
        if (loadError is not null)
            TwitchStatus.Text = Ui.F("Не удалось загрузить настройки: {0}", loadError);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Ui.T("Открыть настройки"), null, (_, _) => ShowSettings());
        trayQueue = new Forms.ToolStripMenuItem(Ui.T("В очереди: 0")) { Enabled = false };
        menu.Items.Add(trayQueue);
        menu.Items.Add(Ui.T("Пауза / продолжить"), null, (_, _) => speech!.TogglePause());
        menu.Items.Add(Ui.T("Пропустить сообщение"), null, (_, _) => speech!.Skip());
        menu.Items.Add(Ui.T("Очистить очередь"), null, (_, _) => speech!.Clear());
        menu.Items.Add(Ui.T("Выйти"), null, async (_, _) => await ExitAsync());
        using (var icon = Application.GetResourceStream(new Uri("/APEX Chatd;component/Assets/APEXChatd.ico", UriKind.Relative)).Stream)
        using (var sourceIcon = new System.Drawing.Icon(icon))
            trayIcon = new System.Drawing.Icon(sourceIcon, 32, 32);
        tray = new Forms.NotifyIcon
        {
            Text = "APEX Chatd",
            Icon = trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowSettings();
        speech = new SpeechQueue(value =>
        {
            SpeechStatus.Text = value;
            PauseQueueButton.IsEnabled = settings.Speech && settings.SpeechVoices.Length > 0;
            PauseQueueIcon.Text = speech!.Paused ? "\uE768" : "\uE769";
            PauseQueueLabel.Text = speech.Paused ? Ui.T("Продолжить") : Ui.T("Пауза");
            trayQueue.Text = Ui.F("В очереди: {0}", speech!.Count) + (speech.Paused ? Ui.T(" · Пауза") : "");
        });

        var themes = ((App)Application.Current).Themes;
        themes.Apply(settings.Theme);
        SourceInitialized += (_, _) => themes.ApplyTitleBar(this);
        AppThemeChoice.SelectedIndex = (int)themes.Theme;
        LanguageChoice.SelectedIndex = Ui.Language == "en" ? 1 : 0;
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        VersionLabel.Text = Ui.F("Версия {0}", version);
        AutoCheckUpdates.IsChecked = settings.AutoCheckUpdates;
        try
        {
            RollbackButton.Visibility = GitHubUpdater.FindPreviousVersion(GitHubUpdater.FindInstallation()) is null
                ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try
        {
            StartWithWindows.IsChecked = StartupRegistration.IsEnabled();
        }
        catch (Exception ex)
        {
            StartWithWindows.IsEnabled = false;
            StartupStatus.Text = Ui.F("Не удалось прочитать настройку автозапуска: {0}", ex.Message);
        }

        OverlayTheme.Content = desktopTheme = new ThemeEditor(settings.DesktopTheme);
        ObsTheme.Content = obsTheme = new ThemeEditor(settings.ObsTheme, CreateObsBackgroundControls());
        settings.ObsTheme = settings.ObsTheme with { MessageMotion = obsTheme.MessageMotion };
        var effectsEditor = new ObsEffectsEditor(settings.ObsEffects);
        settings.ObsEffects = effectsEditor.Value;
        ObsEffects.Content = effectsEditor;
        effectsEditor.Changed += effects =>
        {
            settings.ObsEffects = effects;
            RenderChat();
            SaveSettings(ObsStatus);
        };
        StartMinimized.IsChecked = settings.StartMinimized;
        MinimizeToTray.IsChecked = settings.MinimizeToTray;
        OverlayEnabled.IsChecked = settings.Overlay;
        OverlayLiveOnly.IsChecked = settings.OverlayLiveOnly;
        ObsEnabled.IsChecked = settings.Obs;
        SpeechEnabled.IsChecked = settings.Speech;
        ReadName.IsChecked = settings.ReadName;
        ReadNamePhrase.Text = settings.ReadNamePhrase;
        FilterSpeechByPrefix.IsChecked = settings.SpeechFilter.Enabled;
        SpeechPrefix.Text = settings.SpeechFilter.Prefix;
        InvertSpeechPrefix.IsChecked = settings.SpeechFilter.Inverted;
        SkipSpeechEmotes.IsChecked = settings.SkipSpeechEmotes;
        RetrySpeech.IsChecked = settings.RetrySpeech;
        RandomSpeechVoice.IsChecked = settings.RandomSpeechVoice;
        PinSpeechVoice.IsChecked = settings.PinSpeechVoice;
        Volume.Value = settings.Volume;
        LoadAudioDevices();
        RefreshVoiceProfiles();
        UpdateVoiceSelectionOptions();
        ObsLink.Text = NewObsLink();
        UpdateObsBackgroundLabel();
        speech.Configure(settings);

        desktopTheme.Changed += theme =>
        {
            if (settings.DesktopTheme == theme)
                return;
            settings.DesktopTheme = theme;
            ApplyOverlay();
            SaveSettings(OverlayStatus);
        };
        obsTheme.Changed += theme =>
        {
            if (settings.ObsTheme == theme)
                return;
            settings.ObsTheme = theme;
            RenderChat();
            SaveSettings(ObsStatus);
        };
        OnToggle(OverlayEnabled, UpdateOverlayOptions);
        OnToggle(OverlayLiveOnly, UpdateOverlayOptions);
        OnToggle(ObsEnabled, async () => await UpdateObsAsync());
        OnToggle(SpeechEnabled, UpdateSpeechSettings);
        OnToggle(ReadName, UpdateSpeechSettings);
        ReadNamePhrase.TextChanged += (_, _) => UpdateSpeechSettings();
        OnToggle(FilterSpeechByPrefix, UpdateSpeechSettings);
        OnToggle(InvertSpeechPrefix, UpdateSpeechSettings);
        SpeechPrefix.TextChanged += (_, _) => UpdateSpeechSettings();
        OnToggle(SkipSpeechEmotes, UpdateSpeechSettings);
        OnToggle(RetrySpeech, UpdateSpeechSettings);
        OnToggle(RandomSpeechVoice, () =>
        {
            UpdateVoiceSelectionOptions();
            UpdateSpeechSettings();
        });
        OnToggle(PinSpeechVoice, () =>
        {
            UpdateVoiceSelectionOptions();
            UpdateSpeechSettings();
        });
        AudioDeviceChoice.SelectionChanged += (_, _) =>
        {
            if (!loadingAudioDevices)
                UpdateSpeechSettings();
        };
        Volume.ValueChanged += (_, _) => UpdateSpeechSettings();
        OnToggle(StartWithWindows, UpdateStartupRegistration);
        OnToggle(StartMinimized, () =>
        {
            UpdateAppSettings(StartupStatus);
            if (StartWithWindows.IsChecked == true)
                UpdateStartupRegistration();
        });
        OnToggle(MinimizeToTray, () => UpdateAppSettings(StartupStatus));
        OnToggle(AutoCheckUpdates, () =>
        {
            settings.AutoCheckUpdates = AutoCheckUpdates.IsChecked == true;
            SaveSettings(UpdateStatus);
            ScheduleUpdateChecks();
        });
        StateChanged += (_, _) => HideMinimizedWindow();
        AppThemeChoice.SelectionChanged += (_, _) => UpdateAppSettings(ThemeStatus);
        LanguageChoice.SelectionChanged += async (_, _) => await UpdateLanguageAsync();
        overlayExpiryTimer.Tick += (_, _) => RenderOverlay();
        placementTimer.Tick += (_, _) => SaveOverlayPosition();
        liveTimer.Tick += async (_, _) => await RefreshLiveAsync();
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(4);
            await CheckForUpdatesAsync(manual: false);
        };
        UpdateOverlayStatus();
        RefreshModeration();
        UpdateShortcutFields();
        try
        {
            hotkeys = new QueueHotkeys();
            hotkeys.Triggered += OnQueueAction;
            HotkeyStatus.Text = string.Join(Environment.NewLine, hotkeys.Configure(settings.SpeechHotkeys));
        }
        catch (Exception ex)
        {
            HotkeyStatus.Text = ex.Message;
        }
    }

    private static void OnToggle(CheckBox box, Action changed)
    {
        box.Checked += (_, _) => changed();
        box.Unchecked += (_, _) => changed();
    }

    private void SaveSettings(TextBlock status)
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            status.Text = Ui.F("Не удалось сохранить настройки: {0}", ex.Message);
        }
    }

    internal async Task StartAsync()
    {
        ScheduleUpdateChecks();
        if (HasMissingSpeechVoice)
            tray.ShowBalloonTip(5000, Ui.XamlText("Speech"), Ui.XamlText("MissingSpeechVoices"), Forms.ToolTipIcon.Warning);
        ApplyOverlay();
        if (settings.Obs)
            await UpdateObsAsync();
        try
        {
            var saved = Settings.LoadTokens(TwitchClient.DefaultClientId);
            if (saved is not null && twitch is null && !exiting)
                await RunTwitchAsync(saved);
        }
        catch (Exception ex)
        {
            TwitchStatus.Text = Ui.F("Не удалось восстановить подключение: {0}", ex.Message);
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
        var ct = updateLifetime.Token;
        try
        {
            if (manual) UpdateStatus.Text = Ui.T("Проверяем обновления…");
            var release = await updater.CheckAsync(GitHubUpdater.CurrentVersion, ct);
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
            var prompt = new UpdatePrompt(this, release.Version, installDirectory is not null);
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
            await ExitAsync();
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
        if (!IsVisible || WindowState == WindowState.Minimized)
            tray.ShowBalloonTip(5000, Ui.T("Обновление APEX Chatd"), UpdateStatus.Text, Forms.ToolTipIcon.Info);
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
        var ct = updateLifetime.Token;
        try
        {
            var directory = GitHubUpdater.FindInstallation();
            var previous = GitHubUpdater.FindPreviousVersion(directory);
            if (directory is null || previous is null)
            {
                RollbackButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (MessageBox.Show(this, Ui.F("Вернуться к версии {0}? После установки приложение перезапустится.", previous.ToString(3)),
                Ui.T("Откат APEX Chatd"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var release = await updater.FindRollbackAsync(previous, ct)
                ?? throw new InvalidOperationException(Ui.T("Предыдущая версия больше недоступна на GitHub."));
            var installer = await DownloadInstallerAsync(release, ct);
            ct.ThrowIfCancellationRequested();
            var ignored = settings.IgnoredUpdateVersion;
            settings.IgnoredUpdateVersion = GitHubUpdater.CurrentVersion.ToString(3);
            try
            {
                settings.Save();
                GitHubUpdater.LaunchInstaller(installer, directory);
            }
            catch
            {
                settings.IgnoredUpdateVersion = ignored;
                SaveSettings(UpdateStatus);
                throw;
            }
            await ExitAsync();
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

    private async void ConnectClicked(object sender, RoutedEventArgs e) => await RunTwitchAsync(null);

    private void ReconnectClicked(object sender, RoutedEventArgs e)
    {
        if (twitch is null)
            return;
        reconnectRequested = true;
        ReconnectButton.Visibility = Visibility.Collapsed;
        twitch.Cancel();
    }

    private async Task RunTwitchAsync(TwitchTokens? saved)
    {
        if (twitch is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        twitch = cancellation;
        ConnectButton.IsEnabled = false;
        const string clientId = TwitchClient.DefaultClientId;
        try
        {
            TwitchStatus.Text = Ui.T("Подключение…");
            var tokens = saved ?? await TwitchClient.SignInAsync(clientId, SetTwitchStatus, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (saved is null)
                Settings.SaveTokens(clientId, tokens);
            await TwitchClient.MonitorAsync(clientId, tokens, updated =>
            {
                try { Settings.SaveTokens(clientId, updated); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                {
                    NotifyTwitch(Ui.T("Не удалось сохранить обновлённый вход Twitch. После перезапуска может потребоваться повторный вход."));
                }
            }, SetTwitchStatus, Publish, RemoveChatMessages, SetModeration, cancellation.Token);
        }
        catch (TwitchSignInRequiredException ex)
        {
            TwitchStatus.Text = ex.Message;
            NotifyTwitch(ex.Message);
        }
        catch (OperationCanceledException)
        {
            TwitchStatus.Text = cancellation.IsCancellationRequested ? Ui.T("Twitch отключён") : Ui.T("Время входа истекло.");
        }
        catch (Exception ex)
        {
            TwitchStatus.Text = ex.Message;
        }
        finally
        {
            twitch = null;
            SetModeration(null);
            ConnectButton.IsEnabled = true;
        }

        if (reconnectRequested && !exiting)
        {
            reconnectRequested = false;
            await RunTwitchAsync(null);
        }
    }

    private void SetTwitchStatus(string value) => TwitchStatus.Text = value;

    private void NotifyTwitch(string message)
    {
        if (!exiting)
            tray.ShowBalloonTip(5000, "Twitch", message, Forms.ToolTipIcon.Warning);
    }

    private void DisconnectClicked(object sender, RoutedEventArgs e)
    {
        reconnectRequested = false;
        twitch?.Cancel();
        SetModeration(null);
        try
        {
            Settings.ClearTokens();
            TwitchStatus.Text = Ui.T("Twitch отключён");
        }
        catch (Exception ex)
        {
            TwitchStatus.Text = ex.Message;
        }
    }

    private void Publish(ChatMessage message)
    {
        if (exiting)
            return;
        recent.Add(message);
        if (recent.Count > 50)
            recent.RemoveAt(0);
        Recent.Items.Add(ToChatLine(message));
        if (Recent.Items.Count > 50)
            Recent.Items.RemoveAt(0);
        if (!Recent.IsMouseOver)
            Recent.ScrollIntoView(Recent.Items[Recent.Items.Count - 1]);
        RenderChat();
        speech.Add(message);
    }

    private void RenderChat()
    {
        obs.Update(settings.ObsTheme, recent.ToArray(), settings.ObsBackgroundPath, settings.ObsEffects);
        RenderOverlay();
    }

    private void RenderOverlay()
    {
        overlayExpiryTimer.Stop();
        if (overlay is null || exiting)
            return;

        var theme = settings.DesktopTheme;
        var now = DateTimeOffset.UtcNow;
        var visible = editingOverlay ? [ChatAppearance.Sample] : ChatAppearance.VisibleMessages(theme, recent, now);
        overlay.Render(theme, visible);
        if (!editingOverlay && theme.HideExpiredMessages && visible.Length > 0)
        {
            var remaining = visible.Min(message => message.ReceivedAt) + ChatAppearance.MessageLifetime(theme) - now;
            overlayExpiryTimer.Interval = remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1);
            overlayExpiryTimer.Start();
        }
    }

    private sealed record ChatLine(ChatMessage Message, string? DeleteReason)
    {
        public bool CanDelete => DeleteReason is null;
    }

    private ChatLine ToChatLine(ChatMessage message)
    {
        if (message.ChannelId is null)
            return new(message, Ui.T("Это тестовое сообщение. Оно существует только в приложении."));
        return new(message, moderation is { } session ? session.BlockedReason(message) : Ui.T("Подключи Twitch для модерации."));
    }
    private void RefreshModeration()
    {
        ModerationStatus.Text = moderation is null ? Ui.T("Подключи Twitch для модерации сообщений.") :
            !moderation.CanDelete ? Ui.T("Для модерации нажми «Обновить права Twitch».") :
            Ui.T("Наведи курсор на серую кнопку, чтобы увидеть причину.");
        RefreshChatList();
    }

    private void RefreshChatList()
    {
        Recent.Items.Clear();
        foreach (var message in recent)
            Recent.Items.Add(ToChatLine(message));
        if (Recent.Items.Count > 0)
            Recent.ScrollIntoView(Recent.Items[Recent.Items.Count - 1]);
    }
    private void SetModeration(TwitchModeration? session)
    {
        if (moderation?.ChannelId != session?.ChannelId)
            streamLive = null;
        moderation = session;
        ReconnectButton.Visibility = session is not null && !session.CanDelete
            ? Visibility.Visible : Visibility.Collapsed;
        if (exiting)
            return;
        RefreshModeration();
        UpdateLiveMonitoring();
        ApplyOverlay();
    }

    private void UpdateLiveMonitoring()
    {
        if (!settings.OverlayLiveOnly || moderation is null)
        {
            liveTimer.Stop();
            streamLive = null;
        }
        else
        {
            liveTimer.Start();
            _ = RefreshLiveAsync();
        }

        UpdateOverlayStatus();
    }

    private async Task RefreshLiveAsync()
    {
        var session = moderation;
        var token = twitch?.Token;
        if (exiting || !settings.OverlayLiveOnly || session is null || token is null ||
            ReferenceEquals(checkingLive, session))
            return;

        checkingLive = session;
        try
        {
            var live = await TwitchClient.IsLiveAsync(session, token.Value);
            if (!ReferenceEquals(moderation, session) || !settings.OverlayLiveOnly || exiting)
                return;
            streamLive = live;
            UpdateOverlayStatus();
            ApplyOverlay();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            if (ReferenceEquals(moderation, session) && settings.OverlayLiveOnly && !exiting)
            {
                streamLive = null;
                ApplyOverlay();
                LiveStatus.Text = Ui.T("Не удалось проверить трансляцию. Повторим автоматически.");
            }
        }
        finally
        {
            if (ReferenceEquals(checkingLive, session))
                checkingLive = null;
        }
    }

    private void UpdateOverlayStatus()
    {
        LiveStatus.Text = !settings.OverlayLiveOnly ? "" :
            moderation is null ? Ui.T("Подключи Twitch для проверки трансляции.") :
            streamLive is true ? Ui.T("Трансляция идёт.") :
            streamLive is false ? Ui.T("Трансляция не идёт.") : Ui.T("Проверяем трансляцию…");
    }
    private async void DeleteMessageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatLine row })
            await DeleteMessageAsync(row.Message);
    }

    private async Task DeleteMessageAsync(ChatMessage message)
    {
        var session = moderation;
        var cancellation = twitch;
        if (session is null || cancellation is null || moderating || !session.Allows(message))
            return;

        moderating = true;
        try
        {
            if (moderation != session || cancellation.IsCancellationRequested)
                return;

            await TwitchClient.DeleteMessageAsync(session, message, cancellation.Token);
            RemoveChatMessages(candidate => candidate.ChannelId == session.ChannelId && candidate.Id == message.Id);
            ModerationStatus.Text = Ui.T("Сообщение удалено.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ModerationStatus.Text = ex.Message;
        }
        finally
        {
            moderating = false;
        }
    }

    private void RemoveChatMessages(Func<ChatMessage, bool> matches)
    {
        speech.Remove(matches);
        if (recent.RemoveAll(matches.Invoke) == 0)
            return;
        RefreshChatList();
        RenderChat();
    }

    private void ApplyOverlay()
    {
        if (editingOverlay || (settings.Overlay && (!settings.OverlayLiveOnly || streamLive == true)))
        {
            if (overlay is null)
            {
                overlay = new ChatOverlay(settings);
                overlay.LocationChanged += (_, _) => ScheduleOverlayPositionSave();
                overlay.SizeChanged += (_, _) => ScheduleOverlayPositionSave();
                overlay.PinClicked += ToggleOverlayPlacement;
                overlay.SetInteraction(editingOverlay);
                overlay.Show();
            }

        }
        else
        {
            overlay?.Close();
            overlay = null;
        }
        overlay?.SetInteraction(editingOverlay);
        RenderChat();
    }

    private void UpdateOverlayOptions()
    {
        settings.Overlay = OverlayEnabled.IsChecked == true;
        settings.OverlayLiveOnly = OverlayLiveOnly.IsChecked == true;
        UpdateLiveMonitoring();
        ApplyOverlay();
        SaveSettings(OverlayStatus);
    }
    private void MoveOverlayClicked(object sender, RoutedEventArgs e) => ToggleOverlayPlacement();

    private void ToggleOverlayPlacement()
    {
        try
        {
            if (editingOverlay)
                SaveOverlayPosition();
            editingOverlay = !editingOverlay;
            MoveOverlay.Content = editingOverlay ? Ui.T("Завершить настройку") : Ui.T("Настроить положение");
            ApplyOverlay();
        }
        catch (Exception ex)
        {
            OverlayStatus.Text = ex.Message;
        }
    }

    private void ScheduleOverlayPositionSave()
    {
        if (!editingOverlay)
            return;
        placementTimer.Stop();
        placementTimer.Start();
    }

    private void SaveOverlayPosition()
    {
        placementTimer.Stop();
        if (!editingOverlay || overlay is null)
            return;
        settings.Left = overlay.Left;
        settings.Top = overlay.Top;
        settings.Width = overlay.ActualWidth;
        settings.Height = overlay.ActualHeight;
        SaveSettings(OverlayStatus);
    }

    private async Task UpdateObsAsync(bool openBrowser = false)
    {
        if (obsChanging || exiting)
            return;
        obsChanging = true;
        ObsEnabled.IsEnabled = PreviewObs.IsEnabled = false;
        try
        {
            var enabled = openBrowser || ObsEnabled.IsChecked == true;
            if (enabled)
                await obs.StartAsync();
            else
                await obs.StopAsync();
            obs.SetLiveEnabled(enabled);
            settings.Obs = enabled;
            ObsEnabled.IsChecked = enabled;
            RenderChat();
            ObsStatus.Text = enabled ? Ui.T("Страница для OBS включена. Добавь ссылку в источник «Браузер».") : Ui.T("Страница для OBS выключена.");
            SaveSettings(ObsStatus);
            if (openBrowser && !exiting)
                Process.Start(new ProcessStartInfo(ObsLink.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ObsEnabled.IsChecked = settings.Obs;
            ObsStatus.Text = ex.Message;
        }
        finally
        {
            ObsEnabled.IsEnabled = PreviewObs.IsEnabled = true;
            obsChanging = false;
        }
    }

    private async void PreviewObsClicked(object sender, RoutedEventArgs e) => await UpdateObsAsync(openBrowser: true);

    private StackPanel CreateObsBackgroundControls()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
        var heading = new TextBlock { Text = Ui.XamlText("ObsMediaBackground"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "FluentSecondary");
        panel.Children.Add(heading);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        var choose = new Button { Content = Ui.XamlText("ChooseObsBackground") };
        choose.Click += ChooseObsBackgroundClicked;
        clearObsBackground.Content = Ui.XamlText("RemoveObsBackground");
        clearObsBackground.Click += ClearObsBackgroundClicked;
        actions.Children.Add(choose);
        actions.Children.Add(clearObsBackground);
        panel.Children.Add(actions);
        panel.Children.Add(obsBackgroundName);
        return panel;
    }

    private void ChooseObsBackgroundClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Filter = ObsMedia.FileFilter };
        if (picker.ShowDialog(this) != true || ObsMedia.ContentType(picker.FileName) is null)
            return;
        settings.ObsBackgroundPath = picker.FileName;
        UpdateObsBackgroundLabel();
        RenderChat();
        SaveSettings(ObsStatus);
    }

    private void ClearObsBackgroundClicked(object sender, RoutedEventArgs e)
    {
        settings.ObsBackgroundPath = null;
        UpdateObsBackgroundLabel();
        RenderChat();
        SaveSettings(ObsStatus);
    }

    private void UpdateObsBackgroundLabel()
    {
        var path = settings.ObsBackgroundPath;
        obsBackgroundName.Text = path is null ? Ui.T("Фон не выбран") : Path.GetFileName(path);
        obsBackgroundName.ToolTip = path;
        clearObsBackground.IsEnabled = path is not null;
    }
    private static string NewObsLink() => $"{ObsServer.Url}?{Guid.NewGuid():N}";

    private void CopyObsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var link = NewObsLink();
            Clipboard.SetText(link);
            ObsLink.Text = link;
            ObsStatus.Text = Ui.T("Ссылка скопирована.");
        }
        catch (Exception ex)
        {
            ObsStatus.Text = ex.Message;
        }
    }

    private void UpdateSpeechSettings()
    {
        settings.Speech = SpeechEnabled.IsChecked == true;
        settings.ReadName = ReadName.IsChecked == true;
        settings.ReadNamePhrase = ReadNamePhrase.Text.Trim();
        settings.SpeechFilter = new(FilterSpeechByPrefix.IsChecked == true, SpeechPrefix.Text.Trim(),
            InvertSpeechPrefix.IsChecked == true);
        settings.SkipSpeechEmotes = SkipSpeechEmotes.IsChecked == true;
        settings.RetrySpeech = RetrySpeech.IsChecked == true;
        settings.RandomSpeechVoice = RandomSpeechVoice.IsChecked == true;
        settings.PinSpeechVoice = PinSpeechVoice.IsChecked == true;
        settings.AudioOutputId = AudioDeviceChoice.SelectedValue as string ?? settings.AudioOutputId;
        settings.Volume = (int)Volume.Value;
        UpdateMissingSpeechVoiceWarning();
        speech.Configure(settings);
        SaveSettings(SpeechSettingsStatus);
    }

    private void RefreshAudioDevicesClicked(object sender, RoutedEventArgs e) => LoadAudioDevices();

    private void LoadAudioDevices()
    {
        loadingAudioDevices = true;
        try
        {
            IReadOnlyList<AudioOutputChoice> devices;
            try
            {
                devices = AudioOutputDevices.List();
                AudioDeviceStatus.Text = "";
            }
            catch (Exception ex)
            {
                devices = [AudioOutputDevices.Default];
                AudioDeviceStatus.Text = Ui.F("Не удалось получить список устройств: {0}", ex.Message);
            }

            if (!devices.Any(device => device.Id == settings.AudioOutputId))
                devices = devices.Append(new(settings.AudioOutputId, Ui.T("Выбранное устройство недоступно"))).ToArray();
            AudioDeviceChoice.ItemsSource = devices;
            AudioDeviceChoice.SelectedValue = settings.AudioOutputId;
        }
        finally
        {
            loadingAudioDevices = false;
        }
    }

    private void OnQueueAction(QueueAction action)
    {
        switch (action)
        {
            case QueueAction.Pause: speech.TogglePause(); break;
            case QueueAction.Skip: speech.Skip(); break;
            case QueueAction.Clear: speech.Clear(); break;
            case QueueAction.ClearDisplayed: ClearDisplayedMessages(); break;
        }
    }

    private void UpdateShortcutFields()
    {
        var unset = Ui.T("Не назначено");
        PauseShortcut.Text = settings.SpeechHotkeys.Pause?.ToString() ?? unset;
        SkipShortcut.Text = settings.SpeechHotkeys.Skip?.ToString() ?? unset;
        ClearShortcut.Text = settings.SpeechHotkeys.Clear?.ToString() ?? unset;
        ClearDisplayedShortcut.Text = settings.SpeechHotkeys.ClearDisplayed?.ToString() ?? unset;
    }

    private void ShortcutFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!exiting)
            hotkeys?.Suspend(true);
    }

    private void ShortcutUnfocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!exiting && hotkeys is not null &&
                Keyboard.FocusedElement != PauseShortcut && Keyboard.FocusedElement != SkipShortcut &&
                Keyboard.FocusedElement != ClearShortcut && Keyboard.FocusedElement != ClearDisplayedShortcut)
            {
                var errors = hotkeys.Suspend(false);
                if (errors.Length != 0)
                    HotkeyStatus.Text = string.Join(Environment.NewLine, errors);
            }
        }));
    }

    private void ShortcutKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
            return;

        e.Handled = true;
        if (e.IsRepeat || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (key == Key.Escape)
        {
            HotkeyStatus.Focus();
            return;
        }

        var modifiers = ShortcutModifiers();
        var shortcut = modifiers == 0 && key is Key.Delete or Key.Back
            ? null : new QueueShortcut(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        StoreShortcut((TextBox)sender, shortcut);
    }

    private void ShortcutMouseDown(object sender, MouseButtonEventArgs e)
    {
        var field = (TextBox)sender;
        var key = e.ChangedButton switch { MouseButton.Middle => 4u, MouseButton.XButton1 => 5u, MouseButton.XButton2 => 6u, _ => 0u };
        if (key == 0 || !field.IsKeyboardFocusWithin)
            return;
        e.Handled = true;
        StoreShortcut(field, new QueueShortcut(ShortcutModifiers(), key));
    }

    private static uint ShortcutModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        return ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) |
            ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) |
            ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0) |
            ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
    }

    private void StoreShortcut(TextBox field, QueueShortcut? shortcut)
    {
        if (hotkeys is null || !Enum.TryParse<QueueAction>(field.Tag as string, out var action))
            return;
        var previous = settings.SpeechHotkeys;
        try
        {
            if (shortcut is not null)
                hotkeys.Validate(action, shortcut);
            settings.SpeechHotkeys = previous.Set(action, shortcut);
            settings.Save();
            hotkeys.Configure(settings.SpeechHotkeys);
            UpdateShortcutFields();
            HotkeyStatus.Text = "";
            HotkeyStatus.Focus();
        }
        catch (Exception ex)
        {
            settings.SpeechHotkeys = previous;
            HotkeyStatus.Text = ex.Message;
        }
    }

    private void UpdateAppSettings(TextBlock status)
    {
        var theme = (AppTheme)AppThemeChoice.SelectedIndex;
        if (settings.Theme != theme)
            ((App)Application.Current).Themes.Apply(theme);
        settings.Theme = theme;
        settings.StartMinimized = StartMinimized.IsChecked == true;
        settings.MinimizeToTray = MinimizeToTray.IsChecked == true;
        HideMinimizedWindow();
        status.Text = "";
        SaveSettings(status);
    }

    private async Task UpdateLanguageAsync()
    {
        if (exiting || LanguageChoice.SelectedIndex < 0)
            return;
        var selected = LanguageChoice.SelectedIndex == 1 ? "en" : "ru";
        if (selected == settings.Language)
            return;

        var previous = settings.Language;
        settings.Language = selected;
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            settings.Language = previous;
            LanguageChoice.SelectedIndex = previous == "en" ? 1 : 0;
            LanguageStatus.Text = Ui.F("Не удалось сохранить настройки: {0}", ex.Message);
            return;
        }

        ((App)Application.Current).RestartRequested = true;
        await ExitAsync();
    }
    private async void ResetSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Ui.T("Сбросить все настройки и перезапустить приложение? Подключение Twitch сохранится."),
            Ui.T("Сброс настроек"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        try
        {
            StartupRegistration.SetEnabled(false);
            new Settings().Save();
            editingOverlay = false;
            ((App)Application.Current).RestartRequested = true;
            await ExitAsync();
        }
        catch (Exception ex)
        {
            AppStatus.Text = Ui.F("Не удалось сбросить настройки: {0}", ex.Message);
        }
    }

    private void UpdateStartupRegistration()
    {
        try
        {
            StartupRegistration.SetEnabled(StartWithWindows.IsChecked == true);
            StartupStatus.Text = "";
        }
        catch (Exception ex)
        {
            StartupStatus.Text = Ui.F("Не удалось изменить автозапуск: {0}", ex.Message);
        }
    }
    private void AuthorGitHubRequested(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GitHub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TestClicked(object sender, RoutedEventArgs e) =>
        Publish(new ChatMessage(Ui.T("Пример"), Ui.F("Тестовое сообщение номер {0}.", ++tests)));
    private void PauseClicked(object sender, RoutedEventArgs e) => speech.TogglePause();
    private void SkipClicked(object sender, RoutedEventArgs e) => speech.Skip();
    private void ClearClicked(object sender, RoutedEventArgs e) => speech.Clear();
    private void ResetPinnedVoicesClicked(object sender, RoutedEventArgs e) => speech.ResetPinnedVoices();
    private void ClearDisplayedClicked(object sender, RoutedEventArgs e) => ClearDisplayedMessages();

    private void ClearDisplayedMessages()
    {
        recent.Clear();
        Recent.Items.Clear();
        RenderChat();
    }

    internal void ShowAtStartup(bool autostart)
    {
        if ((autostart || settings.StartMinimized) && settings.MinimizeToTray)
            return;
        if (autostart || settings.StartMinimized)
            WindowState = WindowState.Minimized;
        Show();
    }

    private void HideMinimizedWindow()
    {
        if (settings.MinimizeToTray && WindowState == WindowState.Minimized)
            Hide();
    }

    private void ShowSettings()
    {
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (exiting)
            return;
        e.Cancel = true;
        if (!closePromptOpen)
        {
            closePromptOpen = true;
            Dispatcher.BeginInvoke(new Action(async () => await ConfirmCloseAsync()));
        }
    }

    private async Task ConfirmCloseAsync()
    {
        try
        {
            if (!settings.SkipExitConfirmation)
            {
                var prompt = new ClosePrompt(this);
                if (prompt.ShowDialog() != true)
                    return;

                if (prompt.Remember)
                {
                    settings.SkipExitConfirmation = true;
                    SaveSettings(AppStatus);
                }
            }

            await ExitAsync();
        }
        finally
        {
            closePromptOpen = false;
        }
    }
    private async Task ExitAsync()
    {
        if (exiting)
            return;
        exiting = true;
        hotkeys?.Dispose();
        overlayExpiryTimer.Stop();
        voiceCatalogLifetime.Cancel();
        liveTimer.Stop();
        updateTimer.Stop();
        updateLifetime.Cancel();
        SaveOverlayPosition();
        twitch?.Cancel();
        speech.Configure(settings with { Speech = false });
        overlay?.Close();
        tray.Dispose();
        trayIcon.Dispose();
        while (obsChanging)
            await Task.Delay(25);
        try
        {
            await obs.StopAsync();
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
