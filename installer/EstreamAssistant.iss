; eStream Assistant installer (Inno Setup 6).
; Build:  iscc /DAppVersion=0.2.2 /DPublishDir=..\publish\win-x64 installer\EstreamAssistant.iss
; Output: dist\eStreamAssistant-Setup-<version>.exe
;
; Installs for the current Windows user, no admin needed (spec: C:\eStreamAssistantApp\).
; Settings, the encrypted API key, caches and logs live in %LOCALAPPDATA%\eStreamAssistant
; and are KEPT on uninstall and upgrade.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{6F3C2B1E-8E4A-4C61-9B7D-3E5A2F9C1D40}
AppName=eStream Assistant
AppVersion={#AppVersion}
AppVerName=eStream Assistant {#AppVersion}
AppPublisher=Pocket Studios LLC
AppPublisherURL=https://github.com/PocketStudiosLLC/Estream-Assistant
AppSupportURL=https://github.com/PocketStudiosLLC/Estream-Assistant/issues
AppUpdatesURL=https://github.com/PocketStudiosLLC/Estream-Assistant/releases
VersionInfoVersion={#AppVersion}
DefaultDirName=C:\eStreamAssistantApp
DisableProgramGroupPage=yes
DefaultGroupName=eStream Assistant
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=eStreamAssistant-Setup-{#AppVersion}
SetupIconFile=..\src\EstreamAssistant.App\app.ico
UninstallDisplayIcon={app}\EstreamAssistant.exe
UninstallDisplayName=eStream Assistant
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The app holds this mutex while running; setup asks the owner to close it first.
AppMutex=Local\eStreamAssistant.SingleInstance
CloseApplications=yes
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#PublishDir}\EstreamAssistant.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\eStream Assistant"; Filename: "{app}\EstreamAssistant.exe"
Name: "{group}\Uninstall eStream Assistant"; Filename: "{uninstallexe}"
Name: "{userdesktop}\eStream Assistant"; Filename: "{app}\EstreamAssistant.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EstreamAssistant.exe"; Description: "Open eStream Assistant now"; Flags: nowait postinstall skipifsilent
