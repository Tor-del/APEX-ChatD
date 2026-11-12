using System.IO;
using System.Windows;

namespace ApexChatd.Plugins;

public abstract class Plugin
{
    public abstract void Register(IPluginContext context);
    public virtual Task StartAsync(CancellationToken stopping) => Task.CompletedTask;
    public virtual Task StopAsync() => Task.CompletedTask;
}

public interface IPluginContext
{
    Version AppVersion { get; }
    string Language { get; }
    string Translate(string key, string? fallback = null, string? fallbackLanguage = null) => fallback ?? key;
    Window MainWindow { get; }
    string DataDirectory { get; }
    void RegisterTab(string title, Func<FrameworkElement> createContent);
    void RegisterShortcut(string id, string title, Func<CancellationToken, Task> handler) =>
        throw new NotSupportedException("This host does not support plugin shortcuts.");
    void RegisterSpeechProvider(string id, SpeechProvider provider);
    void SubscribeToChat(Func<ChatEvent, CancellationToken, Task> handler, bool includeTests = false);
    Task RequestExitAsync();
    void Notify(string title, string message);
    void ApplyWindowTheme(Window window);
    void Log(string message);
}

public sealed record ChatEvent(string Id, string Author, string Text, DateTimeOffset ReceivedAt, bool IsTest);
public sealed record Voice(string Id, string Name, string Language);
public sealed record SpeechRequest(string Text, string VoiceId, string Language, int Rate, string Options);
public enum AudioFormat { Wave, Mp3 }

public abstract class SpeechProvider
{
    public abstract string Name { get; }
    public virtual string? OptionsLabel => null;
    public virtual IReadOnlyList<Voice> DefaultVoices => [];
    public virtual Task<IReadOnlyList<Voice>> GetVoicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DefaultVoices);

    // Write a complete WAV or MP3 to destination. The host owns the stream.
    public abstract Task<AudioFormat> SynthesizeAsync(SpeechRequest request, Stream destination,
        CancellationToken cancellationToken);
}
