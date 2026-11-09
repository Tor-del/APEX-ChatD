# APEX Chatd

[Русская документация](README.ru.md) · [Windows x64 installer](https://github.com/Tor-del/APEX-ChatD/releases/latest)

APEX Chatd is a Windows tray app for your Twitch chat. It can display chat in a desktop overlay, publish a local browser source for OBS, and read messages aloud.

## Install and update

Download the x64 installer from [GitHub Releases](https://github.com/Tor-del/APEX-ChatD/releases). It includes the .NET runtime and offers an option to start minimized when you sign in to Windows. Your preferences and Twitch session are stored separately in `%LOCALAPPDATA%\TwitchChatAgent`.

Automatic update checks are enabled by default. You can also select **Check for updates** on the **Application** tab. For an Inno Setup installation, **Install** downloads and verifies the update, closes APEX Chatd, and launches the new version after installation. **Remind me later** postpones the prompt for 24 hours. **Skip this version** hides automatic reminders for that version. A manual check still shows available updates.

## Connect Twitch

1. On the **Twitch** tab, select **Connect Twitch** and authorize access in your browser with your channel account.
2. The app requests chat and moderation permissions. It stores the Twitch session with Windows DPAPI and restores it on startup.

Use **Refresh Twitch permissions** if you previously connected without moderation access. When permitted, **Delete** removes the message from Twitch and from the app's chat views and speech queue.

## Overlay and OBS

- In **Overlay**, enable the always-on-top desktop window and use **Set overlay position** to move, resize, and pin it. You can limit visibility to times when the channel is live.
- In **OBS**, start the local server and add `http://127.0.0.1:17891/chat` as a Browser Source on the same computer. The browser preview opens the same page with the live chat.
- Configure font, colors, opacity, corner radius, message limit, expiration, and chat direction separately for each view. OBS also supports an image or a looping muted video as its background.
- The OBS source uses three.js and requires WebGL 2. Add entrance, exit, and movement effects to animate the chat, or add chromatic aberration, glitch, and scanlines under **Post-processing**.

## Speech and controls

Choose Windows voices, Edge TTS, or an OpenAI-compatible speech endpoint. Edge TTS can randomize voices across all available languages or within the selected language and keep the same assignment for each Twitch user until the session ends or you reset the assignments.

Speech settings include speed, volume, output device, audio presets, pitch, and formants. URLs are excluded from spoken messages. **Controls** lets you assign global shortcuts for pause or resume, skip, and clear queue. Use **Test message** on the Twitch tab to exercise the chat and speech pipeline without connecting a channel.

## Build from source

Use the .NET 10 SDK on Windows.

```powershell
dotnet run --project TwitchChatAgent.csproj
```

To rebuild the bundled OBS script and create the installer with Inno Setup 6.7:

```powershell
npm ci --prefix WebEffects
npm run build --prefix WebEffects
.\installer\build.ps1 -Compiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

The installer is written to `bin/Setup`. GitHub Actions packages Windows builds on branch pushes and pull requests. Pushing a tag such as `v1.18.0` that matches the project version also publishes the installer to Releases.

## Credits

Created by [Tor-del](https://github.com/Tor-del) with support from OpenAI Codex. The [Vendor](Vendor) directory contains the third-party notices and source for components adapted from LazySkald.
