#define AppVer "1.3.1"
#define ShortcutName "Dimsum Manager - 點心粵語點字輸入法管理器"
#define ManagerName "點心粵語點字輸入法管理器"

[Setup]
AppId=DimsumGroup.DimsumInput.Manager
AppName={#ManagerName}
AppVersion={#AppVer}
AppPublisher=Dimsum Group
AppPublisherURL=http://dimsumbrl.work/
AppSupportURL=http://dimsumbrl.work/
DefaultDirName={localappdata}\Programs\DimsumInput
DefaultGroupName={#ManagerName}
OutputDir=..\dist
OutputBaseFilename=Dimsum_Setup_v{#AppVer}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "zh"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Messages]
zh.FinishedLabel=安裝完成。按 Windows 鍵加空格，喺繁體中文（香港）揀「點心粵語點字輸入法」。
zh.FinishedLabelNoIcons=安裝完成。按 Windows 鍵加空格，喺繁體中文（香港）揀「點心粵語點字輸入法」。

zh.ConfirmUninstall=要移除點心粵語點字輸入法嗎？

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; Flags: unchecked

[Files]
Source: "..\dist\manager\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#ManagerName}"; Filename: "{app}\DimsumManager.exe"
Name: "{autodesktop}\{#ShortcutName}"; Filename: "{app}\DimsumManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DimsumManager.exe"; Description: "開啟 Dimsum Manager（調整輸入同報讀進階設定、更新詞庫等）"; Parameters: "{code:AfterSetupManagerArguments}"; Flags: postinstall unchecked skipifsilent nowait runasoriginaluser; Check: OfferManagerAfterSetup

[InstallDelete]
Type: files; Name: "{autodesktop}\{#ManagerName}.lnk"
Type: files; Name: "{app}\README.md"
Type: files; Name: "{autoprograms}\點心粵語點字管理器.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "DimsumManager"; Flags: deletevalue

[UninstallDelete]
Type: files; Name: "{localappdata}\DimsumInput\candidate-reporting-enabled"
Type: files; Name: "{localappdata}\DimsumInput\candidate-report-1"
Type: files; Name: "{localappdata}\DimsumInput\candidate-report-2"

Type: files; Name: "{localappdata}\DimsumInput\candidate-report-1.tmp"
Type: files; Name: "{localappdata}\DimsumInput\candidate-report-2.tmp"
Type: files; Name: "{localappdata}\DimsumInput\singleton-sound-enabled"
Type: files; Name: "{localappdata}\DimsumInput\singleton-sound-service"
Type: files; Name: "{localappdata}\DimsumInput\singleton-sound-event"

[Code]
const
  WM_CLOSE = $0010;
  PROCESS_QUERY_LIMITED_INFORMATION = $1000;
  GENERIC_WRITE = $40000000;
  FILE_SHARE_ALL = 7;
  OPEN_EXISTING = 3;

function FindWindowEx(Parent, Previous: HWND; ClassName, WindowName: LongWord): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function IsWindowVisible(Window: HWND): Boolean;
  external 'IsWindowVisible@user32.dll stdcall';
function GetWindowThreadProcessId(Window: HWND; var ProcessID: LongWord): LongWord;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function PostMessage(Window: HWND; Msg, WParam, LParam: LongWord): Boolean;
  external 'PostMessageW@user32.dll stdcall';
function OpenProcess(Access: LongWord; Inherit: Boolean; ProcessID: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: LongWord; Path: String; var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function CreateFile(Path: String; Access, Share, Security, Creation, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  SoundWasRunning, SetupFinished, ManagerRequired: Boolean;
  NvdaOfferShown, InstallNvdaAfterSetup: Boolean;

function SoundServiceFile: String;
begin
  Result := ExpandConstant('{localappdata}\DimsumInput\singleton-sound-service');
end;

procedure CloseManagerWindows(const Manager: String);
var
  Window: HWND;
  ProcessID, Size: LongWord;
  Process: THandle;
  Path: String;
begin
  Window := FindWindowEx(0, 0, 0, 0);
  while Window <> 0 do begin
    if IsWindowVisible(Window) then begin
      ProcessID := 0;
      GetWindowThreadProcessId(Window, ProcessID);
      Process := OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, ProcessID);
      if Process <> 0 then begin
        Size := 32768;
        SetLength(Path, Size);
        if QueryFullProcessImageName(Process, 0, Path, Size) and
           (CompareText(Copy(Path, 1, Size), Manager) = 0) then
          PostMessage(Window, WM_CLOSE, 0, 0);
        CloseHandle(Process);
      end;
    end;
    Window := FindWindowEx(0, Window, 0, 0);
  end;
end;

function ManagerCanBeReplaced(const Manager: String): Boolean;
var
  Handle: THandle;
begin
  if not FileExists(Manager) then begin
    Result := True;
    Exit;
  end;
  Handle := CreateFile(Manager, GENERIC_WRITE, FILE_SHARE_ALL, 0, OPEN_EXISTING, 0, 0);
  Result := Handle <> THandle(-1);
  if Result then CloseHandle(Handle);
end;

procedure RestoreSoundService;
begin
  if SoundWasRunning then begin
    SaveStringToFile(SoundServiceFile, '', False);
    SoundWasRunning := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Manager: String;
  Attempt: Integer;
begin
  Result := '';
  Manager := ExpandConstant('{app}\DimsumManager.exe');
  if not FileExists(Manager) then Exit;
  SoundWasRunning := SoundWasRunning or FileExists(SoundServiceFile);
  for Attempt := 0 to 59 do begin
    if (Attempt mod 4) = 0 then begin
      CloseManagerWindows(Manager);
      if FileExists(SoundServiceFile) then DeleteFile(SoundServiceFile);
    end;
    if ManagerCanBeReplaced(Manager) then Exit;
    Sleep(250);
  end;
  Result := '點心管理器仍在使用中。請等目前操作完成，返回上一步，再按「安裝」重試。';
end;

procedure DeinitializeSetup;
var
  Code: Integer;
  Manager: String;
begin
  if SoundWasRunning and not SetupFinished then begin
    RestoreSoundService;
    Manager := ExpandConstant('{app}\DimsumManager.exe');
    if FileExists(Manager) then Exec(Manager, '--sound', '', SW_HIDE, ewNoWait, Code);
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var
  Code: Integer;
  Manager: String;
begin
  Manager := ExpandConstant('{app}\DimsumManager.exe');
  if Step = ssPostInstall then begin
    RestoreSoundService;
    SetupFinished := True;
    WizardForm.StatusLabel.Caption := '正在設定點心輸入法。';
    ManagerRequired := True;
    if Exec(Manager, '--legacy-notice', '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 6)) then begin
      if Code = 6 then
        SuppressibleMsgBox('偵測到舊版點心。按「確定」後會安裝新版，並保留你嘅學習記錄同自訂詞庫。',
          mbInformation, MB_OK, IDOK);
      if Exec(Manager, '--complete-install', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
        ManagerRequired := False;
    end;
    if not ManagerRequired and not WizardSilent then begin
      if Exec(Manager, '--check-nvda-addon', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 6) then begin
        NvdaOfferShown := True;
        InstallNvdaAfterSetup := MsgBox('偵測到 NVDA 正在執行。要安裝點心 NVDA 附加組件，報讀候選同查配詞嗎？按「是」後，完成點心安裝時請跟隨 NVDA 指示。',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
      end;
    end;
  end;
  if (Step = ssDone) and not WizardSilent then begin
    if ManagerRequired then
      Exec(Manager, '--setup-problem', '', SW_SHOWNORMAL, ewNoWait, Code)
    else if InstallNvdaAfterSetup then
      Exec(Manager, '--install-nvda-addon', '', SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

function AfterSetupManagerArguments(Param: String): String;
begin
  Result := '';
  if NvdaOfferShown then Result := '--skip-nvda-prompt';
end;

function OfferManagerAfterSetup: Boolean;
begin
  Result := not ManagerRequired;
end;

procedure CurPageChanged(PageID: Integer);
begin
  if (PageID = wpFinished) and ManagerRequired then begin
    WizardForm.FinishedHeadingLabel.Caption := '點心設定尚未完成';
    WizardForm.FinishedLabel.Caption := '按「完成」開啟管理器，查看原因同繼續設定。';
  end;
end;

var EngineRestart, NvdaRestart: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    if not Exec(ExpandConstant('{app}\DimsumManager.exe'), '--remove-components', '',
      SW_HIDE, ewWaitUntilTerminated, Code) then begin
      MsgBox('未能啟動點心移除程序。', mbError, MB_OK);
      Abort;
    end;
    EngineRestart := (Code = 3) or (Code = 5);
    NvdaRestart := (Code = 4) or (Code = 5);
    if (Code <> 0) and not EngineRestart and not NvdaRestart then Abort;
  end;
  if (CurUninstallStep = usPostUninstall) and EngineRestart then
    MsgBox('請重新啟動電腦以完成移除。', mbInformation, MB_OK)
  else if (CurUninstallStep = usPostUninstall) and NvdaRestart then
    MsgBox('請重新啟動 NVDA 以完成移除附加組件。', mbInformation, MB_OK);
end;
