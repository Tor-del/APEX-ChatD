using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TwitchChatAgent;

internal sealed class LocalizationCatalog
{
    private sealed record Pack(string? Name, Dictionary<string, string>? Strings);
    private readonly Dictionary<string, Pack> packs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LocalizationCatalog(params string[] directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                    Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not read localization directory {directory}: {ex.Message}");
            }
        }
    }

    public IEnumerable<(string Code, string Name)> Languages => packs.Select(item =>
        (item.Key, item.Value.Name ?? CultureInfo.GetCultureInfo(item.Key).NativeName));

    public bool Contains(string language) => packs.ContainsKey(language);

    public string? Find(string language, string key)
    {
        for (var culture = GetCulture(language); culture is not null && culture.Name.Length > 0; culture = culture.Parent)
        {
            if (packs.TryGetValue(culture.Name, out var pack) &&
                pack.Strings?.GetValueOrDefault(key) is { Length: > 0 } translation)
                return translation;
        }
        return null;
    }

    public static CultureInfo? GetCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            return null;
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return null; }
    }

    private void Load(string path)
    {
        var culture = GetCulture(Path.GetFileNameWithoutExtension(path));
        if (culture is null || culture.Name.Length == 0) return;
        try
        {
            using var file = File.OpenRead(path);
            if (JsonSerializer.Deserialize<Pack>(file, Json) is not { Strings: not null } pack)
                throw new JsonException("Expected a strings object.");
            if (packs.TryGetValue(culture.Name, out var existing))
            {
                var merged = new Dictionary<string, string>(existing.Strings!, StringComparer.Ordinal);
                foreach (var (key, value) in pack.Strings)
                    if (!string.IsNullOrEmpty(value)) merged[key] = value;
                pack = new(pack.Name ?? existing.Name, merged);
            }
            packs[culture.Name] = pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Could not load localization {path}: {ex.Message}");
        }
    }
}
