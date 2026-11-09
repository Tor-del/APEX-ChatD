using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TwitchChatAgent;

internal sealed class ObsServer
{
    public const string Url = "http://127.0.0.1:17891/chat";
    private static readonly string RendererVersion = typeof(ObsServer).Assembly.GetName().Version!.ToString();
    private WebApplication? app;
    private volatile bool liveEnabled;
    private volatile Snapshot snapshot = new(new(), [], null, new());
    private volatile MediaFile? media;

    public void SetLiveEnabled(bool enabled) => liveEnabled = enabled;

    public void Update(ChatTheme theme, ChatMessage[] messages, string? path, ObsEffectSettings effects)
    {
        if (media?.Path != path)
        {
            var contentType = ObsMedia.ContentType(path);
            media = path is not null && Path.IsPathFullyQualified(path) && contentType is not null
                ? new(path, contentType, Guid.NewGuid()) : null;
        }

        snapshot = new(theme, messages,
            media is { } file ? new($"/background/{file.Id:N}", file.ContentType.StartsWith("video/", StringComparison.Ordinal)) : null, effects);
    }
    public async Task StartAsync()
    {
        if (app is not null)
            return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:17891");
        var server = builder.Build();
        server.Use(async (context, next) =>
        {
            if (context.Request.Host.Host != "127.0.0.1")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(context);
        });
        server.MapGet("/chat", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return liveEnabled ? Results.Content(Page.Replace("__TITLE__", Ui.T("Чат Twitch")).Replace("__LANG__", Ui.Language)
                .Replace("__RENDERER_VERSION__", RendererVersion), "text/html; charset=utf-8") : Results.NotFound();
        });
        server.MapGet("/state", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!liveEnabled)
                return Results.NotFound();
            var current = snapshot;
            return Results.Json(new
            {
                current.Theme,
                Messages = ChatAppearance.VisibleMessages(current.Theme, current.Messages, DateTimeOffset.UtcNow),
                current.Background,
                current.Effects,
                RendererVersion
            });
        });

        server.MapGet("/obs-effects.js", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return liveEnabled
                ? Results.Stream(typeof(ObsServer).Assembly.GetManifestResourceStream("obs-effects.js")!, "text/javascript; charset=utf-8")
                : Results.NotFound();
        });
        server.MapGet("/background/{id:guid}", (Guid id) =>
        {
            var file = media;
            if (!liveEnabled || file is null || file.Id != id)
                return Results.NotFound();
            try
            {
                var stream = File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Results.File(stream, file.ContentType, enableRangeProcessing: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.NotFound();
            }
        });

        try
        {
            await server.StartAsync();
            app = server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (app is null)
            return;
        await app.DisposeAsync();
        app = null;
    }

    private sealed record MediaFile(string Path, string ContentType, Guid Id);
    private sealed record Background(string Url, bool Video);
    private sealed record Snapshot(ChatTheme Theme, ChatMessage[] Messages, Background? Background, ObsEffectSettings Effects);
    private const string Page = """
        <!doctype html>
        <html lang="__LANG__">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>__TITLE__</title>
        <style>
          html, body { height: 100%; margin: 0; overflow: hidden; }
          body { display: flex; flex-direction: column; justify-content: flex-end; }
          body.newest-at-top { justify-content: flex-start; }
          #chat { overflow: clip; padding: 12px;
                  display: flex; flex-direction: column; justify-content: flex-end; }
          body.newest-at-top #chat { justify-content: flex-start; }
          .obs-effects-layer { position: fixed; inset: 0; width: 100%; height: 100%; pointer-events: none; }
          #background { position: fixed; inset: 0; visibility: hidden; }
          #chat { visibility: hidden; }
          .emote { display: inline-block; width: 1.35em; height: 1.35em; object-fit: contain; vertical-align: -.25em; }
          .message { flex: none; overflow-wrap: anywhere; white-space: pre-wrap; margin-top: 4px; }
        </style>
        <div id="background"></div>
        <main id="chat"></main>
        <script>
          const chat = document.querySelector('#chat');
          const background = document.querySelector('#background');
          const cssColor = value => value.length === 9 ? '#' + value.slice(3) + value.slice(1, 3) : value;
          const twitchColor = /^#[0-9a-f]{6}$/i;
          const rendererVersion = '__RENDERER_VERSION__';
          let previous = '';
          let mediaUrl = null;
          let effectRenderer = null;
          let effectImportFailed = false;
          let newestSeen = null;

          async function updateEffects(effects, newMessage) {
            if (effectImportFailed) return;
            try {
              if (!effectRenderer) {
                const module = await import('/obs-effects.js?v=' + rendererVersion);
                effectRenderer = module.createObsEffects(chat, background);
              }
              effectRenderer.update(effects, newMessage);
            } catch (error) {
              effectImportFailed = true;
              console.warn('APEX Chatd: could not load WebGL effects', error);
            }
          }

          function renderMessages(theme, messages) {
            document.body.classList.toggle('newest-at-top', theme.newestAtTop);
            const active = new Set(messages.map(message => message.displayId));
            const existing = new Map();
            for (const row of Array.from(chat.children)) {
              if (active.has(row.dataset.id)) existing.set(row.dataset.id, row);
              else row.remove();
            }

            let next = chat.firstElementChild;
            const ordered = theme.newestAtTop ? [...messages].reverse() : messages;
            for (const message of ordered) {
              let row = existing.get(message.displayId);
              if (!row) {
                row = document.createElement('div');
                row.dataset.id = message.displayId;
                row.className = 'message';
                const name = document.createElement('strong');
                name.textContent = message.name + ': ';
                row.append(name);
                for (const part of message.parts?.length ? message.parts : [{ text: message.text }]) {
                  if (part.emoteId && /^[a-z0-9_-]{1,80}$/i.test(part.emoteId)) {
                    const image = document.createElement('img');
                    image.className = 'emote';
                    image.alt = image.title = part.text;
                    image.crossOrigin = 'anonymous';
                    image.addEventListener('error', () => {
                      image.replaceWith(document.createTextNode(part.text));
                      row.dispatchEvent(new Event('obs-content-changed', { bubbles: true }));
                    }, { once: true });
                    image.src = 'https://static-cdn.jtvnw.net/emoticons/v2/' + part.emoteId + '/static/dark/2.0';
                    row.append(image);
                  } else row.append(document.createTextNode(part.text));
                }
              }

              row.firstElementChild.style.color = theme.useTwitchNameColor && twitchColor.test(message.nameColor)
                ? message.nameColor : cssColor(theme.name);
              if (row !== next) chat.insertBefore(row, next);
              next = row.nextElementSibling;
            }
          }

          function renderMedia(media) {
            if (mediaUrl === (media?.url ?? null)) return;
            mediaUrl = media?.url ?? null;
            background.replaceChildren();
            if (!media) return;
            const element = document.createElement(media.video ? 'video' : 'img');
            if (media.video) {
              element.muted = element.defaultMuted = true;
              element.loop = element.autoplay = element.playsInline = true;
            } else element.alt = '';
            element.src = media.url;
            background.append(element);
          }
          async function refresh() {
            try {
              const response = await fetch('/state', { cache: 'no-store' });
              if (!response.ok) throw new Error('Chat unavailable');
              const state = await response.text();
              if (state === previous) return;
              const { theme, messages, background: media, effects, rendererVersion: serverVersion } = JSON.parse(state);
              if (serverVersion && serverVersion !== rendererVersion) {
                location.reload();
                return;
              }
              const latest = messages.at(-1);
              const newMessage = !!latest && previous !== '' && (!newestSeen ||
                latest.displayTimeMs >= newestSeen.displayTimeMs && latest.displayId !== newestSeen.displayId);
              if (latest && (!newestSeen || latest.displayTimeMs >= newestSeen.displayTimeMs)) newestSeen = latest;
              renderMedia(media);
              const backgroundRgb = parseInt(theme.background.slice(1), 16);
              const updateChat = () => {
                chat.style.fontFamily = theme.font;
                chat.style.fontSize = theme.size + 'px';
                chat.style.color = cssColor(theme.text);
                chat.style.borderRadius = theme.radius + 'px';
                renderMessages(theme, messages);
              };
              background.style.backgroundColor = theme.background.length === 9 ? cssColor(theme.background)
                : 'rgba(' + [backgroundRgb >> 16, (backgroundRgb >> 8) & 255, backgroundRgb & 255, theme.opacity / 100].join(',') + ')';
              if (effectRenderer) effectRenderer.updateChat(updateChat, theme);
              else updateChat();
              previous = state;
              await updateEffects(effects, newMessage);
            } catch {
              // Keep the last rendered chat until a complete snapshot is available again.
            } finally {
              setTimeout(refresh, 300);
            }
          }
          refresh();
        </script>
        </html>
        """;
}
