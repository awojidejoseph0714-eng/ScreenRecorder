[Setup]
AppName=Screen Recorder
AppVersion=2.1.0
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
AppMutex=ScreenRecorderV2-SingleInstanceMutex
UninstallDisplayIcon={app}\ScreenRecorder.exe
OutputDir=installer
OutputBaseFilename=ScreenRecorder-v2.1.0-Setup

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Options:"
Name: "autostart"; Description: "Start with Windows"; GroupDescription: "Options:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Screen Recorder"; Filename: "{app}\ScreenRecorder.exe"
Name: "{group}\Uninstall Screen Recorder"; Filename: "{uninstallexe}"
Name: "{userdesktop}\Screen Recorder"; Filename: "{app}\ScreenRecorder.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ScreenRecorder"; \
    ValueData: """{app}\ScreenRecorder.exe"" --background"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ScreenRecorder.exe"; Description: "Launch Screen Recorder"; \
    Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  Retries: Integer;
begin
  Result := True;
  
  // Try to kill the process using taskkill (with absolute path)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ScreenRecorder.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  
  // Check if it's still running and wait/retry a few times
  Retries := 0;
  while CheckForMutexes('ScreenRecorderV2-SingleInstanceMutex') and (Retries < 10) do
  begin
    Sleep(200);
    Retries := Retries + 1;
  end;
  
  // If it's still running, prompt the user
  if CheckForMutexes('ScreenRecorderV2-SingleInstanceMutex') then
  begin
    MsgBox('Screen Recorder is still running. Please close it before continuing with the installation.', mbCriticalError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
var
  ErrorCode: Integer;
  Retries: Integer;
begin
  Result := True;
  
  // Try to kill the process using taskkill (with absolute path)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im ScreenRecorder.exe', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  
  // Check if it's still running and wait/retry a few times
  Retries := 0;
  while CheckForMutexes('ScreenRecorderV2-SingleInstanceMutex') and (Retries < 10) do
  begin
    Sleep(200);
    Retries := Retries + 1;
  end;
  
  // If it's still running, prompt the user
  if CheckForMutexes('ScreenRecorderV2-SingleInstanceMutex') then
  begin
    MsgBox('Screen Recorder is still running. Please close it from the system tray before uninstalling.', mbCriticalError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Do you want to delete all settings, search databases, and rolling recording buffers?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\ScreenRecorderV2'), True, True, True);
    end;
  end;
end;
