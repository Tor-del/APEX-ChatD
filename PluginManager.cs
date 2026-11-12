using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using ApexChatd.Plugins;

namespace TwitchChatAgent;

internal sealed class PluginManager
{
    public static readonly Version ApiVersion = new(1, 2, 0);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "TwitchChatAgent");
    private static readonly string Packages = Path.Combine(Root, "Plugins");
    private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "BundledPlugins");
    private static readonly string StatePath = Path.Combine(Root, "plugins.json");
    private static readonly Assembly Sdk = typeof(Plugin).Assembly;
    private static readonly Version AppVersion = typeof(App).Assembly.GetName().Version!;
    private readonly Dictionary<string, Preference> preferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Entry> entries = [];
    private readonly List<Context> active = [];
    private readonly Dictionary<string, ApexChatd.Plugins.SpeechProvider> providers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly MainWindow window;
    private readonly Func<Task> exit;
    private readonly Action<string, string> notify;

    internal sealed record Manifest(string Id, string Name, string Version, string EntryAssembly, string EntryType,
        string MinApiVersion, string MaxApiVersionExclusive, string? MinAppVersion,
        Dictionary<string, string>? Files, string? UpdateUrl, string? TabIcon);
    internal sealed record Shortcut(string Id, string PluginName, string Title, Func<Task> Invoke);
    internal enum Trust { Unreviewed, Reviewed, Invalid }
    private sealed record Preference(bool Enabled, bool Removed);
    internal sealed class Entry(string path, bool bundled)
    {
        public string Path { get; } = path;
        public bool Bundled { get; } = bundled;
        public string Key => (Bundled ? "bundle/" : "user/") + System.IO.Path.GetFileName(Path);
        public Manifest? Package { get; set; }
        public Trust Signature { get; set; }
        public string? Problem { get; set; }
        public bool Compatible { get; set; }
        public bool ValidPayload { get; set; }
        public bool Running { get; set; }
    }

    public PluginManager(MainWindow window, Func<Task> exit, Action<string, string> notify)
    {
        this.window = window;
        this.exit = exit;
        this.notify = notify;
        if (File.Exists(StatePath))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Preference>>(File.ReadAllBytes(StatePath), Json);
            if (saved is not null)
                foreach (var (key, value) in saved)
                    if (value is not null) preferences[key] = value;
        }
        Scan(Bundled, bundled: true);
        Scan(Packages, bundled: false);
    }

    public IEnumerable<Entry> Entries => entries.Where(entry => !Removed(entry));
    public IReadOnlyDictionary<string, ApexChatd.Plugins.SpeechProvider> Providers => providers;
    public IEnumerable<Shortcut> Shortcuts => active.SelectMany(context => context.Shortcuts.Values);
    public bool Enabled(Entry entry) => preferences.TryGetValue(entry.Key, out var value) ? value.Enabled : entry.Bundled;
    private bool Removed(Entry entry) => preferences.TryGetValue(entry.Key, out var value) && value.Removed;
    public static string OpenablePackageDirectory()
    {
        Directory.CreateDirectory(Packages);
        return Packages;
    }

    public static string DataDirectory(string id) => Path.Combine(Root, "PluginData", id);

    private void Scan(string root, bool bundled)
    {
        if (!Directory.Exists(root)) return;
        RejectLink(root);
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var entry = new Entry(path, bundled);
            if (Removed(entry))
            {
                if (!bundled)
                {
                    try
                    {
                        // Only remove direct children of the per-user plugin directory.
                        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Packages, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Invalid plugin directory.");
                        RejectLink(path);
                        Directory.Delete(path, recursive: true);
                        Change(entry, null);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Trace.TraceError(ex.ToString());
                    }
                }
                continue;
            }

            Inspect(entry);
            if (entry.ValidPayload && entry.Package is { } package && entries.Any(other => other.Package?.Id == package.Id))
            {
                entry.Compatible = false;
                entry.Problem = Ui.F("Идентификатор плагина уже используется: {0}", package.Id);
            }
            entries.Add(entry);
        }
    }

    private static void Inspect(Entry entry)
    {
        try
        {
            RejectLink(entry.Path);
            var manifestPath = Path.Combine(entry.Path, "plugin.json");
            RejectLink(manifestPath);
            var raw = File.ReadAllBytes(manifestPath);
            var package = JsonSerializer.Deserialize<Manifest>(raw, Json) ?? throw new InvalidDataException("Empty plugin.json.");
            if (string.IsNullOrWhiteSpace(package.Id) || !Regex.IsMatch(package.Id, @"\A[a-z0-9][a-z0-9.-]{0,99}\z") ||
                package.Id.EndsWith('.') || string.IsNullOrWhiteSpace(package.Name) || string.IsNullOrWhiteSpace(package.EntryType) ||
                string.IsNullOrWhiteSpace(package.EntryAssembly) || package.EntryAssembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !package.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !Version.TryParse(package.Version, out var version) || version.Build < 0 || version.Revision >= 0 ||
                !Version.TryParse(package.MinApiVersion, out var min) || min.Build < 0 || min.Revision >= 0 ||
                !Version.TryParse(package.MaxApiVersionExclusive, out var max) || max.Build < 0 || max.Revision >= 0 || max <= min ||
                package.MinAppVersion is not null && (!Version.TryParse(package.MinAppVersion, out var app) || app.Build < 0 || app.Revision >= 0) ||
                package.UpdateUrl is not null && !TryHttps(package.UpdateUrl, out _))
                throw new InvalidDataException("Invalid plugin.json.");

            entry.Package = package;
            var signaturePath = Path.Combine(entry.Path, "plugin.sig");
            if (File.Exists(signaturePath))
            {
                entry.Signature = Trust.Invalid;
                RejectLink(signaturePath);
                var signature = File.ReadAllBytes(signaturePath);
                using var publicKey = typeof(App).Assembly.GetManifestResourceStream("PluginApprovalKey.pem");
                if (publicKey is null)
                    throw new InvalidDataException(Ui.T("В этой сборке нет ключа проверки плагинов."));
                using var reader = new StreamReader(publicKey);
                using var rsa = RSA.Create();
                rsa.ImportFromPem(reader.ReadToEnd());
                if (!rsa.VerifyData(raw, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new InvalidDataException(Ui.T("Подпись плагина недействительна."));
            }

            if (package.Files is not null || entry.Signature == Trust.Invalid)
                VerifyFiles(entry.Path, package.Files ?? throw new InvalidDataException("Missing file hashes."));

            var assembly = Path.Combine(entry.Path, package.EntryAssembly);
            RejectLink(assembly);
            if (!File.Exists(assembly))
                throw new FileNotFoundException(package.EntryAssembly);

            if (entry.Signature == Trust.Invalid) entry.Signature = Trust.Reviewed;
            entry.ValidPayload = true;
            if (ApiVersion < min || ApiVersion >= max)
                entry.Problem = Ui.F("Требуется API от {0} до {1} (не включая). Доступен API {2}.", min, max, ApiVersion);
            else if (package.MinAppVersion is not null && AppVersion < Version.Parse(package.MinAppVersion))
                entry.Problem = Ui.F("Требуется APEX Chatd {0} или новее.", package.MinAppVersion);
            else
                entry.Compatible = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or CryptographicException or ArgumentException)
        {
            entry.Problem = ex.Message;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Plugin packages cannot contain links.");
    }

    private static void VerifyFiles(string root, Dictionary<string, string> hashes)
    {
        var expected = new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase);
        Walk(root);
        if (expected.Count != 0) throw new InvalidDataException("Plugin files are missing.");

        void Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(path);
                if (Directory.Exists(path))
                {
                    Walk(path);
                    continue;
                }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("plugin.sig", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var file = File.OpenRead(path);
                if (!expected.Remove(relative, out var hash) ||
                    !Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Plugin file has changed: {relative}");
            }
        }
    }

    public void Import(string zip)
    {
        Directory.CreateDirectory(Packages);
        RejectLink(Packages);
        var stage = Path.Combine(Root, ".plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zip, stage);
            var candidate = new Entry(stage, bundled: false);
            Inspect(candidate);
            if (candidate.Package is null || !candidate.ValidPayload)
                throw new InvalidDataException(candidate.Problem);
            var package = candidate.Package;
            if (entries.Any(other => other.Package?.Id == package.Id))
                throw new InvalidOperationException(Ui.T("Плагин с таким идентификатором уже установлен."));
            var path = Path.Combine(Packages, package.Id);
            Directory.Move(stage, path);
            var entry = new Entry(path, bundled: false);
            Inspect(entry);
            entries.Add(entry);
        }
        finally
        {
            if (Directory.Exists(stage) && Path.GetDirectoryName(Path.GetFullPath(stage)) == Root)
                Directory.Delete(stage, recursive: true);
        }
    }

    public void SetEnabled(Entry entry, bool enabled) => Change(entry, new(enabled, false));
    public void Remove(Entry entry) => Change(entry, new(false, true));

    private void Change(Entry entry, Preference? value)
    {
        var next = new Dictionary<string, Preference>(preferences, StringComparer.OrdinalIgnoreCase);
        if (value is null) next.Remove(entry.Key);
        else next[entry.Key] = value;
        Directory.CreateDirectory(Root);
        var temporary = StatePath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next, Json));
        File.Move(temporary, StatePath, overwrite: true);
        preferences.Clear();
        foreach (var (key, item) in next) preferences[key] = item;
    }

    public async Task StartAsync(Action<string, Func<FrameworkElement>, string?> addTab)
    {
        foreach (var entry in Entries.Where(entry => Enabled(entry) && entry.Compatible))
        {
            if (stopping.IsCancellationRequested) break;
            var package = entry.Package!;
            Context? context = null;
            try
            {
                var file = Path.Combine(entry.Path, package.EntryAssembly);
                var assembly = new PluginLoadContext(file).LoadFromAssemblyPath(file);
                var type = assembly.GetType(package.EntryType, throwOnError: true)!;
                if (!typeof(Plugin).IsAssignableFrom(type) || type.IsAbstract)
                    throw new InvalidDataException("Invalid plugin entry type.");
                var plugin = (Plugin)Activator.CreateInstance(type)!;
                context = new Context(this, package.Id, package.Name, plugin, entry.Path);
                plugin.Register(context);
                context.Seal();
                await plugin.StartAsync(context.Token);
                context.Token.ThrowIfCancellationRequested();
                foreach (var (id, provider) in context.Providers) providers.Add(id, provider);
                foreach (var tab in context.Tabs) addTab(tab.Title, tab.Create, package.TabIcon);
                active.Add(context);
                context.StartChat();
                entry.Running = true;
            }
            catch (Exception ex)
            {
                if (context is not null) await context.StopAsync();
                entry.Problem = ex.GetBaseException().Message;
                Trace.TraceError(ex.ToString());
            }
        }
    }

    public void Publish(ChatEvent message)
    {
        foreach (var context in active) context.Publish(message);
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        foreach (var context in active.AsEnumerable().Reverse())
            await context.StopAsync();
        active.Clear();
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == Sdk.GetName().Name) return Sdk;
            var file = resolver.ResolveAssemblyToPath(name);
            return file is null ? null : LoadFromAssemblyPath(file);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var file = resolver.ResolveUnmanagedDllToPath(name);
            return file is null ? 0 : LoadUnmanagedDllFromPath(file);
        }
    }

    private sealed class Context(PluginManager host, string pluginId, string pluginName, Plugin plugin, string packageDirectory) : IPluginContext
    {
        private readonly LocalizationCatalog localizations = new(Path.Combine(packageDirectory, "Locales"));
        private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.stopping.Token);
        private readonly List<(Func<ChatEvent, CancellationToken, Task> Handler, bool Tests)> handlers = [];
        private Channel<ChatEvent>? chat;
        private bool sealedRegistration;
        public Version AppVersion => PluginManager.AppVersion;
        public string Language => Ui.Language;
        public string Translate(string key, string? fallback = null, string? fallbackLanguage = null) =>
            localizations.Find(Language, key) ?? (fallbackLanguage is null ? null : localizations.Find(fallbackLanguage, key)) ?? fallback ?? key;
        public Window MainWindow => host.window;
        public string DataDirectory => PluginManager.DataDirectory(pluginId);
        public CancellationToken Token => lifetime.Token;
        public Dictionary<string, Shortcut> Shortcuts { get; } = new(StringComparer.Ordinal);
        public List<(string Title, Func<FrameworkElement> Create)> Tabs { get; } = [];
        public Dictionary<string, ApexChatd.Plugins.SpeechProvider> Providers { get; } = new(StringComparer.Ordinal);

        public void RegisterTab(string title, Func<FrameworkElement> createContent)
        {
            EnsureRegistration();
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(createContent);
            Tabs.Add((title, createContent));
        }

        public void RegisterShortcut(string id, string title, Func<CancellationToken, Task> handler)
        {
            EnsureRegistration();
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(handler);
            if (!Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]{0,99}\z"))
                throw new ArgumentException("Invalid shortcut ID.", nameof(id));
            var token = Token;
            var fullId = pluginId + "/" + id;
            Shortcuts.Add(fullId, new(fullId, pluginName, title,
                () => token.IsCancellationRequested ? Task.CompletedTask : handler(token)));
        }

        public void RegisterSpeechProvider(string id, ApexChatd.Plugins.SpeechProvider provider)
        {
            EnsureRegistration();
            ArgumentNullException.ThrowIfNull(provider);
            if (!Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9.-]{0,99}\z"))
                throw new ArgumentException("Invalid provider ID.", nameof(id));
            Providers.Add(pluginId + "/" + id, provider);
        }

        public void SubscribeToChat(Func<ChatEvent, CancellationToken, Task> handler, bool includeTests = false)
        {
            EnsureRegistration();
            ArgumentNullException.ThrowIfNull(handler);
            handlers.Add((handler, includeTests));
        }

        public Task RequestExitAsync() => host.window.Dispatcher.InvokeAsync(host.exit).Task.Unwrap();
        public void Notify(string title, string message) => host.window.Dispatcher.Invoke(() => host.notify(title, message));
        public void ApplyWindowTheme(Window window) => ((App)Application.Current).Themes.ApplyTitleBar(window);
        public void Log(string message) => Trace.WriteLine($"[{pluginId}] {message}");

        public void Seal() => sealedRegistration = true;
        private void EnsureRegistration()
        {
            if (sealedRegistration) throw new InvalidOperationException("Plugin registration has ended.");
        }

        public void StartChat()
        {
            if (handlers.Count == 0) return;
            chat = Channel.CreateBounded<ChatEvent>(new BoundedChannelOptions(256)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
            });
            var token = Token;
            _ = Task.Run(() => ReadChatAsync(token));
        }

        public void Publish(ChatEvent message)
        {
            if (!message.IsTest || handlers.Any(handler => handler.Tests))
                chat?.Writer.TryWrite(message);
        }

        private async Task ReadChatAsync(CancellationToken token)
        {
            try
            {
                await foreach (var message in chat!.Reader.ReadAllAsync(token))
                    foreach (var (handler, tests) in handlers)
                    {
                        token.ThrowIfCancellationRequested();
                        if (message.IsTest && !tests) continue;
                        try { await handler(message, token); }
                        catch (Exception ex) when (!token.IsCancellationRequested) { Log(ex.ToString()); }
                    }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Log(ex.ToString()); }
        }

        public async Task StopAsync()
        {
            try
            {
                lifetime.Cancel();
                chat?.Writer.TryComplete();
                await plugin.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) { Log(ex.ToString()); }
            finally { lifetime.Dispose(); }
        }
    }

    internal static bool TryHttps(string? value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
}
