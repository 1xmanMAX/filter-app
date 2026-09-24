; Inno Setup script for FilterApp-Setup.exe. Built by release.cmd, which passes /DMyVersion=x.y.z.
#ifndef MyVersion
  #define MyVersion "0.0.0"
#endif

[Setup]
AppId={{7C1E8A52-4F3B-4D8E-9B1A-2F6C0D9E5A31}
AppName=Filter App
AppVersion={#MyVersion}
AppVerName=Filter App {#MyVersion}
AppPublisher=1xmanMAX
AppPublisherURL=https://github.com/1xmanMAX/filter-app
; Per-user install, no admin prompt; same folder Instalar.cmd uses, so either one upgrades the other.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Filter App
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\FilterApp.exe
UninstallDisplayName=Filter App
SetupIconFile=..\src\FilterApp\app.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app holds this mutex while running: setup asks to close it before replacing the exe.
AppMutex=FilterApp.SingleInstance
OutputDir=..\release
OutputBaseFilename=FilterApp-Setup

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\release\FilterApp\FilterApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Left by the older Instalar.cmd; this setup has its own uninstaller.
Type: files; Name: "{app}\Desinstalar.cmd"

[Registry]
; Entry written by the older Instalar.cmd; without this, Settings > Apps would list Filter App twice.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FilterApp"; Flags: deletekey

[Icons]
Name: "{userprograms}\Filter App"; Filename: "{app}\FilterApp.exe"; Comment: "Renombra y copia archivos a una carpeta"
Name: "{userdesktop}\Filter App"; Filename: "{app}\FilterApp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FilterApp.exe"; Description: "{cm:LaunchProgram,Filter App}"; Flags: nowait postinstall skipifsilent
