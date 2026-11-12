using Forms = System.Windows.Forms;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using Microsoft.Win32;

namespace TwitchChatAgent;

public partial class MainWindow
{
    private PluginManager? plugins;
    private readonly CancellationTokenSource pluginChecks = new();
    private static readonly HttpClient PluginUpdatesHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    internal void ShowPlugins() => PluginsTab.IsSelected = true;

    private async Task StartPluginsAsync()
    {
        try
        {
            plugins = new PluginManager(this, ExitAsync,
                (title, message) => tray.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info));
            if (plugins.Entries.Any(entry => entry.Package?.Id == "apexchatd.github-updater" && plugins.Enabled(entry)))
                ImportLegacyUpdaterSettings();
            await plugins.StartAsync(AddPluginTab);
            if (!exiting)
            {
                speech.Configure(settings);
                RefreshVoiceProfiles();
                RefreshPluginShortcuts();
                AddRandomEdgeVoiceButton.Visibility = EdgeProvider is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ShowPluginStatus(ex.Message);
            Trace.TraceError(ex.ToString());
        }
        RefreshPlugins();
    }

    private void ImportLegacyUpdaterSettings()
    {
        var directory = PluginManager.DataDirectory("apexchatd.github-updater");
        var path = Path.Combine(directory, "settings.json");
        if (File.Exists(path)) return;
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(new
            {
                settings.AutoCheckUpdates, settings.IgnoredUpdateVersion, settings.DeferredUpdateVersion, settings.UpdateReminderAfter
            }));
            File.Move(path + ".tmp", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError(ex.ToString());
        }
    }

    private void AddPluginTab(string title, Func<FrameworkElement> create, string? icon)
    {
        var tab = new TabItem { Header = title, Tag = icon?.Length == 1 ? icon : "\uE74C" };
        MainTabs.SelectionChanged += LoadContent;
        MainTabs.Items.Add(tab);

        void LoadContent(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, MainTabs) || MainTabs.SelectedItem != tab) return;
            MainTabs.SelectionChanged -= LoadContent;
            try { tab.Content = create(); }
            catch (Exception ex)
            {
                tab.Content = new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
                Trace.TraceError(ex.ToString());
            }
        }
    }

    private void RefreshPlugins()
    {
        PluginList.Children.Clear();
        AddPluginButton.IsEnabled = plugins is not null;
        if (plugins is null) return;

        foreach (var entry in plugins.Entries)
        {
            var manifest = entry.Package;
            var enabled = plugins.Enabled(entry);
            var row = new StackPanel();
            var card = new Border { Child = row, Padding = new Thickness(16) };
            card.SetResourceReference(StyleProperty, "SettingsSection");
            card.SetResourceReference(Border.BackgroundProperty, "FluentBackdrop");
            PluginList.Children.Add(card);

            var heading = new DockPanel();
            row.Children.Add(heading);
            var state = entry.Problem is not null ? P("Требует внимания", "Needs attention") :
                entry.Running != enabled ? P("После перезапуска", "Pending restart") :
                enabled ? P("Включён", "Enabled") : P("Выключен", "Disabled");
            var stateLabel = new TextBlock { Text = state, FontSize = 12, FontWeight = FontWeights.SemiBold };
            stateLabel.SetResourceReference(TextBlock.ForegroundProperty, entry.Running && enabled ? "FluentAccent" : "FluentSecondary");
            var badge = new Border
            {
                Child = stateLabel, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top
            };
            badge.SetResourceReference(Border.BackgroundProperty, "FluentSurface");
            DockPanel.SetDock(badge, Dock.Right);
            heading.Children.Add(badge);
            heading.Children.Add(new TextBlock
            {
                Text = manifest?.Name ?? Path.GetFileName(entry.Path), FontSize = 17,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
            });
            var trust = entry.Signature switch
            {
                PluginManager.Trust.Reviewed => P("✓ Проверен", "✓ Verified"),
                PluginManager.Trust.Invalid => P("Подпись недействительна", "Invalid signature"),
                _ => P("Не проверен", "Unreviewed")
            };
            var details = new TextBlock
            {
                Text = manifest is null ? trust : $"{manifest.Version} · {trust}",
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
            };
            details.SetResourceReference(TextBlock.ForegroundProperty, "FluentSecondary");
            row.Children.Add(details);
            if (entry.Problem is not null)
                row.Children.Add(new TextBlock
                {
                    Text = entry.Problem, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap
                });

            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, -4) };
            row.Children.Add(actions);
            var toggle = new Button { IsEnabled = enabled || entry.Compatible, Margin = new Thickness(0, 0, 8, 4) };
            SetPluginAction(toggle, enabled ? "\uE769" : "\uE768", enabled ? P("Выключить", "Disable") : P("Включить", "Enable"));
            toggle.Click += (_, _) =>
            {
                if (!enabled && entry.Signature == PluginManager.Trust.Unreviewed &&
                    MessageBox.Show(this, P("Этот выпуск не проверен APEX Chatd. Включить плагин?",
                        "APEX Chatd has not reviewed this release. Enable the plugin?"), "APEX Chatd",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                ChangePlugins(() => plugins.SetEnabled(entry, !enabled));
            };
            actions.Children.Add(toggle);
            var remove = new Button { Margin = new Thickness(0, 0, 8, 4) };
            SetPluginAction(remove, "\uE74D", P("Удалить", "Remove"));
            remove.Click += (_, _) => ChangePlugins(() => plugins.Remove(entry));
            actions.Children.Add(remove);
            if (entry.ValidPayload && PluginManager.TryHttps(manifest?.UpdateUrl, out var url))
            {
                var check = new Button { Margin = new Thickness(0, 0, 8, 4) };
                SetPluginAction(check, "\uE72C", P("Проверить обновление", "Check for update"));
                var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 8, 0, 0) };
                var release = new Button
                {
                    Content = P("Открыть страницу выпуска", "Open release page"),
                    Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0)
                };
                check.Click += async (_, _) => await CheckPluginUpdateAsync(check, result, release, manifest!, url!);
                actions.Children.Add(check);
                row.Children.Add(result);
                row.Children.Add(release);
            }
        }
        if (PluginList.Children.Count == 0)
        {
            var message = new TextBlock
            {
                Text = P("Плагины не установлены. Добавьте ZIP с плагином.", "No plugins installed. Add a plugin ZIP."),
                TextWrapping = TextWrapping.Wrap
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "FluentSecondary");
            var empty = new Border { Child = message };
            empty.SetResourceReference(StyleProperty, "SettingsSection");
            PluginList.Children.Add(empty);
        }
    }

    private void AddPluginClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = P("Пакет плагина (*.zip)|*.zip", "Plugin package (*.zip)|*.zip") };
        if (dialog.ShowDialog(this) == true)
            ChangePlugins(() => plugins!.Import(dialog.FileName));
    }

    private void ChangePlugins(Action action)
    {
        try
        {
            action();
            RefreshPlugins();
            ShowPluginStatus(P("Перезапустите приложение, чтобы применить изменения.", "Restart the app to apply your changes."));
        }
        catch (Exception ex) { ShowPluginStatus(ex.Message); }
    }

    private async Task CheckPluginUpdateAsync(Button button, TextBlock status, Button release,
        PluginManager.Manifest manifest, Uri url)
    {
        button.IsEnabled = false;
        release.Visibility = Visibility.Collapsed;
        release.Tag = null;
        release.Click -= OpenPluginRelease;
        status.Visibility = Visibility.Visible;
        status.Text = P("Проверяем обновление…", "Checking for an update…");
        try
        {
            using var response = await PluginUpdatesHttp.GetAsync(url, pluginChecks.Token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(pluginChecks.Token));
            var root = json.RootElement;
            if (root.GetProperty("id").GetString() != manifest.Id ||
                !Version.TryParse(root.GetProperty("version").GetString(), out var latest) || latest.Build < 0 || latest.Revision >= 0 ||
                !PluginManager.TryHttps(root.GetProperty("releaseUrl").GetString(), out var page))
                throw new InvalidDataException(P("Некорректные сведения о выпуске.", "Invalid release information."));
            if (latest <= Version.Parse(manifest.Version))
                status.Text = P("Обновлений нет.", "No update available.");
            else
            {
                status.Text = string.Format(P("Доступна версия {0}.", "Version {0} is available."), latest);
                release.Tag = page;
                release.Click += OpenPluginRelease;
                release.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (pluginChecks.IsCancellationRequested) { }
        catch (Exception ex)
        {
            status.Text = string.Format(P("Не удалось проверить обновление. {0}", "Could not check for an update. {0}"), ex.Message);
        }
        finally { button.IsEnabled = true; }
    }

    private void OpenPluginRelease(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Uri page }) return;
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowPluginStatus(ex.Message); }
    }

    private void OpenPluginFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(PluginManager.OpenablePackageDirectory()) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowPluginStatus(ex.Message); }
    }

    private async void RestartForPluginsClicked(object sender, RoutedEventArgs e)
    {
        if (exiting) return;
        RestartForPluginsButton.IsEnabled = false;
        var app = (App)Application.Current;
        app.RestartToPlugins = true;
        app.RestartRequested = true;
        await ExitAsync();
    }

    private void ShowPluginStatus(string message)
    {
        PluginStatus.Text = message;
        PluginStatus.Visibility = Visibility.Visible;
    }

    private static void SetPluginAction(Button button, string icon, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = label });
        button.Content = content;
        AutomationProperties.SetName(button, label);
    }

    private static string P(string ru, string en) => Ui.Translate(ru, en);
}
