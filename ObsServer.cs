using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TwitchChatAgent;

internal sealed class ObsServer
{
    public const string Url = "http://127.0.0.1:17891/chat";
    private WebApplication? app;
    private volatile bool liveEnabled;
    private volatile Snapshot snapshot = new(new(), [], new(), null, new());
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

        snapshot = new(theme, messages, new(
            (theme.EntranceProfiles ?? new()).Get(theme.Animation, theme.AnimationDurationMs),
            (theme.ExitProfiles ?? new()).Get(theme.ExitAnimation, 280)),
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
            return liveEnabled ? Results.Content(Page.Replace("__TITLE__", Ui.T("Чат Twitch")).Replace("__LANG__", Ui.Language), "text/html; charset=utf-8") : Results.NotFound();
        });
        server.MapGet("/state", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!liveEnabled)
                return Results.NotFound();
            var current = snapshot;
            return Results.Json(current with
            {
                Messages = ChatAppearance.VisibleMessages(current.Theme, current.Messages, DateTimeOffset.UtcNow)
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
    private sealed record Motion(MotionOptions? Entrance = null, MotionOptions? Exit = null);
    private sealed record Snapshot(ChatTheme Theme, ChatMessage[] Messages, Motion Motion, Background? Background, ObsEffectSettings Effects);
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
          #background, #departures { position: fixed; inset: 0; pointer-events: none; }
          #background { z-index: 0; overflow: hidden; }
          #background > * { width: 100%; height: 100%; object-fit: cover; }
          #chat { position: relative; z-index: 1; overflow: hidden; padding: 12px; }
          #departures { z-index: 2; overflow: hidden; }
          .obs-effects-layer { position: fixed; inset: 0; width: 100%; height: 100%; pointer-events: none; }
          .obs-effects-source-hidden { visibility: hidden; }
          .emote { display: inline-block; width: 1.35em; height: 1.35em; object-fit: contain; vertical-align: -.25em; }
          .message { overflow-wrap: anywhere; white-space: pre-wrap; margin-top: 4px; }
        </style>
        <div id="background"></div>
        <main id="chat"></main>
        <div id="departures" aria-hidden="true"></div>
        <script>
          const chat = document.querySelector('#chat');
          const departures = document.querySelector('#departures');
          const background = document.querySelector('#background');
          const cssColor = value => value.length === 9 ? '#' + value.slice(3) + value.slice(1, 3) : value;
          const twitchColor = /^#[0-9a-f]{6}$/i;
          const easings = ['linear', 'ease-out', 'ease-in', 'ease-in-out'];
          const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
          let previous = '';
          let mediaUrl = null;
          let effectRenderer = null;
          let effectImportFailed = false;
          let newestSeen = null;

          async function updateEffects(effects, newMessage) {
            if (!effects?.enabled) {
              effectRenderer?.dispose();
              effectRenderer = null;
              effectImportFailed = false;
              return;
            }
            if (effectImportFailed) return;
            try {
              if (!effectRenderer) {
                const module = await import('/obs-effects.js');
                effectRenderer = module.createObsEffects(chat, departures, background);
              }
              effectRenderer.update(effects, newMessage);
            } catch (error) {
              effectImportFailed = true;
              console.warn('APEX Chatd: could not load WebGL effects', error);
            }
          }

          function animate(row, kind, options, leaving, startOpacity = 1) {
            const outer = { opacity: clamp(options.opacity, 0, 100) / 100, transform: 'none' };
            const normal = { opacity: startOpacity, transform: 'none' };
            if (kind === 2 || kind === 3)
              outer.transform = 'translate(' + clamp(options.offsetX, -500, 500) + 'px,' + clamp(options.offsetY, -500, 500) + 'px)';
            if (kind === 4) {
              outer.transform = 'scale(' + clamp(options.scale, 10, 200) / 100 + ')';
              row.style.transformOrigin = clamp(options.originX, 0, 100) + '% ' + clamp(options.originY, 0, 100) + '%';
            }
            return row.animate(leaving ? [normal, outer] : [outer, normal], {
              duration: clamp(options.durationMs, 100, 5000),
              delay: clamp(options.delayMs, 0, 2000),
              easing: easings[options.easing] || 'linear', fill: 'both'
            });
          }

          function leave(row, theme, motion, rect) {
            if (!theme.exitAnimation || !motion.exit) {
              row.remove();
              return;
            }
            const style = getComputedStyle(row);
            const opacity = Number(style.opacity);
            const font = style.font;
            const color = style.color;
            for (const current of row.getAnimations()) current.cancel();
            row.style.font = font;
            row.style.color = color;
            Object.assign(row.style, { position: 'absolute', margin: '0', left: rect.left + 'px',
              top: rect.top + 'px', width: rect.width + 'px' });
            if (departures.childElementCount >= 50) departures.firstElementChild.remove();
            departures.append(row);
            animate(row, theme.exitAnimation, motion.exit, true, opacity).finished.then(() => row.remove(), () => row.remove());
          }

          function renderMessages(theme, motion, messages) {
            document.body.classList.toggle('newest-at-top', theme.newestAtTop);
            const active = new Set(messages.map(message => message.displayId));
            const existing = new Map();
            const currentRows = Array.from(chat.children, row => [row, row.getBoundingClientRect()]);
            for (const [row, rect] of currentRows) {
              if (active.has(row.dataset.id)) existing.set(row.dataset.id, row);
              else leave(row, theme, motion, rect);
            }

            let next = chat.firstElementChild;
            const ordered = theme.newestAtTop ? [...messages].reverse() : messages;
            for (const message of ordered) {
              let row = existing.get(message.displayId);
              const isNew = !row;
              if (isNew) {
                Array.from(departures.children).find(old => old.dataset.id === message.displayId)?.remove();
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
              if (isNew && theme.animation && motion.entrance) {
                const entrance = animate(row, theme.animation, motion.entrance, false);
                entrance.finished.then(() => entrance.cancel(), () => {});
              }
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
              const { theme, messages, motion, background: media, effects } = JSON.parse(state);
              const latest = messages.at(-1);
              const newMessage = !!latest && previous !== '' && (!newestSeen ||
                latest.displayTimeMs >= newestSeen.displayTimeMs && latest.displayId !== newestSeen.displayId);
              if (latest && (!newestSeen || latest.displayTimeMs >= newestSeen.displayTimeMs)) newestSeen = latest;
              renderMedia(media);
              const background = parseInt(theme.background.slice(1), 16);
              chat.style.fontFamily = theme.font;
              chat.style.fontSize = theme.size + 'px';
              chat.style.color = cssColor(theme.text);
              chat.style.borderRadius = theme.radius + 'px';
              chat.style.backgroundColor = messages.length
                ? (theme.background.length === 9 ? cssColor(theme.background)
                  : 'rgba(' + [background >> 16, (background >> 8) & 255, background & 255, theme.opacity / 100].join(',') + ')')
                : 'transparent';
              renderMessages(theme, motion, messages);
              chat.scrollTop = theme.newestAtTop ? 0 : chat.scrollHeight;
              previous = state;
              await updateEffects(effects, newMessage);
            } catch {
              chat.replaceChildren();
              departures.replaceChildren();
              renderMedia(null);
              await updateEffects(null, false);
              chat.style.backgroundColor = 'transparent';
              previous = '';
            } finally {
              setTimeout(refresh, 300);
            }
          }
          refresh();
        </script>
        </html>
        """;
}
