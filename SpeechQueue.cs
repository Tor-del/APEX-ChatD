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
    private readonly EdgeSpeechClient edge = new();
    private static readonly HttpClient compatibleHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? current;
    private ChatMessage? currentMessage;
    private IWavePlayer? output;
    private IReadOnlyList<SpeechVoice> edgeVoices = EdgeVoiceCatalog.Defaults;
    private bool running;
    private bool paused;
    private bool enabled;
    private string? lastError;
    private Settings options = new();

    public int Count => pending.Count;
    public bool Paused => paused;

    public Task<IReadOnlyList<SpeechVoice>> GetEdgeVoicesAsync(CancellationToken ct) => edge.GetVoicesAsync(ct);

    public void SetEdgeVoices(IReadOnlyList<SpeechVoice> voices) => edgeVoices = voices;

    private SpeechVoice? ChooseEdgeVoice(Settings settings)
    {
        var language = settings.EdgeLanguage ?? EdgeVoiceCatalog.LocaleFromVoice(settings.EdgeVoice);
        var choices = settings.RandomEdgeVoiceInLanguage
            ? edgeVoices.Where(voice => string.Equals(voice.Locale, language, StringComparison.OrdinalIgnoreCase)).ToArray()
            : edgeVoices;
        return choices.Count == 0 ? null : choices[Random.Shared.Next(choices.Count)];
    }

    public void Configure(Settings settings)
    {
        options = settings with { };
        lastError = null;
        enabled = settings.Speech;
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
        var text = SpeechTextFilter.RemoveLinks(message.Text);
        if (string.IsNullOrWhiteSpace(text))
            return;
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
        else
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
        if (!running && enabled && !paused && pending.Count > 0)
            _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        running = true;
        try
        {
            while (enabled && !paused && pending.TryDequeue(out var message))
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
        SpeechVoice? randomVoice = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var currentOptions = options;
                var voice = currentOptions.Edge && currentOptions.RandomEdgeVoice
                    ? randomVoice ??= ChooseEdgeVoice(currentOptions)
                    : null;
                await SpeakAsync(message, currentOptions, voice, ct);
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
            while (paused && options.RetrySpeech)
                await Task.Delay(200, ct);
        } while (options.RetrySpeech);
    }

    private async Task SpeakAsync(ChatMessage message, Settings settings, SpeechVoice? voiceChoice, CancellationToken ct)
    {
        var text = settings.ReadName ? $"{message.Name}: {message.Text}" : message.Text;
        using var bytes = new MemoryStream();
        var format = SpeechAudioFormat.Wave;
        if (settings.CompatibleTts)
        {
            using var client = new OpenAiSpeechClient(
                new(settings.TtsEndpoint, settings.TtsModel, settings.ReadTtsKey(), settings.TtsFormat), compatibleHttp);
            format = client.AudioFormat;
            var request = new SpeechRequest(text, settings.TtsVoice, "", settings.Rate);
            await foreach (var chunk in client.StreamAsync(request, ct))
                await bytes.WriteAsync(chunk, ct);
        }
        else if (settings.Edge)
        {
            format = ((ISpeechProvider)edge).AudioFormat;
            var request = new SpeechRequest(text, voiceChoice?.ShortName ?? settings.EdgeVoice,
                voiceChoice?.Locale ?? settings.EdgeLanguage ?? EdgeVoiceCatalog.LocaleFromVoice(settings.EdgeVoice), settings.Rate);
            await foreach (var chunk in edge.StreamAsync(request, ct))
                await bytes.WriteAsync(chunk, ct);
        }
        else
        {
            using var synth = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item => item.Id == settings.WindowsVoice);
            if (voice is not null)
                synth.Voice = voice;
            synth.Options.SpeakingRate = Math.Clamp(1 + settings.Rate / 100.0, 0.5, 2);
            using var audio = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
            using var stream = audio.AsStreamForRead();
            await stream.CopyToAsync(bytes, ct);
        }

        ct.ThrowIfCancellationRequested();
        bytes.Position = 0;
        using WaveStream reader = format == SpeechAudioFormat.Mp3 ? new Mp3FileReader(bytes) : new WaveFileReader(bytes);
        var decoded = reader.ToSampleProvider();
        var samples = settings.SpeechEffectIntensity > 0 && SpeechFftFilter.Supports(settings.SpeechEffect)
            ? await Task.Run(() => SpeechFftFilter.Apply(decoded, settings.SpeechEffect, settings.SpeechEffectIntensity, ct), ct)
            : SpeechEffectSampleProvider.Apply(decoded, settings.SpeechEffect, settings.SpeechEffectIntensity, settings.SpeechRadioNoise);
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
        var gain = new VolumeSampleProvider(samples) { Volume = settings.Volume / 100f };
        player.Init(gain.ToWaveProvider());
        output = player;
        try
        {
            if (!paused)
                player.Play();
            await finished.Task.WaitAsync(ct);
        }
        finally
        {
            output = null;
            player.Stop();
        }
    }

    private void Report() => status(Ui.F("В очереди: {0}", Count) + (paused ? Ui.T(" · Пауза") : "")
        + (lastError is null ? "" : Ui.F(" · Ошибка озвучивания: {0}", lastError)));
}

