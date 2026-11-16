using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using PluginSpeech = ApexChatd.Plugins.SpeechProvider;
using ScreenReader.Speech;
using Windows.Media.SpeechSynthesis;

namespace TwitchChatAgent;

internal sealed class SpeechQueue(Func<string, PluginSpeech?> findProvider, Action<string> status)
{
    private static readonly TimeSpan RetryLimit = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly Queue<ChatMessage> pending = new();
    private readonly Dictionary<string, Guid> pinnedVoices = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient compatibleHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? current;
    private ChatMessage? currentMessage;
    private IWavePlayer? output;
    private bool running;
    private bool paused;
    private bool previewing;
    private bool enabled;
    private string? lastError;
    private Settings options = new();

    public int Count => pending.Count;
    public bool Paused => paused;

    public bool IsVoiceAvailable(SpeechVoiceProfile profile) => profile.ExtensionProviderId is not { } id || findProvider(id) is not null;

    public async Task PreviewAsync(string text, SpeechVoiceProfile profile, CancellationToken ct)
    {
        if (previewing) throw new InvalidOperationException(Ui.XamlText("PlayingSpeechSample"));
        ct.ThrowIfCancellationRequested();
        previewing = true;
        try
        {
            if (!paused) output?.Pause();
            await PlaySpeechAsync(text, options, profile, ct, ct, preview: true);
        }
        finally
        {
            previewing = false;
            if (enabled && !paused)
            {
                if (current?.IsCancellationRequested == false) output?.Play();
                Start();
            }
        }
    }

    public void ResetPinnedVoices() => pinnedVoices.Clear();

    private SpeechVoiceProfile ChooseVoice(ChatMessage message)
    {
        var voices = options.SpeechVoices;
        if (!options.RandomSpeechVoice)
            return Array.Find(voices, voice => voice.Id == options.PrimarySpeechVoiceId) ?? voices[0];

        var key = message.UserId is { Length: > 0 } id ? "id:" + id : "name:" + message.Name;
        if (options.PinSpeechVoice && pinnedVoices.TryGetValue(key, out var pinnedId) &&
            Array.Find(voices, voice => voice.Id == pinnedId) is { } pinned)
            return pinned;

        var selected = voices[Random.Shared.Next(voices.Length)];
        if (options.PinSpeechVoice)
            pinnedVoices[key] = selected.Id;
        return selected;
    }

    public void Configure(Settings settings)
    {
        options = settings with { SpeechVoices = settings.SpeechVoices.Where(IsVoiceAvailable).ToArray() };
        lastError = null;
        enabled = settings.Speech && options.SpeechVoices.Length > 0;
        if (!enabled)
        {
            pending.Clear();
            Skip();
            paused = false;
        }
        Report();
    }

    public void Add(ChatMessage message)
    {
        if (!enabled)
            return;
        var filter = options.SpeechFilter;
        if (filter.Enabled)
        {
            if (string.IsNullOrWhiteSpace(filter.Prefix))
                return;
            var matches = message.Text.AsSpan().TrimStart().StartsWith(filter.Prefix, StringComparison.OrdinalIgnoreCase);
            if (matches == filter.Inverted)
                return;
        }

        var text = SpeechTextFilter.Prepare(message, options.SkipSpeechEmotes);
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (options.RandomSpeechVoice && options.PinSpeechVoice)
            ChooseVoice(message);
        pending.Enqueue(text == message.Text ? message : message with { Text = text });
        Report();
        Start();
    }

    public void TogglePause()
    {
        if (!enabled)
            return;
        paused = !paused;
        if (paused)
            output?.Pause();
        else if (!previewing)
        {
            output?.Play();
            Start();
        }
        Report();
    }

    public void Skip() => current?.Cancel();

    public void Remove(Func<ChatMessage, bool> matches)
    {
        var count = pending.Count;
        for (int index = 0; index < count; index++)
        {
            var message = pending.Dequeue();
            if (!matches(message))
                pending.Enqueue(message);
        }

        if (currentMessage is not null && matches(currentMessage))
            Skip();
        Report();
    }

    public void Clear()
    {
        pending.Clear();
        Report();
    }

    private void Start()
    {
        if (!running && enabled && !paused && !previewing && pending.Count > 0)
            _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        running = true;
        try
        {
            while (enabled && !paused && !previewing && pending.TryDequeue(out var message))
            {
                using var cancellation = new CancellationTokenSource();
                current = cancellation;
                currentMessage = message;
                try
                {
                    lastError = null;
                    Report();
                    await SpeakWithRetryAsync(message, cancellation.Token);
                }
                catch (Exception) when (cancellation.IsCancellationRequested) { }
                finally
                {
                    current = null;
                    currentMessage = null;
                }
            }
        }
        finally
        {
            running = false;
            Report();
        }
    }

