; SuperScreenshot installer for Windows (Inno Setup 6).
;
; Built by publish.ps1 -Installer and by CI:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<folder with SuperScreenshot.exe> /DOutputDir=<out> packaging\windows\superscreenshot.iss
;
; Installs for the current user by default (no admin prompt) into %LOCALAPPDATA%\Programs\SuperScreenshot,
; or for all users into Program Files when chosen on the first page or with /ALLUSERS.
; Adds a Start menu shortcut and an Installed apps entry. The app itself adds the
; start-with-Windows entry on first run; the uninstaller closes the app and removes that entry.
; Silent install: SuperScreenshot-Setup-<ver>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist\installer"
#endif

[Setup]
; Never change this GUID: it's how upgrades find the existing install.
AppId={{237197A2-6051-4E27-A935-58CCFA768B76}
AppName=SuperScreenshot
AppVersion={#AppVersion}
AppVerName=SuperScreenshot {#AppVersion}
AppPublisher=Glen Rhodes
AppPublisherURL=https://github.com/glenwrhodes/SuperScreenshot
AppSupportURL=https://github.com/glenwrhodes/SuperScreenshot/issues
AppUpdatesURL=https://github.com/glenwrhodes/SuperScreenshot/releases
AppCopyright=Copyright (c) 2026 Glen Rhodes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=SuperScreenshot
VersionInfoDescription=SuperScreenshot Setup
DefaultDirName={autopf}\SuperScreenshot
DefaultGroupName=SuperScreenshot
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004: the first that can hide the selection overlay from screen capture.
MinVersion=10.0.19041
CloseApplications=yes
SetupIconFile=..\..\src\SuperScreenshot\app.ico
UninstallDisplayIcon={app}\SuperScreenshot.exe
UninstallDisplayName=SuperScreenshot
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=SuperScreenshot-Setup-{#AppVersion}-x64

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\SuperScreenshot.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SuperScreenshot"; Filename: "{app}\SuperScreenshot.exe"; Comment: "Snipping Tool with vertical scrolling (Win+Shift+X)"
Name: "{autodesktop}\SuperScreenshot"; Filename: "{app}\SuperScreenshot.exe"; Comment: "Snipping Tool with vertical scrolling (Win+Shift+X)"; Tasks: desktopicon

[Registry]
; Created by the app at runtime; removed on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SuperScreenshot"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SuperScreenshot"; Flags: uninsdeletekey

[Run]
Filename: "{app}\SuperScreenshot.exe"; Description: "Start SuperScreenshot now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\SuperScreenshot.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitApp"
