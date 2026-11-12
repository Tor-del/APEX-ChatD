using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using ScreenReader.Edge;
using ScreenReader.Speech;
using Windows.Media.SpeechSynthesis;

namespace TwitchChatAgent;

internal sealed class SpeechQueue(Action<string> status)
{
    private readonly Queue<ChatMessage> pending = new();
    private readonly Dictionary<string, Guid> pinnedVoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly EdgeSpeechClient edge = new();
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

    public Task<IReadOnlyList<SpeechVoice>> GetEdgeVoicesAsync(CancellationToken ct) => edge.GetVoicesAsync(ct);

    public async Task PreviewAsync(string text, SpeechVoiceProfile profile, CancellationToken ct)
    {
        if (previewing) throw new InvalidOperationException(Ui.XamlText("PlayingSpeechSample"));
        ct.ThrowIfCancellationRequested();
        previewing = true;
        try
        {
            if (!paused) output?.Pause();
            await PlaySpeechAsync(text, options, profile, ct, preview: true);
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
        options = settings with { };
        lastError = null;
        enabled = settings.Speech && settings.SpeechVoices.Length > 0;
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
        Guid? selectedVoice = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var currentOptions = options;
                if (currentOptions.SpeechVoices.Length == 0)
                    return;
                var voice = Array.Find(currentOptions.SpeechVoices, profile => profile.Id == selectedVoice) ?? ChooseVoice(message);
                selectedVoice = voice.Id;
                var text = !currentOptions.ReadName ? message.Text
                    : string.IsNullOrWhiteSpace(currentOptions.ReadNamePhrase) ? $"{message.Name}: {message.Text}"
                    : $"{message.Name} {currentOptions.ReadNamePhrase} {message.Text}";
                await PlaySpeechAsync(text, currentOptions, voice, ct);
                lastError = null;
                Report();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = options.RetrySpeech ? Ui.F("{0} · Повторяем через 5 секунд", ex.Message) : ex.Message;
                Report();
                if (!options.RetrySpeech)
                    return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            while ((paused || previewing) && options.RetrySpeech)
                await Task.Delay(200, ct);
        } while (options.RetrySpeech);
    }

    private async Task PlaySpeechAsync(string text, Settings settings, SpeechVoiceProfile profile, CancellationToken ct,
        bool preview = false)
    {
        using var bytes = new MemoryStream();
        var format = SpeechAudioFormat.Wave;
        if (profile.Provider == SpeechProvider.Compatible)
        {
            using var client = new OpenAiSpeechClient(
                new(profile.Endpoint, profile.Model, profile.ReadApiKey(), profile.Format), compatibleHttp);
            format = client.AudioFormat;
            var request = new SpeechRequest(text, profile.Voice, "", profile.Rate);
            await foreach (var chunk in client.StreamAsync(request, ct))
                await bytes.WriteAsync(chunk, ct);
        }
        else if (profile.Provider == SpeechProvider.Edge)
        {
            format = ((ISpeechProvider)edge).AudioFormat;
            var request = new SpeechRequest(text, profile.Voice, profile.Language, profile.Rate);
            await foreach (var chunk in edge.StreamAsync(request, ct))
                await bytes.WriteAsync(chunk, ct);
        }
        else
        {
            using var synth = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item => item.Id == profile.Voice);
            if (voice is not null)
                synth.Voice = voice;
            synth.Options.SpeakingRate = Math.Clamp(1 + profile.Rate / 100.0, 0.5, 2);
            using var audio = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
            using var stream = audio.AsStreamForRead();
            await stream.CopyToAsync(bytes, ct);
        }

        ct.ThrowIfCancellationRequested();
        bytes.Position = 0;
        using WaveStream reader = format == SpeechAudioFormat.Mp3 ? new Mp3FileReader(bytes) : new WaveFileReader(bytes);
        var decoded = reader.ToSampleProvider();
        var pitch = profile.Pitch;
        var formant = profile.Formant;
        var fft = profile.Intensity > 0 && SpeechFftFilter.Supports(profile.Effect);
        var samples = fft || pitch != 0 || formant != 0
            ? await Task.Run(() => SpeechVoiceTransform.Apply(ApplyEffect(), pitch, formant, ct), ct)
            : ApplyEffect();
        ct.ThrowIfCancellationRequested();
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
            ? SpeechFftFilter.Apply(decoded, profile.Effect, profile.Intensity, ct)
            : SpeechEffectSampleProvider.Apply(decoded, profile.Effect, profile.Intensity, profile.RadioNoise);
    }

    private void Report() => status(Ui.F("В очереди: {0}", Count) + (paused ? Ui.T(" · Пауза") : "")
        + (lastError is null ? "" : Ui.F(" · Ошибка озвучивания: {0}", lastError)));
}

