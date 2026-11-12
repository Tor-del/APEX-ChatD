# APEX ChatD — карта проекта

APEX Chatd — приложение в трее Windows для чата собственного канала Twitch. Оно показывает сообщения в окне настроек, в оверлее поверх окон и в браузерном источнике OBS, умеет озвучивать их и удалять отдельные сообщения через Twitch. Оверлей, OBS и озвучивание включаются независимо. Пользовательские инструкции доступны в [README.ru.md](README.ru.md) и [README.md](README.md).

## Архитектура и путь сообщения

Приложение собирается из [TwitchChatAgent.csproj](TwitchChatAgent.csproj) на C# и .NET 10 для Windows. Интерфейс использует WPF, значок в трее — Windows Forms, OBS-сервер — ASP.NET Core, вывод звука — NAudio.

1. [App.xaml.cs](App.xaml.cs) ограничивает запуск одним экземпляром, обрабатывает аргументы запуска и создаёт `MainWindow`. [MainWindow.xaml.cs](MainWindow.xaml.cs) управляет жизненным циклом Twitch, оверлея, OBS и озвучивания.
2. [TwitchClient.cs](TwitchClient.cs) авторизует пользователя через OAuth Device Code, читает чат по EventSub WebSocket и передаёт `ChatMessage` в `MainWindow.Publish`. Кнопка «Тестовое сообщение» вызывает тот же `Publish`.
3. `Publish` обновляет историю и список в окне (до 50 сообщений), передаёт снимок в `ObsServer.Update`, обновляет WPF-оверлей и вызывает `SpeechQueue.Add`. Затем `PluginManager.Publish` передаёт подписчикам SDK снимок сообщения с признаком тестового источника. Оба оверлея используют `ChatAppearance.VisibleMessages` для отбора видимых сообщений с учётом собственного оформления и срока показа.
4. Удаление сообщения из интерфейса и события очистки из Twitch сходятся в `MainWindow.RemoveChatMessages`. Этот путь обновляет локальные представления и убирает совпавшие сообщения из озвучивания, включая текущее.

## Куда идти с задачей

| Задача | Точки входа |
| --- | --- |
| Авторизация, переподключение, статус трансляции, модерация | [TwitchClient.cs](TwitchClient.cs) и обработчики в [MainWindow.xaml.cs](MainWindow.xaml.cs). Удаление через Helix, входящие события чата и очистки через EventSub. |
| Текст и эмоуты Twitch | Модель `ChatMessage` в [TwitchClient.cs](TwitchClient.cs), разбор фрагментов в [ChatMessagePart.cs](ChatMessagePart.cs), привязка сообщения к строке WPF в [ChatText.cs](ChatText.cs), подготовка к озвучиванию в [SpeechTextFilter.cs](SpeechTextFilter.cs). |
| Экранный оверлей | [ChatOverlay.cs](ChatOverlay.cs) рисует сообщения. [ChatAppearance.cs](ChatAppearance.cs) содержит общие правила отображения. Положение, режим настройки и видимость по статусу трансляции связаны с [MainWindow.xaml.cs](MainWindow.xaml.cs). |
| Страница OBS, её оформление и фон | [ObsServer.cs](ObsServer.cs) содержит встроенные HTML/CSS/JS и HTTP-маршруты. Браузерный источник на том же ПК открывает `http://127.0.0.1:17891/chat` и опрашивает `/state`. [ObsMedia.cs](ObsMedia.cs) определяет поддерживаемые типы локального фона. |
| Переходы и постобработка OBS | [ObsAnimationEditor.cs](ObsAnimationEditor.cs), [ObsEffectsEditor.cs](ObsEffectsEditor.cs) и [ObsEffectSettings.cs](ObsEffectSettings.cs) задают настройки. Исходники рендера и переходов находятся в [WebEffects/](WebEffects/), вход для сборщика — [effects.js](WebEffects/effects.js). |
| Очередь, фильтры, горячие клавиши и звук | [SpeechQueue.cs](SpeechQueue.cs) фильтрует и ставит сообщения в очередь, выбирает профиль, запускает синтез и управляет воспроизведением. [QueueHotkeys.cs](QueueHotkeys.cs) обрабатывает глобальные сочетания. [MainWindow.PluginShortcuts.cs](MainWindow.PluginShortcuts.cs) добавляет в «Управление» команды запущенных плагинов и регистрирует их вместе со встроенными сочетаниями. [AudioOutputDevices.cs](AudioOutputDevices.cs) перечисляет устройства. Обработка питча и формант начинается в [SpeechVoiceTransform.cs](SpeechVoiceTransform.cs). |
| Профили голосов и провайдеры | [MainWindow.SpeechVoices.cs](MainWindow.SpeechVoices.cs), [SpeechVoiceEditor.xaml.cs](SpeechVoiceEditor.xaml.cs) и [SpeechVoiceProfile.cs](SpeechVoiceProfile.cs). Встроены Windows Speech и OpenAI-совместимый API. `SpeechQueue` разрешает расширения по ID провайдера и исключает из очереди профили с отсутствующим плагином. Старые профили Edge используют ID `apexchatd.edge-tts/edge`. |
| Окно настроек, темы, язык, трей и автозапуск | [MainWindow.xaml](MainWindow.xaml), [MainWindow.xaml.cs](MainWindow.xaml.cs), [Ui.cs](Ui.cs) для русского и английского интерфейса, [ThemeManager.cs](ThemeManager.cs) и [Themes/](Themes/) для темы приложения, [StartupRegistration.cs](StartupRegistration.cs) для автозапуска Windows. |
| Плагины | [PluginManager.cs](PluginManager.cs) читает и проверяет пакет, загружает отдельную сборку и управляет жизненным циклом. [MainWindow.Plugins.cs](MainWindow.Plugins.cs) добавляет вкладки, управляет установкой, перезапуском приложения, открытием папки пользовательских плагинов и ручной проверкой версии. Контракты и сборка пакетов описаны в [Plugins/README.md](Plugins/README.md). |
| Установщик, обновления и откат | [GitHub Updater](Plugins/GitHubUpdater/UpdaterPlugin.cs) добавляет отдельную вкладку «Обновления». Его [клиент](Plugins/GitHubUpdater/GitHubUpdater.cs) выбирает релиз и проверяет размер, SHA-256 и версию установщика. [installer/APEXChatd.iss](installer/APEXChatd.iss) обслуживает установку, перезапуск и отметку предыдущей версии. |

