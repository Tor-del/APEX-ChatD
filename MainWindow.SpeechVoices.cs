using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PluginSpeech = ApexChatd.Plugins.SpeechProvider;

namespace TwitchChatAgent;

public partial class MainWindow
{
    private sealed class VoiceListItem(SpeechVoiceProfile profile, string label)
    {
        public SpeechVoiceProfile Profile { get; set; } = profile;
        public string Label { get; } = label;
    }
    private sealed record VoiceAssignmentItem(string UserId, string Name, Guid VoiceId, string VoiceLabel);

    private PluginSpeech? EdgeProvider => plugins?.Providers.GetValueOrDefault(SpeechVoiceProfile.EdgeProviderId);
    private bool IsVoiceAvailable(SpeechVoiceProfile profile) => speech.IsVoiceAvailable(profile);
    private bool HasMissingSpeechVoice => settings.Speech && !settings.SpeechVoices.Any(speech.CanUseVoice);

    private void UpdateMissingSpeechVoiceWarning()
    {
        MissingSpeechVoiceWarning.Visibility = HasMissingSpeechVoice ? Visibility.Visible : Visibility.Collapsed;
        AddRandomEdgeVoiceButton.Visibility = EdgeProvider is not null && !settings.SpeechVoices.Any(IsVoiceAvailable)
            ? Visibility.Visible : Visibility.Collapsed;
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
        RefreshVoiceProfiles(profile.Id);
        UpdateSpeechSettings();
    }

    private void RefreshVoiceProfiles(Guid? selected = null)
    {
        var voices = settings.SpeechVoices;
        var items = voices.Select(voice => new VoiceListItem(voice, GetVoiceLabel(voice))).ToArray();
        VoiceProfilesList.ItemsSource = items;
        VoiceProfilesList.SelectedItem = items.FirstOrDefault(item => item.Profile.Id == selected);
        UpdateVoiceProfileButtons();
        UpdateMissingSpeechVoiceWarning();
        VoiceProfilesHint.Text = Ui.T("Добавьте голос, чтобы начать озвучивание.");
        RefreshVoiceAssignmentChoices();
    }

    private string GetVoiceLabel(SpeechVoiceProfile voice) =>
        !IsVoiceAvailable(voice) ? string.Format(P("{0} · Провайдер недоступен", "{0} · Provider unavailable"), voice.Name)
            : voice.Name;

    private void RefreshVoiceAssignmentChoices()
    {
        VoiceAssignmentChoice.ItemsSource = settings.SpeechVoices.Where(speech.CanUseVoice).ToArray();
        RefreshVoiceAssignments();
    }

    private void UpdateVoiceSelectionOptions() =>
        VoiceAssignmentsPanel.Visibility = PinSpeechVoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void RefreshVoiceAssignments()
    {
        var previous = VoiceAssignmentsList.SelectedItem as VoiceAssignmentItem;
        var pendingVoiceId = VoiceAssignmentChoice.SelectedValue as Guid?;
        var labels = settings.SpeechVoices.ToDictionary(voice => voice.Id,
            voice => voice.Enabled ? GetVoiceLabel(voice)
                : string.Format(P("{0} · Выключен", "{0} · Disabled"), GetVoiceLabel(voice)));
        var items = settings.SpeechVoiceAssignments
            .OrderBy(pair => pair.Value.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new VoiceAssignmentItem(pair.Key, pair.Value.Name, pair.Value.VoiceId, labels[pair.Value.VoiceId]))
            .ToArray();
        VoiceAssignmentsList.ItemsSource = items;
        var selected = items.FirstOrDefault(item => item.UserId == previous?.UserId);
        VoiceAssignmentsList.SelectedItem = selected;
        if (selected is not null && selected.VoiceId == previous?.VoiceId && pendingVoiceId is not null)
            VoiceAssignmentChoice.SelectedValue = pendingVoiceId;
        VoiceAssignmentsList.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoiceAssignmentsHint.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearVoiceAssignments.IsEnabled = items.Length > 0;
    }

    private void VoiceAssignmentSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = VoiceAssignmentsList.SelectedItem as VoiceAssignmentItem;
        VoiceAssignmentEditor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        VoiceAssignmentChoice.SelectedValue = selected?.VoiceId;
        UpdateApplyVoiceAssignment();
    }

    private void VoiceAssignmentChoiceChanged(object sender, SelectionChangedEventArgs e) => UpdateApplyVoiceAssignment();

    private void UpdateApplyVoiceAssignment() =>
        ApplyVoiceAssignment.IsEnabled = VoiceAssignmentsList.SelectedItem is VoiceAssignmentItem selected &&
            VoiceAssignmentChoice.SelectedValue is Guid id && id != selected.VoiceId;

    private void ApplyVoiceAssignmentClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceAssignmentsList.SelectedItem is VoiceAssignmentItem selected &&
            VoiceAssignmentChoice.SelectedValue is Guid id && id != selected.VoiceId)
        {
            settings.SpeechVoiceAssignments[selected.UserId] = new(selected.Name, id);
            SaveVoiceAssignments();
        }
    }

    private void RemoveVoiceAssignmentClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceAssignmentsList.SelectedItem is VoiceAssignmentItem selected && settings.SpeechVoiceAssignments.Remove(selected.UserId))
            SaveVoiceAssignments();
    }

    private void ClearVoiceAssignmentsClicked(object sender, RoutedEventArgs e)
    {
        settings.SpeechVoiceAssignments.Clear();
        SaveVoiceAssignments();
    }

    private void SaveVoiceAssignments()
    {
        RefreshVoiceAssignments();
        SpeechSettingsStatus.Text = "";
        SaveSettings(SpeechSettingsStatus);
    }

    private void VoiceProfileSelected(object sender, SelectionChangedEventArgs e) => UpdateVoiceProfileButtons();

    private void UpdateVoiceProfileButtons()
    {
        var selected = VoiceProfilesList.SelectedItem as VoiceListItem;
        EditVoiceProfileButton.IsEnabled = RemoveVoiceProfile.IsEnabled = selected is not null;
    }

    private void VoiceProfileDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(VoiceProfilesList, source) is not ListBoxItem { DataContext: VoiceListItem item })
            return;
        for (var element = source; element is Visual; element = VisualTreeHelper.GetParent(element))
        {
            if (element is CheckBox) return;
            if (element is ListBoxItem) break;
        }
        e.Handled = true;
        OpenVoiceEditor(item.Profile);
    }

    private void AddVoiceProfileClicked(object sender, RoutedEventArgs e) => OpenVoiceEditor();

    private void EditVoiceProfileClicked(object sender, RoutedEventArgs e)
    {
        if (VoiceProfilesList.SelectedItem is VoiceListItem item)
            OpenVoiceEditor(item.Profile);
    }

    private void VoiceProfileToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: VoiceListItem item } checkBox)
            return;
        item.Profile = item.Profile with { Enabled = checkBox.IsChecked == true };
        settings.SpeechVoices = settings.SpeechVoices.Select(voice => voice.Id == item.Profile.Id ? item.Profile : voice).ToArray();
        RefreshVoiceAssignmentChoices();
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
        foreach (var (userId, assignment) in settings.SpeechVoiceAssignments)
            if (assignment.VoiceId == item.Profile.Id)
                settings.SpeechVoiceAssignments.Remove(userId);
        RefreshVoiceProfiles();
        UpdateSpeechSettings();
    }

}
