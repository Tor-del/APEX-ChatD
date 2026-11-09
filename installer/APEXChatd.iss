#define AppName "APEX Chatd"
#define AppExe "APEX Chatd.exe"
#define Payload SourcePath + "..\bin\InstallerPayload"
#define AppVersion GetVersionNumbersString(Payload + "\" + AppExe)

[Setup]
AppId=APEXChatd.Desktop
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\APEX Chatd
DefaultGroupName=APEX Chatd
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
AppMutex=Local\TwitchChatAgent
WizardStyle=modern
SetupIconFile=..\Assets\APEXChatd.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\bin\Setup
OutputBaseFilename=APEX-Chatd-Setup-{#AppVersion}-x64
Compression=lzma2/fast
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.AutoStart=Start APEX Chatd minimized when I sign in to Windows
ru.AutoStart=Запускать APEX Chatd свёрнутым при входе в Windows
en.Launch=Launch APEX Chatd
ru.Launch=Запустить APEX Chatd

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\Vendor\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\source"; Flags: ignoreversion
Source: "..\Vendor\Directory.Build.props"; DestDir: "{app}\source"; Flags: ignoreversion
Source: "..\Vendor\licenses\*"; DestDir: "{app}\source\licenses"; Flags: ignoreversion
Source: "..\Vendor\ScreenReader.Edge\*.cs"; DestDir: "{app}\source\ScreenReader.Edge"; Flags: ignoreversion
Source: "..\Vendor\ScreenReader.Edge\*.csproj"; DestDir: "{app}\source\ScreenReader.Edge"; Flags: ignoreversion
Source: "..\Vendor\ScreenReader.Speech\*.cs"; DestDir: "{app}\source\ScreenReader.Speech"; Flags: ignoreversion
Source: "..\Vendor\ScreenReader.Speech\*.csproj"; DestDir: "{app}\source\ScreenReader.Speech"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\APEX Chatd"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "APEX Chatd"; ValueData: """{app}\{#AppExe}"" --autostart"; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "APEX Chatd"; Flags: deletevalue; Tasks: not autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "TwitchChatAgent"; Flags: deletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'APEX Chatd';

var
  StartupTaskInitialized: Boolean;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID <> wpSelectTasks) or StartupTaskInitialized or WizardSilent then
    Exit;
  StartupTaskInitialized := True;
  if RegValueExists(HKCU, RunKey, RunValue) or RegValueExists(HKCU, RunKey, 'TwitchChatAgent') then
    WizardSelectTasks('autostart')
  else
    WizardSelectTasks('!autostart');
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Value, InstalledCommand: String;
begin
  if Step <> usUninstall then
    Exit;
  InstalledCommand := '"' + ExpandConstant('{app}\{#AppExe}') + '" --autostart';
  if RegQueryStringValue(HKCU, RunKey, RunValue, Value) and (CompareText(Value, InstalledCommand) = 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
end;