    private async Task SpeakWithRetryAsync(ChatMessage message, CancellationToken ct)
    {
        using var retryWindow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retryWindow.CancelAfter(RetryLimit);
        Guid? selectedVoice = null;
        string? error = null;
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var currentOptions = options;
                if (currentOptions.SpeechVoices.Length == 0)
                    return;
                var synthesisCt = currentOptions.RetrySpeech ? retryWindow.Token : ct;
                try
                {
                    var voice = Array.Find(currentOptions.SpeechVoices, profile => profile.Id == selectedVoice) ?? ChooseVoice(message);
                    selectedVoice = voice.Id;
                    var text = message.Text;
                    if (currentOptions.ReadName && SpeechTextFilter.CleanForSpeech(message.Name) is { Length: > 0 } name)
                    {
                        var phrase = SpeechTextFilter.CleanForSpeech(currentOptions.ReadNamePhrase);
                        text = phrase.Length == 0 ? $"{name}: {text}" : $"{name} {phrase} {text}";
                    }
                    await PlaySpeechAsync(text, currentOptions, voice, ct, synthesisCt);
                    lastError = null;
                    Report();
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    error = ex is OperationCanceledException && synthesisCt.IsCancellationRequested
                        ? Ui.T("Истекла минута на озвучивание сообщения.") : ex.Message;
                }

                if (!options.RetrySpeech || retryWindow.IsCancellationRequested)
                    break;
                lastError = Ui.F("{0} · Повторяем через 5 секунд", error);
                Report();

                await Task.Delay(RetryDelay, retryWindow.Token);
                while ((paused || previewing) && options.RetrySpeech)
                    await Task.Delay(200, retryWindow.Token);
            } while (options.RetrySpeech && !retryWindow.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (retryWindow.IsCancellationRequested && !ct.IsCancellationRequested) { }

        lastError = error;
        Report();
    }

    private async Task PlaySpeechAsync(string text, Settings settings, SpeechVoiceProfile profile, CancellationToken ct,
        CancellationToken synthesisCt, bool preview = false)
    {
        synthesisCt.ThrowIfCancellationRequested();
        using var bytes = new MemoryStream();
        var format = SpeechAudioFormat.Wave;
        if (profile.ExtensionProviderId is { } id)
        {
            var provider = findProvider(id) ?? throw new InvalidOperationException(Ui.T("Провайдер недоступен."));
            var request = new ApexChatd.Plugins.SpeechRequest(text, profile.Voice, profile.Language, profile.Rate, profile.ProviderOptions);
            var result = await provider.SynthesizeAsync(request, bytes, synthesisCt);
            format = result switch
            {
                ApexChatd.Plugins.AudioFormat.Wave => SpeechAudioFormat.Wave,
                ApexChatd.Plugins.AudioFormat.Mp3 => SpeechAudioFormat.Mp3,
                _ => throw new InvalidDataException("Unsupported plugin audio format.")
            };
        }
        else if (profile.Provider == SpeechProvider.Compatible)
        {
            using var client = new OpenAiSpeechClient(
                new(profile.Endpoint, profile.Model, profile.ReadApiKey(), profile.Format), compatibleHttp);
            format = client.AudioFormat;
            var request = new SpeechRequest(text, profile.Voice, "", profile.Rate);
            await foreach (var chunk in client.StreamAsync(request, synthesisCt))
                await bytes.WriteAsync(chunk, synthesisCt);
        }
        else
        {
            using var synth = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item => item.Id == profile.Voice);
            if (voice is not null)
                synth.Voice = voice;
            synth.Options.SpeakingRate = Math.Clamp(1 + profile.Rate / 100.0, 0.5, 2);
            using var audio = await synth.SynthesizeTextToStreamAsync(text).AsTask(synthesisCt);
            using var stream = audio.AsStreamForRead();
            await stream.CopyToAsync(bytes, synthesisCt);
        }

        synthesisCt.ThrowIfCancellationRequested();
        bytes.Position = 0;
        using WaveStream reader = format == SpeechAudioFormat.Mp3 ? new Mp3FileReader(bytes) : new WaveFileReader(bytes);
        var decoded = reader.ToSampleProvider();
        var pitch = profile.Pitch;
        var formant = profile.Formant;
        var fft = profile.Intensity > 0 && SpeechFftFilter.Supports(profile.Effect);
        var samples = fft || pitch != 0 || formant != 0
            ? await Task.Run(() => SpeechVoiceTransform.Apply(ApplyEffect(), pitch, formant, synthesisCt), synthesisCt)
            : ApplyEffect();
        synthesisCt.ThrowIfCancellationRequested();
        using var device = AudioOutputDevices.Resolve(settings.AudioOutputId);
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is null)
                finished.TrySetResult();
            else
                finished.TrySetException(e.Exception);
        };
        var gain = new VolumeSampleProvider(samples)
        {
            Volume = Math.Clamp(settings.Volume, 0, 100) * Math.Clamp(profile.Volume, 0, 200) / 10_000f
        };
        player.Init(gain.ToWaveProvider());
        synthesisCt.ThrowIfCancellationRequested();
        if (!preview) output = player;
        try
        {
            if (preview || (!paused && !previewing))
                player.Play();
            await finished.Task.WaitAsync(ct);
        }
        finally
        {
            if (!preview) output = null;
            player.Stop();
        }

        ISampleProvider ApplyEffect() => fft
            ? SpeechFftFilter.Apply(decoded, profile.Effect, profile.Intensity, synthesisCt)
            : SpeechEffectSampleProvider.Apply(decoded, profile.Effect, profile.Intensity, profile.RadioNoise);
    }

    private void Report() => status(Ui.F("В очереди: {0}", Count) + (paused ? Ui.T(" · Пауза") : "")
        + (lastError is null ? "" : Ui.F(" · Ошибка озвучивания: {0}", lastError)));
}

