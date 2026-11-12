using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PluginSpeech = ApexChatd.Plugins.SpeechProvider;

namespace TwitchChatAgent;

public partial class MainWindow
{
    private sealed record VoiceListItem(SpeechVoiceProfile Profile, string Label);

    private PluginSpeech? EdgeProvider => plugins?.Providers.GetValueOrDefault(SpeechVoiceProfile.EdgeProviderId);
    private bool IsVoiceAvailable(SpeechVoiceProfile profile) => speech.IsVoiceAvailable(profile);
    private bool HasMissingSpeechVoice => settings.Speech && !settings.SpeechVoices.Any(IsVoiceAvailable);

    private void UpdateMissingSpeechVoiceWarning()
    {
        MissingSpeechVoiceWarning.Visibility = HasMissingSpeechVoice ? Visibility.Visible : Visibility.Collapsed;
        VoiceProfilesHint.Visibility = settings.SpeechVoices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenVoicesClicked(object sender, RoutedEventArgs e) => VoicesTab.IsSelected = true;

    private void AddRandomEdgeVoiceClicked(object sender, RoutedEventArgs e)
    {
        if (!HasMissingSpeechVoice || EdgeProvider is not { } edge) return;
        var locale = Ui.LanguageFamily == "en" ? "en-US" : Ui.LanguageFamily;
        var voices = edge.DefaultVoices.Where(voice => voice.Language == locale ||
            voice.Language.StartsWith(locale + "-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (voices.Length == 0)
            voices = edge.DefaultVoices.Where(voice => voice.Language == "en-US").ToArray();
        if (voices.Length == 0) return;
        var voice = voices[Random.Shared.Next(voices.Length)];
        var profile = new SpeechVoiceProfile
        {
            Name = voice.Name, Provider = SpeechProvider.Edge, PluginProviderId = SpeechVoiceProfile.EdgeProviderId, Voice = voice.Id, Language = voice.Language
        };
        settings.SpeechVoices = [.. settings.SpeechVoices, profile];
        settings.PrimarySpeechVoiceId = profile.Id;
        RefreshVoiceProfiles(profile.Id);
        UpdateSpeechSettings();
    }

    private void RefreshVoiceProfiles(Guid? selected = null)
    {
        var voices = settings.SpeechVoices;
        if (!voices.Any(voice => voice.Id == settings.PrimarySpeechVoiceId))
            settings.PrimarySpeechVoiceId = voices.FirstOrDefault()?.Id;

        var items = voices.Select(voice => new VoiceListItem(voice, !IsVoiceAvailable(voice)
            ? string.Format(P("{0} · Провайдер недоступен", "{0} · Provider unavailable"), voice.Name)
            : voice.Id == settings.PrimarySpeechVoiceId ? Ui.F("{0} · Основной", voice.Name) : voice.Name)).ToArray();
        VoiceProfilesList.ItemsSource = items;
        VoiceProfilesList.SelectedItem = items.FirstOrDefault(item => item.Profile.Id == selected);
        UpdateVoiceProfileButtons();
        UpdateMissingSpeechVoiceWarning();
        VoiceProfilesHint.Text = Ui.T("Добавьте голос, чтобы начать озвучивание.");
    }

    private void UpdateVoiceSelectionOptions()
    {
        var random = RandomSpeechVoice.IsChecked == true;
        PinSpeechVoice.Visibility = random ? Visibility.Visible : Visibility.Collapsed;
        ResetPinnedVoices.Visibility = random && PinSpeechVoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VoiceProfileSelected(object sender, SelectionChangedEventArgs e) => UpdateVoiceProfileButtons();

    private void UpdateVoiceProfileButtons()
    {
        var selected = VoiceProfilesList.SelectedItem as VoiceListItem;
        EditVoiceProfileButton.IsEnabled = RemoveVoiceProfile.IsEnabled = selected is not null;
        MakePrimaryVoiceButton.IsEnabled = selected is not null && IsVoiceAvailable(selected.Profile) && selected.Profile.Id != settings.PrimarySpeechVoiceId;
    }

    private void VoiceProfileDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(VoiceProfilesList, source) is not ListBoxItem { DataContext: VoiceListItem item })
            return;
        e.Handled = true;
        OpenVoiceEditor(item.Profile);
    }

    private void AddVoiceProfileClicked(object sender, RoutedEventArgs e) => OpenVoiceEditor();

    private void EditVoiceProfileClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceProfilesList.SelectedItem is VoiceListItem item)
            OpenVoiceEditor(item.Profile);
    }

    private void MakePrimaryVoiceClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceProfilesList.SelectedItem is not VoiceListItem item || item.Profile.Id == settings.PrimarySpeechVoiceId)
            return;
        settings.PrimarySpeechVoiceId = item.Profile.Id;
        RefreshVoiceProfiles(item.Profile.Id);
        UpdateSpeechSettings();
    }

    private void OpenVoiceEditor(SpeechVoiceProfile? existing = null)
    {
        var editor = new SpeechVoiceEditor(this, existing ?? new() { PluginProviderId = EdgeProvider is null ? null : SpeechVoiceProfile.EdgeProviderId },
            existing is null, plugins?.Providers ?? new Dictionary<string, PluginSpeech>(), speech.PreviewAsync);
        if (editor.ShowDialog() != true || editor.Result is not { } profile)
            return;

        var voices = settings.SpeechVoices;
        settings.SpeechVoices = existing is null ? [.. voices, profile]
            : voices.Select(item => item.Id == profile.Id ? profile : item).ToArray();
        RefreshVoiceProfiles(profile.Id);
        UpdateSpeechSettings();
    }

    private void RemoveVoiceProfileClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceProfilesList.SelectedItem is not VoiceListItem item) return;
        settings.SpeechVoices = settings.SpeechVoices.Where(voice => voice.Id != item.Profile.Id).ToArray();
        RefreshVoiceProfiles();
        UpdateSpeechSettings();
    }

}
