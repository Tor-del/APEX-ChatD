# APEX ChatD

[Русская документация](README.ru.md) · [Windows x64 installer](https://github.com/Tor-del/APEX-ChatD/releases/latest)

APEX Chatd is a Windows tray app for your Twitch chat. It can display chat in a desktop overlay, publish a local browser source for OBS, and read messages aloud.

## Install and update

Download either x64 installer from [GitHub Releases](https://github.com/Tor-del/APEX-ChatD/releases). The file ending in `-edge-tts-x64.exe` includes the optional Edge TTS plugin. To run without installing, extract the matching `APEX-Chatd-Portable-*.zip` and open `APEX Chatd.exe`. It includes the .NET runtime and offers an option to start minimized when you sign in to Windows. Your preferences and Twitch session are stored separately in `%LOCALAPPDATA%\TwitchChatAgent`.

The GitHub installer includes the GitHub Updater plugin with automatic checks enabled by default. Select **Check for updates** on the **Updates** tab for a manual check. For an Inno Setup installation, **Install** downloads and verifies the update, closes APEX Chatd, and launches the new version after installation. **Remind me later** postpones the prompt for 24 hours. **Skip this version** hides automatic reminders for that version. A manual check still shows available updates.

After an upgrade from 1.18.0 or later, **Roll back to the previous version** reinstalls the exact previous GitHub release and restarts the app. Your preferences and Twitch session are retained.

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

Add voice profiles using Windows voices, an OpenAI-compatible speech endpoint, or a TTS plugin. The installer with Edge TTS offers it as an optional plugin, selected by default. The [Edge TTS releases](https://github.com/Tor-del/APEX-ChatD-Plugin-EdgeTTS/releases/latest) also provide a separately installable ZIP. Double-click a profile or select **Edit** to open its settings. Select **Make primary** to use that profile by default. Random selection uses the profiles in the list, and assignments can be kept for each Twitch user until the session ends or you reset them.

Set speed, an audio preset, pitch, and formants on each profile. Volume and the output device apply to all profiles. URLs are excluded from spoken messages, and messages starting with `!` are skipped by default. **Controls** lets you assign global shortcuts for pause or resume, skip, and clear queue. Use **Test message** on the Twitch tab to exercise the chat and speech pipeline without connecting a channel.

## Interface language

Russian and English are built in. To add a language, place a UTF-8 JSON file named after its language tag, such as `fr.json`, in `%LOCALAPPDATA%\TwitchChatAgent\Locales` and restart APEX Chatd. Use a `name` field for the language name and a `strings` object for translations. XAML labels use keys from `Labels` in [Ui.cs](Ui.cs). Messages from code use their original Russian text as the key. The [Russian README](README.ru.md#язык-интерфейса) includes an example.

## Plugins

Use **Plugins → Add plugin** to install a ZIP. Enable the plugin and select **Restart app**. Use **Open folder** to view user-installed plugin files. The list shows which releases APEX Chatd has reviewed and explains compatibility errors. Assign shortcuts for active plugin commands under **Controls → Plugin shortcuts**. For plugins that provide a release URL, **Check for update** checks for a newer version and can open its release page. See the [plugin SDK and packaging guide](Plugins/README.md).

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