## Данные и контракты

- [Settings.cs](Settings.cs) читает `%LOCALAPPDATA%\TwitchChatAgent\settings.json` и сохраняет его через временный файл. Там же мигрируют старые поля фильтра префиксов в `SpeechFilter`. При изменении настроек учитывай JSON предыдущих версий, в том числе отсутствие новых полей.
- `DesktopTheme` и `ObsTheme` используют общую модель `ChatTheme`, но хранятся раздельно. `AppTheme` управляет темой окна настроек.
- В `%LOCALAPPDATA%\TwitchChatAgent\twitch.dat` лежит Twitch-сессия. Она и ключи голосовых профилей защищены Windows DPAPI с областью `CurrentUser`. Эти данные привязаны к профилю Windows. Для примеров и диагностики используй обезличенные настройки.
- `ChatMessage` одновременно используется локальными потребителями и сериализуется для OBS. Служебные идентификаторы Twitch и признак модератора исключены из JSON через `JsonIgnore`. При изменении модели сверяй браузерный код в `ObsServer.cs` с формой `/state`.
- OBS-сервер слушает `127.0.0.1:17891`. Он отдаёт выбранный фоновый файл по `/background/{id}`. Исходники WebGL-эффектов собираются в [Assets/obs-effects.js](Assets/obs-effects.js), который встраивается в сборку .NET. После изменения `WebEffects/` пересобери и включи в изменение этот пакет.

- Пакеты, установленные пользователем, лежат в `%LOCALAPPDATA%\TwitchChatAgent\Plugins`. Состояние сохраняется в `plugins.json`, данные плагинов — в `PluginData\<id>`. Назначения горячих клавиш хранятся в `settings.json` в `PluginHotkeys` по ключу `<id плагина>/<id команды>`. Менеджер подхватывает комплектные пакеты из `BundledPlugins` рядом с EXE. Комплектные плагины можно выключать. Старый флаг удаления для комплектного плагина оставляет его видимым и выключенным. Настройки старого обновлятора переносятся при первом запуске GitHub Updater.

- [LocalizationCatalog.cs](LocalizationCatalog.cs) читает JSON-словари по коду языка. [Ui.cs](Ui.cs) загружает словари приложения из `Locales` рядом с EXE и из `%LOCALAPPDATA%\TwitchChatAgent\Locales`. Язык выбирается в шапке или в настройках и применяется при перезапуске. SDK 1.2 читает `Locales` из папки конкретного плагина для его интерфейса.

## Сборка и проверка

Из корня проекта в PowerShell с .NET 10 SDK на Windows:

```powershell
dotnet build TwitchChatAgent.csproj
dotnet run --project TwitchChatAgent.csproj
```

Пересборка пакета OBS с Node.js и npm:

```powershell
npm ci --prefix WebEffects
npm run build --prefix WebEffects
```

Сборка установщика с Inno Setup 6:

```powershell
.\installer\build.ps1 -Compiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

Скрипт публикует self-contained `win-x64` во временный каталог сборки в `bin/PluginPackages` и записывает установщик и portable ZIP в `bin/Setup`. Без параметров он собирает вариант без Edge TTS с прежним именем `-x64.exe`. Для варианта с Edge TTS передай подписанный ZIP через `-EdgePluginZip`. [Import-EdgePlugin.ps1](installer/Import-EdgePlugin.ps1) проверяет подпись, хеши файлов и совместимость с API. Имя установщика с Edge TTS заканчивается на `-edge-tts-x64.exe`. Установка плагина выбирается флажком, включённым по умолчанию при первой установке.

[CI](.github/workflows/ci.yml) собирает оба варианта. Сборка с Edge TTS скачивает ZIP последнего стабильного релиза из [репозитория плагина](https://github.com/Tor-del/APEX-ChatD-Plugin-EdgeTTS/releases/latest). [Публикация](.github/workflows/release.yml) запускается тегом `v<версия>` из `TwitchChatAgent.csproj` и прикрепляет два установщика и два соответствующих portable ZIP к одному релизу. В релизной сборке секрет `APEXCHATD_PLUGIN_SIGNING_KEY` подписывает GitHub Updater. Edge TTS подписывает его собственный workflow. Оба пакета проверяются по [открытому ключу приложения](Assets/plugin-approval-public.pem). Inno Setup и portable ZIP записывают вариант в `installer-variant.txt`, по которому GitHub Updater выбирает установщик при следующем обновлении.

Для быстрой ручной проверки включи нужные модули и нажми «Тестовое сообщение». Проверь список, экранный оверлей, страницу OBS и очередь озвучивания по затронутому сценарию. Для визуальной проверки постэффектов нужен браузерный источник с WebGL 2. Авторизацию, события Twitch и модерацию проверяй с подключённым каналом.
