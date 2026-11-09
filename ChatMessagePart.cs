using System.Text.Json;

namespace TwitchChatAgent;

internal sealed record ChatMessagePart(string Text, string? EmoteId = null)
{
    public static ChatMessagePart[] Read(JsonElement message)
    {
        if (!message.TryGetProperty("fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array)
            return [];

        var parts = new List<ChatMessagePart>();
        foreach (var fragment in fragments.EnumerateArray())
        {
            if (!fragment.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                continue;

            var id = fragment.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("emote") &&
                fragment.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object &&
                emote.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            parts.Add(new(text.GetString()!, IsEmoteId(id) ? id : null));
        }
        return parts.ToArray();
    }

    private static bool IsEmoteId(string? id) => id is { Length: > 0 and <= 80 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

