using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using ScreenReader.Edge;
using ScreenReader.Speech;
using Windows.Media.SpeechSynthesis;

namespace TwitchChatAgent;

internal partial class SpeechVoiceEditor : Window
{
    private static readonly SpeechProvider[] Providers = [SpeechProvider.Edge, SpeechProvider.Windows, SpeechProvider.Compatible];
    private readonly SpeechVoiceProfile draft;
    private readonly Func<Task<IReadOnlyList<SpeechVoice>>> loadEdgeVoices;
    private readonly Func<string, SpeechVoiceProfile, CancellationToken, Task> playSample;
    private CancellationTokenSource? samplePlayback;
    private IReadOnlyList<SpeechVoice> edgeVoices = EdgeVoiceCatalog.Defaults;
    private bool loadingVoiceProfile;
    private bool edgeCatalogLoaded;
    private bool edgeCatalogLoading;
    private bool closed;

    public SpeechVoiceProfile? Result { get; private set; }

    public SpeechVoiceEditor(Window owner, SpeechVoiceProfile profile, bool adding,
        Func<Task<IReadOnlyList<SpeechVoice>>> loadEdgeVoices, Func<string, SpeechVoiceProfile, CancellationToken, Task> playSample)
    {
        this.loadEdgeVoices = loadEdgeVoices;
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
        };
        loadingVoiceProfile = true;
        try
        {
            VoiceProfileName.Text = profile.Name;
            VoiceProfileStatus.Text = "";
            SpeechSample.Text = Ui.XamlText("SpeechSamplePhrase");
            Engine.SelectedIndex = Math.Max(0, Array.IndexOf(Providers, profile.Provider));
            var windows = SpeechSynthesizer.AllVoices;
            var windowsLanguage = windows.FirstOrDefault(voice => voice.Id == profile.Voice)?.Language
                ?? windows.FirstOrDefault(voice => voice.Language.StartsWith(Ui.Language + "-", StringComparison.OrdinalIgnoreCase))?.Language;
            PopulateLanguages(WindowsLanguage, windows.Select(voice => voice.Language), windowsLanguage);
            PopulateWindowsVoices(profile.Provider == SpeechProvider.Windows ? profile.Voice : null);
            PopulateEdgeLanguages(profile.Provider == SpeechProvider.Edge && !string.IsNullOrWhiteSpace(profile.Language)
                    ? profile.Language : EdgeVoiceCatalog.InterfaceLocale,
                profile.Provider == SpeechProvider.Edge ? profile.Voice : null);
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
        EdgeLanguage.SelectionChanged += (_, _) =>
        {
            if (!loadingVoiceProfile) PopulateEdgeVoices(edgeVoices);
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

    private SpeechProvider SelectedProvider => Engine.SelectedIndex >= 0 ? Providers[Engine.SelectedIndex] : SpeechProvider.Edge;

    private SpeechVoiceProfile? ReadProfile()
    {
        try
        {
            var provider = SelectedProvider;
            var endpoint = TtsEndpoint.Text.Trim();
            var model = TtsModel.Text.Trim();
            var (voice, language, display) = provider switch
            {
                SpeechProvider.Windows when WindowsVoice.SelectedItem is VoiceInformation windows => (windows.Id, windows.Language, windows.DisplayName),
                SpeechProvider.Edge when EdgeVoice.SelectedItem is SpeechVoice edge => (edge.ShortName, edge.Locale, edge.FriendlyName),
                SpeechProvider.Compatible => (TtsVoice.Text.Trim(), "", TtsVoice.Text.Trim()),
                _ => ("", "", "")
            };
            if (string.IsNullOrEmpty(voice))
            {
                VoiceProfileStatus.Text = Ui.T("Выберите или укажите голос.");
                return null;
            }
            if (provider == SpeechProvider.Compatible &&
                (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(model)))
            {
                VoiceProfileStatus.Text = Ui.T("Укажите HTTP(S)-адрес и модель TTS.");
                return null;
            }
            return draft with
            {
                Name = string.IsNullOrWhiteSpace(VoiceProfileName.Text) ? display : VoiceProfileName.Text.Trim(),
                Provider = provider, Voice = voice, Language = language,
                Endpoint = endpoint, Model = model,
                Format = TtsFormat.SelectedIndex == 1 ? SpeechAudioFormat.Wave : SpeechAudioFormat.Mp3,
                ProtectedApiKey = provider == SpeechProvider.Compatible ? SpeechVoiceProfile.ProtectApiKey(TtsApiKey.Password) : "",
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

    private void UpdateProfileProvider()
    {
        if (loadingVoiceProfile) return;
        var provider = SelectedProvider;
        WindowsVoiceOptions.Visibility = provider == SpeechProvider.Windows ? Visibility.Visible : Visibility.Collapsed;
        EdgeVoiceOptions.Visibility = provider == SpeechProvider.Edge ? Visibility.Visible : Visibility.Collapsed;
        CompatibleTtsOptions.Visibility = provider == SpeechProvider.Compatible ? Visibility.Visible : Visibility.Collapsed;
        if (provider == SpeechProvider.Edge)
            _ = LoadEdgeCatalogAsync();
    }

    private static void PopulateLanguages(ComboBox selector, IEnumerable<string> locales, string? preferred)
    {
        selector.ItemsSource = locales.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(locale => new VoiceLanguage(locale, EdgeVoiceCatalog.LanguageName(locale))).OrderBy(item => item.Name).ToArray();
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

    private void PopulateEdgeLanguages(string? language, string? preferred)
    {
        var choices = edgeVoices;
        if (!string.IsNullOrWhiteSpace(preferred) && choices.All(voice => voice.ShortName != preferred))
            choices = [.. choices, new SpeechVoice(preferred, preferred, language ?? EdgeVoiceCatalog.LocaleFromVoice(preferred), "")];
        PopulateLanguages(EdgeLanguage, choices.Select(voice => voice.Locale), language);
        PopulateEdgeVoices(choices, preferred);
    }

    private void PopulateEdgeVoices(IReadOnlyList<SpeechVoice> choices, string? preferred = null)
    {
        var locale = EdgeLanguage.SelectedValue as string;
        EdgeVoice.ItemsSource = choices.Where(voice => string.Equals(voice.Locale, locale, StringComparison.OrdinalIgnoreCase))
            .OrderBy(voice => voice.FriendlyName).ToArray();
        EdgeVoice.SelectedValue = preferred;
        if (EdgeVoice.SelectedIndex < 0) EdgeVoice.SelectedIndex = 0;
    }

    private async Task LoadEdgeCatalogAsync()
    {
        if (edgeCatalogLoaded || edgeCatalogLoading || closed) return;
        edgeCatalogLoading = true;
        EdgeCatalogStatus.Text = Ui.T("Загружаем каталог Edge TTS…");
        try
        {
            var loaded = await loadEdgeVoices();
            if (closed) return;
            edgeVoices = loaded;
            edgeCatalogLoaded = true;
            loadingVoiceProfile = true;
            try { PopulateEdgeLanguages(EdgeLanguage.SelectedValue as string, EdgeVoice.SelectedValue as string); }
            finally { loadingVoiceProfile = false; }
            EdgeCatalogStatus.Text = "";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed)
                EdgeCatalogStatus.Text = Ui.F("Каталог Edge TTS недоступен ({0}). Показаны встроенные варианты.", EdgeSpeechClient.DescribeFailure(ex));
        }
        finally { edgeCatalogLoading = false; }
    }

    private void UpdateSpeechEffectVisibility()
    {
        SpeechEffectControls.Visibility = SpeechEffectChoice.SelectedIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeechRadioNoise.Visibility = SpeechEffectChoice.SelectedIndex == (int)SpeechEffect.Radio
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
