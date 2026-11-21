#define MyAppName "AgentDeck"
#ifndef MyAppVersion
  #define MyAppVersion "0.6.0"
#endif
#define MyAppPublisher "AgentDeck"
#define MyAppURL "https://github.com/natekk8/agentdeck"
#define MyAppExeName "AgentDeck.exe"

[Setup]
AppId={{8E34D59A-6E12-4C8B-B19A-4D7E21F06A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\AgentDeck
DisableDirPage=no
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=AgentDeck-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
english.InstallingDeps=Installing Python runtime environment and dependencies (pyserial, cryptography)...
polish.InstallingDeps=Instalowanie środowiska Python i wymaganych bibliotek (pyserial, cryptography)...
english.DesktopShortcut=Create a &desktop shortcut
polish.DesktopShortcut=Utwórz skrót na &pulpicie

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\AgentDeck.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\AgentDeck-StickS3.bin"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Start-AgentDeck.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Otworz-AgentDeck.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\host\*.py"; DestDir: "{app}\host"; Excludes: "test_*"; Flags: ignoreversion
Source: "..\host\Window.xaml"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\host\Desktop.cs"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\host\requirements.txt"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "setup-deps.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\installer\setup-deps.ps1"" -InstallDir ""{app}"""; StatusMsg: "{cm:InstallingDeps}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\.venv"
Type: filesandordirs; Name: "{app}\host\__pycache__"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM AgentDeck.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('powershell.exe', '-NoProfile -WindowStyle Hidden -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like ''*agentdeck.py*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
