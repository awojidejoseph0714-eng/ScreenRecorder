[Setup]
AppName=Screen Recorder
AppVersion=2.1
AppPublisher=AduraX
AppPublisherURL=https://github.com/awojidejoseph0714-eng/ScreenRecorder
DefaultDirName={localappdata}\Programs\ScreenRecorder
DefaultGroupName=Screen Recorder
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ScreenRecorder.exe
OutputDir=installer
OutputBaseFilename=ScreenRecorder_Setup

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Options:"
Name: "autostart"; Description: "Start with Windows"; GroupDescription: "Options:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Screen Recorder"; Filename: "{app}\ScreenRecorder.exe"
Name: "{group}\Uninstall Screen Recorder"; Filename: "{uninstallexe}"
Name: "{commondesktop}\Screen Recorder"; Filename: "{app}\ScreenRecorder.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ScreenRecorder"; \
    ValueData: """{app}\ScreenRecorder.exe"" --background"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ScreenRecorder.exe"; Description: "Launch Screen Recorder"; \
    Flags: nowait postinstall skipifsilent
