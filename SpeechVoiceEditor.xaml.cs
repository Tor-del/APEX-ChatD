using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using PluginSpeech = ApexChatd.Plugins.SpeechProvider;
using PluginVoiceInfo = ApexChatd.Plugins.Voice;
using System.Globalization;
using ScreenReader.Speech;
using Windows.Media.SpeechSynthesis;

namespace TwitchChatAgent;

internal partial class SpeechVoiceEditor : Window
{
    private sealed record ProviderChoice(string Name, SpeechProvider BuiltIn, string? Id = null, PluginSpeech? Plugin = null);
    private sealed record VoiceLanguage(string Code, string Name);
    private readonly CancellationTokenSource catalogLifetime = new();
    private readonly SpeechVoiceProfile draft;
    private readonly Func<string, SpeechVoiceProfile, CancellationToken, Task> playSample;
    private CancellationTokenSource? samplePlayback;
    private IReadOnlyList<PluginVoiceInfo> pluginVoices = [];
    private bool loadingVoiceProfile;
    private bool closed;

    public SpeechVoiceProfile? Result { get; private set; }

    public SpeechVoiceEditor(Window owner, SpeechVoiceProfile profile, bool adding,
        IReadOnlyDictionary<string, PluginSpeech> extensions, Func<string, SpeechVoiceProfile, CancellationToken, Task> playSample)
    {
        this.playSample = playSample;
        draft = profile;
        InitializeComponent();
        Owner = owner;
        Title = Ui.T(adding ? "Добавить голос" : "Изменить голос");
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 64);
        SourceInitialized += (_, _) => ((App)Application.Current).Themes.ApplyTitleBar(this);
        Closed += (_, _) =>
        {
            closed = true;
            samplePlayback?.Cancel();
            catalogLifetime.Cancel();
            catalogLifetime.Dispose();
        };
        loadingVoiceProfile = true;
        try
        {
            VoiceProfileName.Text = profile.Name;
            VoiceProfileStatus.Text = "";
            SpeechSample.Text = Ui.XamlText("SpeechSamplePhrase");
            var choices = new List<ProviderChoice>
            {
                new(Ui.XamlText("WindowsVoice"), SpeechProvider.Windows),
                new(Ui.XamlText("CompatibleTtsProvider"), SpeechProvider.Compatible)
            };
            choices.AddRange(extensions.Select(item => new ProviderChoice(item.Value.Name,
                item.Key == SpeechVoiceProfile.EdgeProviderId ? SpeechProvider.Edge : SpeechProvider.Windows, item.Key, item.Value)));
            var choice = choices.FirstOrDefault(item => profile.ExtensionProviderId is { } id ? item.Id == id :
                item.Id is null && item.BuiltIn == profile.Provider);
            if (choice is null)
            {
                choice = new(Ui.F("Провайдер недоступен ({0})", profile.ExtensionProviderId), profile.Provider, profile.ExtensionProviderId);
                choices.Add(choice);
            }
            Engine.ItemsSource = choices;
            Engine.SelectedItem = choice;
            var windows = SpeechSynthesizer.AllVoices;
            var windowsLanguage = windows.FirstOrDefault(voice => voice.Id == profile.Voice)?.Language
                ?? windows.FirstOrDefault(voice => voice.Language.StartsWith(Ui.LanguageFamily + "-", StringComparison.OrdinalIgnoreCase))?.Language;
            PopulateLanguages(WindowsLanguage, windows.Select(voice => voice.Language), windowsLanguage);
            PopulateWindowsVoices(profile.Provider == SpeechProvider.Windows ? profile.Voice : null);
            TtsEndpoint.Text = profile.Endpoint;
            TtsModel.Text = profile.Model;
            TtsVoice.Text = profile.Provider == SpeechProvider.Compatible ? profile.Voice : "alloy";
            TtsFormat.SelectedIndex = profile.Format == SpeechAudioFormat.Wave ? 1 : 0;
            TtsApiKey.Clear();
            try { TtsApiKey.Password = profile.ReadApiKey(); }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                VoiceProfileStatus.Text = Ui.T("Не удалось прочитать ключ TTS. Введите его заново.");
            }
            Rate.Value = profile.Rate;
            VoiceVolume.Value = profile.Volume;
            SpeechEffectChoice.SelectedIndex = Enum.IsDefined(profile.Effect) ? (int)profile.Effect : 0;
            SpeechEffectIntensity.Value = profile.Intensity;
            SpeechPitch.Value = profile.Pitch;
            SpeechFormant.Value = profile.Formant;
            SpeechRadioNoise.IsChecked = profile.RadioNoise;
            UpdateSpeechEffectVisibility();
        }
        finally
        {
            loadingVoiceProfile = false;
        }
        Engine.SelectionChanged += (_, _) => UpdateProfileProvider();
        WindowsLanguage.SelectionChanged += (_, _) =>
        {
            if (!loadingVoiceProfile) PopulateWindowsVoices();
        };
        PluginLanguage.SelectionChanged += (_, _) =>
        {
            if (!loadingVoiceProfile) PopulatePluginVoices();
        };
        SpeechEffectChoice.SelectionChanged += (_, _) => UpdateSpeechEffectVisibility();
        Loaded += (_, _) => { UpdateProfileProvider(); VoiceProfileName.Focus(); };
    }

    private void SaveVoiceProfileClicked(object sender, RoutedEventArgs e)
    {
        if (ReadProfile() is not { } profile) return;
        Result = profile;
        DialogResult = true;
    }

    private async void ListenVoiceClicked(object sender, RoutedEventArgs e)
    {
        if (samplePlayback is not null)
        {
            samplePlayback.Cancel();
            return;
        }

        var text = SpeechSample.Text.Trim();
        if (text.Length == 0)
        {
            VoiceProfileStatus.Text = Ui.XamlText("EmptySpeechSample");
            SpeechSample.Focus();
            return;
        }
        if (ReadProfile() is not { } profile) return;

        using var cancellation = new CancellationTokenSource();
        samplePlayback = cancellation;
        ListenButton.Content = Ui.XamlText("StopSpeechSample");
        VoiceProfileStatus.Text = Ui.XamlText("PlayingSpeechSample");
        try
        {
            await playSample(text, profile, cancellation.Token);
            if (!closed) VoiceProfileStatus.Text = "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closed) VoiceProfileStatus.Text = "";
        }
        catch (Exception ex)
        {
            if (!closed) VoiceProfileStatus.Text = Ui.F("Не удалось воспроизвести пример: {0}", ex.Message);
        }
        finally
        {
            samplePlayback = null;
            if (!closed) ListenButton.Content = Ui.XamlText("ListenSpeechVoice");
        }
    }

    private ProviderChoice SelectedProvider => (ProviderChoice)Engine.SelectedItem;

    private SpeechVoiceProfile? ReadProfile()
    {
        try
        {
            var choice = SelectedProvider;
            var provider = choice.BuiltIn;
            var endpoint = TtsEndpoint.Text.Trim();
            var model = TtsModel.Text.Trim();
            var (voice, language, display) = choice.Id is not null
                ? ((PluginVoice.SelectedItem as PluginVoiceInfo)?.Id ?? PluginVoice.Text.Trim(), (string?)PluginLanguage.SelectedValue ?? "", (PluginVoice.SelectedItem as PluginVoiceInfo)?.Name ?? PluginVoice.Text.Trim())
                : provider switch
            {
                SpeechProvider.Windows when WindowsVoice.SelectedItem is VoiceInformation windows => (windows.Id, windows.Language, windows.DisplayName),
                SpeechProvider.Compatible => (TtsVoice.Text.Trim(), "", TtsVoice.Text.Trim()),
                _ => ("", "", "")
            };
            if (string.IsNullOrEmpty(voice))
            {
                VoiceProfileStatus.Text = Ui.T("Выберите или укажите голос.");
                return null;
            }
            if (choice.Id is null && provider == SpeechProvider.Compatible &&
                (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(model)))
            {
                VoiceProfileStatus.Text = Ui.T("Укажите HTTP(S)-адрес и модель TTS.");
                return null;
            }
            return draft with
            {
                Name = string.IsNullOrWhiteSpace(VoiceProfileName.Text) ? display : VoiceProfileName.Text.Trim(),
                Provider = provider, PluginProviderId = choice.Id, Voice = voice, Language = language,
                ProviderOptions = choice.Id is null ? "" : PluginOptions.Text,
                Endpoint = endpoint, Model = model,
                Format = TtsFormat.SelectedIndex == 1 ? SpeechAudioFormat.Wave : SpeechAudioFormat.Mp3,
                ProtectedApiKey = choice.Id is null && provider == SpeechProvider.Compatible ? SpeechVoiceProfile.ProtectApiKey(TtsApiKey.Password) : "",
                Rate = (int)Rate.Value, Volume = (int)VoiceVolume.Value, Effect = (SpeechEffect)SpeechEffectChoice.SelectedIndex,
                Intensity = (int)SpeechEffectIntensity.Value, RadioNoise = SpeechRadioNoise.IsChecked == true,
                Pitch = (int)SpeechPitch.Value, Formant = (int)SpeechFormant.Value
            };
        }
        catch (CryptographicException ex)
        {
            VoiceProfileStatus.Text = Ui.F("Не удалось сохранить настройки: {0}", ex.Message);
            return null;
        }
    }

    private async void UpdateProfileProvider()
    {
        if (loadingVoiceProfile) return;
        var choice = SelectedProvider;
        WindowsVoiceOptions.Visibility = choice.Id is null && choice.BuiltIn == SpeechProvider.Windows ? Visibility.Visible : Visibility.Collapsed;
        CompatibleTtsOptions.Visibility = choice.Id is null && choice.BuiltIn == SpeechProvider.Compatible ? Visibility.Visible : Visibility.Collapsed;
        PluginVoiceOptions.Visibility = choice.Id is not null ? Visibility.Visible : Visibility.Collapsed;
        if (choice.Id is null) return;
        var existing = choice.Id == draft.ExtensionProviderId;
        try
        {
            var label = choice.Plugin?.OptionsLabel;
            PluginOptionsLabel.Text = label ?? "";
            PluginOptionsLabel.Visibility = PluginOptions.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
            PluginOptions.Text = existing ? draft.ProviderOptions : "";
            pluginVoices = choice.Plugin?.DefaultVoices ?? [];
            SetPluginLanguages(existing ? draft.Language : null, existing ? draft.Voice : null);
            PluginCatalogStatus.Text = "";
            if (choice.Plugin is null) return;
            var voices = await choice.Plugin.GetVoicesAsync(catalogLifetime.Token);
            if (closed || !ReferenceEquals(SelectedProvider, choice) || voices.Count == 0) return;
            var language = (string?)PluginLanguage.SelectedValue;
            var voice = PluginVoice.Text;
            pluginVoices = voices;
            SetPluginLanguages(language, voice);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed && ReferenceEquals(SelectedProvider, choice))
                PluginCatalogStatus.Text = ex.Message;
        }
    }

    private static void PopulateLanguages(ComboBox selector, IEnumerable<string> locales, string? preferred)
    {
        selector.ItemsSource = locales.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(locale => new VoiceLanguage(locale, LanguageName(locale))).OrderBy(item => item.Name).ToArray();
        selector.SelectedValue = preferred;
        if (selector.SelectedIndex < 0) selector.SelectedIndex = 0;
    }

    private void PopulateWindowsVoices(string? preferred = null)
    {
        var locale = WindowsLanguage.SelectedValue as string;
        WindowsVoice.ItemsSource = SpeechSynthesizer.AllVoices.Where(voice => string.Equals(voice.Language, locale, StringComparison.OrdinalIgnoreCase)).ToArray();
        WindowsVoice.SelectedValue = preferred;
        if (WindowsVoice.SelectedIndex < 0 && string.IsNullOrEmpty(preferred)) WindowsVoice.SelectedIndex = 0;
    }

    private void SetPluginLanguages(string? language, string? voice)
    {
        loadingVoiceProfile = true;
        try
        {
            var locales = pluginVoices.Select(item => item.Language);
            if (!string.IsNullOrEmpty(language)) locales = locales.Append(language);
            var preferred = language ?? pluginVoices.FirstOrDefault(item =>
                item.Language.StartsWith(Ui.LanguageFamily + "-", StringComparison.OrdinalIgnoreCase))?.Language;
            PopulateLanguages(PluginLanguage, locales, preferred);
            PopulatePluginVoices(voice);
        }
        finally { loadingVoiceProfile = false; }
    }

    private void PopulatePluginVoices(string? preferred = null)
    {
        var locale = PluginLanguage.SelectedValue as string;
        var choices = pluginVoices.Where(voice => voice.Language == locale).OrderBy(voice => voice.Name).ToArray();
        PluginVoice.ItemsSource = choices;
        PluginVoice.SelectedItem = choices.FirstOrDefault(voice => voice.Id == preferred) ??
            (string.IsNullOrEmpty(preferred) ? choices.FirstOrDefault() : null);
        if (PluginVoice.SelectedItem is null) PluginVoice.Text = preferred ?? "";
    }

    private static string LanguageName(string locale)
    {
        try { return $"{CultureInfo.GetCultureInfo(locale).NativeName} ({locale})"; }
        catch (CultureNotFoundException) { return locale; }
    }

    private void UpdateSpeechEffectVisibility()
    {
        SpeechEffectControls.Visibility = SpeechEffectChoice.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeechRadioNoise.Visibility = SpeechEffectChoice.SelectedIndex == (int)SpeechEffect.Radio
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
