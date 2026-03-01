#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\\publish"
#endif

#define MyAppName "AutoLogin"
#define MyAppExeName "AutoLogin.exe"
#define MyAppPublisher "AutoLogin"
#define MyAppURL "https://github.com"

[Setup]
AppId={{D6D7AD3A-2A2E-4B30-9E6D-1C17A4F90A95}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=no
DisableDirPage=no
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#SourcePath}..\dist
OutputBaseFilename=AutoLogin-Setup-{#MyAppVersion}
SetupIconFile={#SourcePath}..\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start AutoLogin when you sign in"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AutoLogin"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent
