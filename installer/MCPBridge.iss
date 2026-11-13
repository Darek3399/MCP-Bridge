#define AppName "MCP Bridge"
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Installer.ps1 from Directory.Build.props
#endif
#define AppExeName "MCPBridge.exe"
#define PublishDir "..\Work\Build\publish\win-x64"

[Setup]
AppId={{17473ED1-A877-4A88-8485-A68A5DD8F646}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MCP Bridge
DefaultDirName={code:GetDefaultInstallDir}
UsePreviousAppDir=yes
DisableDirPage=no
UsePreviousTasks=no
DefaultGroupName=MCP Bridge
SetupIconFile=..\src\McpBridge\Resources\MCPBridge.ico
UninstallDisplayIcon={app}\{#AppExeName}
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\Work\Build\installer
OutputBaseFilename=MCPBridge-Setup-{#AppVersion}
#ifdef FastDependencyPackaging
Compression=none
SolidCompression=no
#else
Compression=lzma2/ultra64
SolidCompression=yes
#endif
WizardStyle=modern
ShowLanguageDialog=yes
UsePreviousLanguage=no
DisableStartupPrompt=yes
SetupLogging=no
CloseApplications=yes
CloseApplicationsFilter=MCPBridge.exe
RestartApplications=no
Uninstallable=yes
ChangesEnvironment=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
russian.WindowsVersionNotSupported=Для установки требуется Windows 10 версии 1903 (сборка 18362) или новее, либо Windows 11 x64.
english.WindowsVersionNotSupported=Windows 10 version 1903 (build 18362) or newer, or Windows 11 x64 is required.

[CustomMessages]
russian.CreateDesktopIcon=Создать ярлык на рабочем столе
english.CreateDesktopIcon=Create a desktop shortcut
russian.AdditionalIcons=Дополнительные параметры:
english.AdditionalIcons=Additional options:
russian.LaunchApp=Запустить MCP Bridge
english.LaunchApp=Launch MCP Bridge
russian.StoppingApp=Завершение MCP Bridge...
english.StoppingApp=Stopping MCP Bridge...
russian.ErrUninstallOld=Не удалось завершить удаление предыдущей версии. Закройте старую версию приложения и повторите установку.
english.ErrUninstallOld=Failed to uninstall the previous version. Please close the running application and try again.
russian.ErrProbeStart=Не удалось запустить встроенную проверку зависимостей через Windows PowerShell.
english.ErrProbeStart=Failed to start built-in dependency check via Windows PowerShell.
russian.ErrProbeFailed=Проверка зависимостей завершилась с ошибкой. Код: 
english.ErrProbeFailed=Dependency check failed with exit code: 
russian.ErrProbeRead=Не удалось прочитать отчёт проверки зависимостей.
english.ErrProbeRead=Failed to read the dependency check report.
russian.ErrProbeNotRun=Проверка зависимостей не была выполнена.
english.ErrProbeNotRun=Dependency check was not executed.
russian.StatusInstallingNode=Установка Node.js 26.13.0...
english.StatusInstallingNode=Installing Node.js 26.13.0...
russian.ErrNodeStart=Не удалось запустить установщик Node.js 26.13.0 через Windows Installer.
english.ErrNodeStart=Failed to start Node.js 26.13.0 installer via Windows Installer.
russian.ErrNodeExit=Установка Node.js 26.13.0 завершилась с кодом 
english.ErrNodeExit=Node.js 26.13.0 installation failed with code 
russian.StatusVerifyNode=Проверка Node.js после установки...
english.StatusVerifyNode=Verifying Node.js installation...
russian.StatusFinalCheck=Финальная проверка зависимостей...
english.StatusFinalCheck=Finalizing dependency verification...
russian.StatusCopyingFiles=Копирование файлов MCP Bridge и MCP-компонентов...
english.StatusCopyingFiles=Copying MCP Bridge files and components...
russian.StatusCheckingInstalled=Проверка установленных компонентов...
english.StatusCheckingInstalled=Checking installed components...
russian.StatusFinished=Установка завершена.
english.StatusFinished=Installation complete.
russian.StatusCheckingTailscale=Проверка и настройка Tailscale...
english.StatusCheckingTailscale=Checking and configuring Tailscale...
russian.FullReinstall=Полная переустановка MCP Bridge и компонентов (с сохранением настроек и авторизации Tailscale)
english.FullReinstall=Fully reinstall MCP Bridge and components (preserve settings and Tailscale sign-in)
russian.StatusFullReinstall=Подготовка полной переустановки компонентов...
english.StatusFullReinstall=Preparing a full component reinstall...
russian.ErrFullReinstall=Не удалось подготовить полную переустановку компонентов.
english.ErrFullReinstall=Failed to prepare the full component reinstall.
russian.ErrTailscaleFilesMissing=Не найдены встроенные файлы установки Tailscale.
english.ErrTailscaleFilesMissing=Built-in Tailscale installation files were not found.
russian.ErrTailscaleStateMove=Не удалось перенести прежнее состояние Tailscale в Config.
english.ErrTailscaleStateMove=Failed to migrate previous Tailscale state to Config.
russian.ErrTailscaleProbeStart=Не удалось запустить проверку и подготовку Tailscale.
english.ErrTailscaleProbeStart=Failed to start Tailscale verification and setup.
russian.ErrTailscaleProbeFailed=Не удалось проверить или подготовить Tailscale.
english.ErrTailscaleProbeFailed=Failed to verify or prepare Tailscale.
russian.ErrDotNet=.NET Framework 4.8 не обнаружен. На Windows 10 1903+ и Windows 11 он входит в состав системы.
english.ErrDotNet=.NET Framework 4.8 was not detected. It is included with Windows 10 1903+ and Windows 11.
russian.UninstallOptionsTitle=Параметры удаления MCP Bridge
english.UninstallOptionsTitle=MCP Bridge Uninstall Options
russian.UninstallOptionsPrompt=Выберите, что нужно удалить вместе с программой:
english.UninstallOptionsPrompt=Select what should be removed along with the application:
russian.UninstallRemoveSettings=Удалить настройки программы (settings.json)
english.UninstallRemoveSettings=Remove application settings (settings.json)
russian.UninstallRemovePackages=Удалить скачанные пакеты MCP-серверов (ServerPackages)
english.UninstallRemovePackages=Remove downloaded MCP server packages (ServerPackages)
russian.UninstallRemoveLogs=Удалить логи работы программы (Logs)
english.UninstallRemoveLogs=Remove application logs (Logs)
russian.UninstallRemoveTailscaleKey=Удалить ключ и данные узла Tailscale (ProgramData\Tailscale)
english.UninstallRemoveTailscaleKey=Remove Tailscale machine key and local data (ProgramData\Tailscale)
russian.UninstallRemoveNodeJs=Удалить Node.js с компьютера
english.UninstallRemoveNodeJs=Remove Node.js from this computer
russian.UninstallBtnRemove=Удалить
english.UninstallBtnRemove=Uninstall
russian.UninstallBtnCancel=Отмена
english.UninstallBtnCancel=Cancel

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fullreinstall"; Description: "{cm:FullReinstall}"; Flags: unchecked

[Dirs]
Name: "{app}\Config"; Permissions: users-modify
Name: "{app}\Logs"; Permissions: users-modify
Name: "{app}\Updates"; Permissions: users-modify
Name: "{app}\Dependencies"; Permissions: users-readexec
Name: "{app}\Dependencies\EmptyFilesystemRoot"; Permissions: users-modify
Name: "{app}\Adapters"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Config\settings.json,Logs\errors.log,Logs\connections.log,Logs\app.log"
#ifdef FilesystemMcpPayload
Source: "..\Dependencies\FilesystemMcp\*"; DestDir: "{app}\Dependencies\FilesystemMcp"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstallFilesystemMcp
#endif
Source: "..\Dependencies\supergateway.exe"; DestDir: "{app}\Dependencies"; Flags: ignoreversion; Check: ShouldInstallSupergateway
Source: "..\Dependencies\uv.exe"; DestDir: "{app}\Dependencies"; Flags: ignoreversion
Source: "..\Dependencies\uvx.exe"; DestDir: "{app}\Dependencies"; Flags: ignoreversion
Source: "..\Dependencies\tailscale-setup.msi"; DestDir: "{app}\Dependencies"; Flags: ignoreversion
Source: "..\scripts\Uninstall-Tailscale.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion
Source: "..\scripts\TailscaleSetup.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion
Source: "..\scripts\Cleanup-Updates.ps1"; DestDir: "{app}\Scripts"; Flags: ignoreversion
Source: "..\Dependencies\node-v26.13.0-x64.msi"; Flags: dontcopy
#ifdef FilesystemMcpPayload
Source: "..\Dependencies\FilesystemMcp-engine.json"; Flags: dontcopy
#endif
Source: "Check-Dependencies.ps1"; Flags: dontcopy
Source: "NodeRuntimeDiscovery.ps1"; Flags: dontcopy
Source: "..\scripts\FullReinstall.ps1"; Flags: dontcopy

[Registry]
; Suppress only Tailscale's static "Send with Tailscale..." Explorer verb.
; Taildrop itself remains available from the Tailscale application.
Root: HKCR; Subkey: "*\shell\tailscale"; ValueType: string; ValueName: "LegacyDisable"; ValueData: ""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--postinstall-launch"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser
[Icons]
Name: "{group}\MCP Bridge"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Resources\MCPBridge.ico"
Name: "{autodesktop}\MCP Bridge"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Resources\MCPBridge.ico"; Tasks: desktopicon

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM MCPBridge.exe"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StoppingApp}"; RunOnceId: "StopMCPBridge"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Scripts\Uninstall-Tailscale.ps1"" -MsiPath ""{app}\Dependencies\tailscale-installed.msi"" -StateFile ""{app}\Config\tailscale-state.txt"""; Flags: runhidden waituntilterminated; RunOnceId: "StopAndUninstallTailscale"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Scripts\Cleanup-Updates.ps1"" -UpdatesDirectory ""{app}\Updates"""; Flags: runhidden waituntilterminated; RunOnceId: "CleanupMCPBridgeUpdates"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Dependencies\EmptyFilesystemRoot"

[Code]
var
  PreviousInstallDir: String;
  OldUninstallString: String;
  ProbeLines: TArrayOfString;
  ProbeFile: string;
  ProbeError: string;
  ProbeReady: Boolean;
  RebootRequired: Boolean;
  FullReinstallPrepared: Boolean;

function GetPreviousInstallDir(const RootKey: Integer; const UninstallKey: String): String;
var
  Candidate: String;
  UninstallValue: String;
begin
  Result := '';

  if RegQueryStringValue(RootKey, UninstallKey, 'Inno Setup: App Path', Candidate) then
  begin
    if (Candidate <> '') and DirExists(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;

  if RegQueryStringValue(RootKey, UninstallKey, 'InstallLocation', Candidate) then
  begin
    if (Candidate <> '') and DirExists(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;

  // The uninstaller is stored in {app}; this also supports the legacy AppId.
  if RegQueryStringValue(RootKey, UninstallKey, 'UninstallString', UninstallValue) then
  begin
    UninstallValue := RemoveQuotes(UninstallValue);
    if FileExists(UninstallValue) then
    begin
      Candidate := ExtractFileDir(UninstallValue);
      if (Candidate <> '') and DirExists(Candidate) then
        Result := Candidate;
    end;
  end;
end;

function GetDefaultInstallDir(Param: String): String;
begin
  if PreviousInstallDir <> '' then
    Result := PreviousInstallDir
  else
    Result := ExpandConstant('{autopf}\MCP Bridge');
end;

function InitializeSetup: Boolean;
var
  UninstallKey: String;
  UninstallString: String;
  OldUninstaller: String;
begin
  Result := True;
  PreviousInstallDir := '';
  OldUninstallString := '';
  UninstallString := '';
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{17473ED1-A877-4A88-8485-A68A5DD8F646}_is1';

  if not RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', UninstallString) then
  begin
    UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D2AF33D2-E4E3-4AC7-94F6-7F1CC9EE2D42}_is1';
    RegQueryStringValue(HKLM64, UninstallKey, 'UninstallString', UninstallString);
  end;

  if UninstallString <> '' then
  begin
    PreviousInstallDir := GetPreviousInstallDir(HKLM64, UninstallKey);
    OldUninstaller := RemoveQuotes(UninstallString);
    if (PreviousInstallDir = '') and FileExists(OldUninstaller) then
      PreviousInstallDir := ExtractFileDir(OldUninstaller);
    if FileExists(OldUninstaller) then
      OldUninstallString := OldUninstaller;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Existing installations keep their original directory; fresh installs show the folder picker.
  Result := (PageID = wpSelectDir) and (PreviousInstallDir <> '');
end;

function GetProbeValue(const Key: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(ProbeLines) - 1 do
  begin
    if Pos(Key + '=', ProbeLines[I]) = 1 then
    begin
      Result := Copy(ProbeLines[I], Length(Key) + 2, MaxInt);
      Exit;
    end;
  end;
end;

function ProbeFound(const Key: String): Boolean;
begin
  Result := (GetProbeValue(Key) = '1') and not (FullReinstallPrepared and (Key <> 'DotNet48'));
end;

function IsFullReinstallSelected: Boolean;
begin
  Result := WizardIsTaskSelected('fullreinstall');
end;

function PrepareFullReinstall: Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  OutputFile: String;
  Parameters: String;
  ExitCode: Integer;
  OutputLines: TArrayOfString;
  ErrorText: String;
  I: Integer;
begin
  Result := False;
  FullReinstallPrepared := False;

  if not IsFullReinstallSelected then
  begin
    Result := True;
    Exit;
  end;

  WizardForm.StatusLabel.Caption := CustomMessage('StatusFullReinstall');
  WizardForm.Update;
  if not FileExists(ExpandConstant('{tmp}\FullReinstall.ps1')) then
    ExtractTemporaryFile('FullReinstall.ps1');

  ScriptPath := ExpandConstant('{tmp}\FullReinstall.ps1');
  OutputFile := ExpandConstant('{tmp}\mcpbridge-full-reinstall.txt');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  DeleteFile(OutputFile);

  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -AppDirectory "' + ExpandConstant('{app}') +
    '" -OutputFile "' + OutputFile + '"';

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException(CustomMessage('ErrFullReinstall'));

  if (ExitCode <> 0) or not FileExists(OutputFile) then
  begin
    ErrorText := '';
    if FileExists(OutputFile) and LoadStringsFromFile(OutputFile, OutputLines) then
      for I := 0 to GetArrayLength(OutputLines) - 1 do
        ErrorText := ErrorText + #13#10 + OutputLines[I];
    RaiseException(CustomMessage('ErrFullReinstall') + ErrorText);
  end;

  if not LoadStringsFromFile(OutputFile, OutputLines) then
    RaiseException(CustomMessage('ErrFullReinstall'));

  for I := 0 to GetArrayLength(OutputLines) - 1 do
    begin
    if OutputLines[I] = 'FullReinstallPrepared=1' then
      FullReinstallPrepared := True;
    if OutputLines[I] = 'RebootRequired=1' then
    begin
      RebootRequired := True;
    end;
  end;

  if not FullReinstallPrepared then
    RaiseException(CustomMessage('ErrFullReinstall'));

  Result := True;
end;

procedure SetInstallStatus(const Message: String);
begin
  WizardForm.StatusLabel.Caption := Message;
  WizardForm.Update;
end;

function RunDependencyProbe: Boolean;
var
  ExitCode: Integer;
  PowerShellPath: String;
  Parameters: String;
  ScriptPath: String;
begin
  Result := False;
  ProbeReady := False;
  ProbeError := '';
  ProbeFile := ExpandConstant('{tmp}\mcp-dependency-probe.txt');
  DeleteFile(ProbeFile);

  if not FileExists(ExpandConstant('{tmp}\Check-Dependencies.ps1')) then
    ExtractTemporaryFile('Check-Dependencies.ps1');
  if not FileExists(ExpandConstant('{tmp}\NodeRuntimeDiscovery.ps1')) then
    ExtractTemporaryFile('NodeRuntimeDiscovery.ps1');
#ifdef FilesystemMcpPayload
  if not FileExists(ExpandConstant('{tmp}\FilesystemMcp-engine.json')) then
    ExtractTemporaryFile('FilesystemMcp-engine.json');
#endif
  ScriptPath := ExpandConstant('{tmp}\Check-Dependencies.ps1');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -AppDirectory "' + ExpandConstant('{app}') +
    '" -OutputFile "' + ProbeFile +
    '" -PayloadFilesystemPackageJson "' + ExpandConstant('{tmp}\FilesystemMcp-engine.json') + '"';

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    ProbeError := CustomMessage('ErrProbeStart');
    Exit;
  end;
  if (ExitCode <> 0) or not FileExists(ProbeFile) then
  begin
    ProbeError := CustomMessage('ErrProbeFailed') + IntToStr(ExitCode);
    Exit;
  end;
  if not LoadStringsFromFile(ProbeFile, ProbeLines) then
  begin
    ProbeError := CustomMessage('ErrProbeRead');
    Exit;
  end;

  ProbeReady := True;
  Result := True;
end;

function GetPersistentLogPath(const FileName: String): String;
var
  LogsDirectory: String;
begin
  LogsDirectory := ExpandConstant('{app}\Logs');
  ForceDirectories(LogsDirectory);
  Result := AddBackslash(LogsDirectory) + FileName;
end;

procedure InstallMissingDependencies;
var
  InstallerExitCode: Integer;
  NodeInstallerPath: String;
  NodeMsiLogPath: String;
  NodeMsiInstallDirectory: String;
  NodeMsiParameters: String;
  MsiExecPath: String;
begin
  if not ProbeReady then
    RaiseException(CustomMessage('ErrProbeNotRun'));

  if not ProbeFound('Node') then
  begin
    SetInstallStatus(CustomMessage('StatusInstallingNode'));
    ExtractTemporaryFile('node-v26.13.0-x64.msi');
    NodeInstallerPath := ExpandConstant('{tmp}\node-v26.13.0-x64.msi');
    NodeMsiLogPath := GetPersistentLogPath('MCPBridge-node-msi-install.log');
    MsiExecPath := ExpandConstant('{sys}\msiexec.exe');
    NodeMsiInstallDirectory := GetProbeValue('NodeMsiInstallDirectory');
    NodeMsiParameters := '/i "' + NodeInstallerPath + '"';
    if NodeMsiInstallDirectory <> '' then
      NodeMsiParameters := NodeMsiParameters + ' INSTALLDIR="' + AddBackslash(NodeMsiInstallDirectory) + '"';
    NodeMsiParameters := NodeMsiParameters + ' /qn /norestart /L*v "' + NodeMsiLogPath + '"';

    if not Exec(MsiExecPath, NodeMsiParameters, '', SW_HIDE, ewWaitUntilTerminated, InstallerExitCode) then
      RaiseException(CustomMessage('ErrNodeStart'));

    if (InstallerExitCode = 3010) or (InstallerExitCode = 1641) then
    begin
      RebootRequired := True;
    end
    else if InstallerExitCode <> 0 then
      RaiseException(CustomMessage('ErrNodeExit') + IntToStr(InstallerExitCode) + '. Log: ' + NodeMsiLogPath);

    SetInstallStatus(CustomMessage('StatusVerifyNode'));
    if not RunDependencyProbe then
      RaiseException(ProbeError);
  end;

  SetInstallStatus(CustomMessage('StatusFinalCheck'));
  if not RunDependencyProbe then
    RaiseException(ProbeError);

  SetInstallStatus(CustomMessage('StatusCopyingFiles'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  NeedsRestart := False;
  RebootRequired := False;
  FullReinstallPrepared := False;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM MCPBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);  if OldUninstallString <> '' then
  begin
    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress', '1');
    if not Exec(OldUninstallString, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    begin
      RegDeleteValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress');
      Result := CustomMessage('ErrUninstallOld');
      Exit;
    end;
    RegDeleteValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress');
    OldUninstallString := '';
  end;

  try
    if not PrepareFullReinstall then
    begin
      Result := CustomMessage('ErrFullReinstall');
      Exit;
    end;
  except
    Result := GetExceptionMessage;
    Exit;
  end;

  SetInstallStatus(CustomMessage('StatusCheckingInstalled'));

  if not RunDependencyProbe then
  begin
    Result := ProbeError;
    Exit;
  end;

  if not ProbeFound('DotNet48') then
    Result := CustomMessage('ErrDotNet');
end;

function ShouldInstallFilesystemMcp: Boolean;
begin
  Result := ProbeReady and not ProbeFound('FilesystemMcp');
end;

function ShouldInstallSupergateway: Boolean;
begin
  Result := ProbeReady and not ProbeFound('Supergateway');
end;

function NeedRestart: Boolean;
begin
  Result := RebootRequired;
end;

procedure EnsureTailscaleRuntime;
var
  PowerShellPath: String;
  ScriptPath: String;
  MsiPath: String;
  StateFile: String;
  LegacyStateFile: String;
  OutputFile: String;
  Parameters: String;
  ExitCode: Integer;
  OutputLines: TArrayOfString;
  ErrorText: String;
  I: Integer;
begin
  ScriptPath := ExpandConstant('{app}\Scripts\TailscaleSetup.ps1');
  MsiPath := ExpandConstant('{app}\Dependencies\tailscale-setup.msi');
  StateFile := ExpandConstant('{app}\Config\tailscale-state.txt');
  LegacyStateFile := ExpandConstant('{app}\Dependencies\tailscale-state.txt');
  OutputFile := ExpandConstant('{tmp}\mcpbridge-tailscale-install.txt');
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');

  if not FileExists(ScriptPath) or not FileExists(MsiPath) then
    RaiseException(CustomMessage('ErrTailscaleFilesMissing'));

  if FileExists(LegacyStateFile) then
  begin
    if not FileExists(StateFile) then
      if not CopyFile(LegacyStateFile, StateFile, False) then
        RaiseException(CustomMessage('ErrTailscaleStateMove'));
    DeleteFile(LegacyStateFile);
  end;

  DeleteFile(OutputFile);
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Mode "Install" -MsiPath "' + MsiPath +
    '" -StateFile "' + StateFile + '"';
  if IsFullReinstallSelected then
    Parameters := Parameters + ' -ForceRepair';
  Parameters := Parameters + ' -OutputFile "' + OutputFile + '"';

  SetInstallStatus(CustomMessage('StatusCheckingTailscale'));
  if not Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    RaiseException(CustomMessage('ErrTailscaleProbeStart'));

  if (ExitCode <> 0) or not FileExists(OutputFile) then
  begin
    ErrorText := 'Диагностика не записана';
    if FileExists(OutputFile) and LoadStringsFromFile(OutputFile, OutputLines) then
      for I := 0 to GetArrayLength(OutputLines) - 1 do
        ErrorText := ErrorText + #13#10 + OutputLines[I];
    RaiseException(CustomMessage('ErrTailscaleProbeFailed') + ErrorText);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LanguageMarkerPath: String;
begin
  if CurStep = ssInstall then
    InstallMissingDependencies
  else if CurStep = ssPostInstall then
  begin
    EnsureTailscaleRuntime;
    SetInstallStatus(CustomMessage('StatusFinished'));
    LanguageMarkerPath := ExpandConstant('{app}\Config\installer-language.txt');
    SaveStringToFile(LanguageMarkerPath, ActiveLanguage + #13#10, False);
  end;
end;

function InitializeUninstall: Boolean;
var
  UpgradeFlag: String;
  BridgeInstalledTailscale: Boolean;
  StateLines: TArrayOfString;
  I: Integer;
  Form: TSetupForm;
  PromptLabel: TNewStaticText;
  RemoveSettingsCheckBox: TNewCheckBox;
  RemovePackagesCheckBox: TNewCheckBox;
  RemoveLogsCheckBox: TNewCheckBox;
  RemoveTailscaleKeyCheckBox: TNewCheckBox;
  RemoveNodeJsCheckBox: TNewCheckBox;
  OkButton: TNewButton;
  CancelButton: TNewButton;
  RemoveSettingsVal, RemovePackagesVal, RemoveLogsVal, RemoveTailscaleKeyVal, RemoveNodeJsVal: String;
begin
  Result := True;

  if RegQueryStringValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress', UpgradeFlag) and (UpgradeFlag = '1') then
    Exit;

  if UninstallSilent then
    Exit;

  BridgeInstalledTailscale := False;
  if LoadStringsFromFile(ExpandConstant('{app}\Config\tailscale-state.txt'), StateLines) then
  begin
    for I := 0 to GetArrayLength(StateLines) - 1 do
    begin
      if Trim(StateLines[I]) = 'InstalledByMcpBridge=1' then
      begin
        BridgeInstalledTailscale := True;
        Break;
      end;
    end;
  end;

#if Ver >= EncodeVer(6, 6, 0)
  Form := CreateCustomForm(520, 268, False, False);
#else
  Form := CreateCustomForm;
  Form.ClientWidth := ScaleX(520);
  Form.ClientHeight := ScaleY(268);
#endif
  try
    Form.Position := poScreenCenter;
    Form.Caption := CustomMessage('UninstallOptionsTitle');

    PromptLabel := TNewStaticText.Create(Form);
    PromptLabel.Parent := Form;
    PromptLabel.Left := ScaleX(18);
    PromptLabel.Top := ScaleY(16);
    PromptLabel.Width := Form.ClientWidth - ScaleX(36);
    PromptLabel.AutoSize := True;
    PromptLabel.Caption := CustomMessage('UninstallOptionsPrompt');

    RemoveSettingsCheckBox := TNewCheckBox.Create(Form);
    RemoveSettingsCheckBox.Parent := Form;
    RemoveSettingsCheckBox.Left := ScaleX(18);
    RemoveSettingsCheckBox.Top := ScaleY(46);
    RemoveSettingsCheckBox.Width := Form.ClientWidth - ScaleX(36);
    RemoveSettingsCheckBox.Height := ScaleY(20);
    RemoveSettingsCheckBox.Caption := CustomMessage('UninstallRemoveSettings');
    RemoveSettingsCheckBox.Checked := True;

    RemovePackagesCheckBox := TNewCheckBox.Create(Form);
    RemovePackagesCheckBox.Parent := Form;
    RemovePackagesCheckBox.Left := ScaleX(18);
    RemovePackagesCheckBox.Top := ScaleY(76);
    RemovePackagesCheckBox.Width := Form.ClientWidth - ScaleX(36);
    RemovePackagesCheckBox.Height := ScaleY(20);
    RemovePackagesCheckBox.Caption := CustomMessage('UninstallRemovePackages');
    RemovePackagesCheckBox.Checked := True;

    RemoveLogsCheckBox := TNewCheckBox.Create(Form);
    RemoveLogsCheckBox.Parent := Form;
    RemoveLogsCheckBox.Left := ScaleX(18);
    RemoveLogsCheckBox.Top := ScaleY(106);
    RemoveLogsCheckBox.Width := Form.ClientWidth - ScaleX(36);
    RemoveLogsCheckBox.Height := ScaleY(20);
    RemoveLogsCheckBox.Caption := CustomMessage('UninstallRemoveLogs');
    RemoveLogsCheckBox.Checked := True;

    RemoveTailscaleKeyCheckBox := TNewCheckBox.Create(Form);
    RemoveTailscaleKeyCheckBox.Parent := Form;
    RemoveTailscaleKeyCheckBox.Left := ScaleX(18);
    RemoveTailscaleKeyCheckBox.Top := ScaleY(136);
    RemoveTailscaleKeyCheckBox.Width := Form.ClientWidth - ScaleX(36);
    RemoveTailscaleKeyCheckBox.Height := ScaleY(20);
    RemoveTailscaleKeyCheckBox.Caption := CustomMessage('UninstallRemoveTailscaleKey');
    RemoveTailscaleKeyCheckBox.Enabled := BridgeInstalledTailscale;
    RemoveTailscaleKeyCheckBox.Checked := BridgeInstalledTailscale;

    RemoveNodeJsCheckBox := TNewCheckBox.Create(Form);
    RemoveNodeJsCheckBox.Parent := Form;
    RemoveNodeJsCheckBox.Left := ScaleX(18);
    RemoveNodeJsCheckBox.Top := ScaleY(166);
    RemoveNodeJsCheckBox.Width := Form.ClientWidth - ScaleX(36);
    RemoveNodeJsCheckBox.Height := ScaleY(20);
    RemoveNodeJsCheckBox.Caption := CustomMessage('UninstallRemoveNodeJs');
    RemoveNodeJsCheckBox.Checked := False;

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Width := ScaleX(96);
    CancelButton.Height := ScaleY(26);
    CancelButton.Left := Form.ClientWidth - ScaleX(18) - CancelButton.Width;
    CancelButton.Top := Form.ClientHeight - ScaleY(16) - CancelButton.Height;
    CancelButton.Caption := CustomMessage('UninstallBtnCancel');
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    OkButton := TNewButton.Create(Form);
    OkButton.Parent := Form;
    OkButton.Width := ScaleX(104);
    OkButton.Height := ScaleY(26);
    OkButton.Left := CancelButton.Left - ScaleX(8) - OkButton.Width;
    OkButton.Top := CancelButton.Top;
    OkButton.Caption := CustomMessage('UninstallBtnRemove');
    OkButton.ModalResult := mrOk;
    OkButton.Default := True;

    if Form.ShowModal <> mrOk then
    begin
      Result := False;
      Exit;
    end;

    if RemoveSettingsCheckBox.Checked then RemoveSettingsVal := '1' else RemoveSettingsVal := '0';
    if RemovePackagesCheckBox.Checked then RemovePackagesVal := '1' else RemovePackagesVal := '0';
    if RemoveLogsCheckBox.Checked then RemoveLogsVal := '1' else RemoveLogsVal := '0';
    if RemoveTailscaleKeyCheckBox.Checked then RemoveTailscaleKeyVal := '1' else RemoveTailscaleKeyVal := '0';
    if RemoveNodeJsCheckBox.Checked then RemoveNodeJsVal := '1' else RemoveNodeJsVal := '0';

    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveSettings', RemoveSettingsVal);
    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UninstallRemovePackages', RemovePackagesVal);
    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveLogs', RemoveLogsVal);
    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveTailscaleKey', RemoveTailscaleKeyVal);
    RegWriteStringValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveNodeJs', RemoveNodeJsVal);
  finally
    Form.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UpgradeFlag: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress', UpgradeFlag) and (UpgradeFlag = '1') then
      Exit;

    RegDeleteKeyIncludingSubkeys(HKLM, 'Software\MCPBridge');
    RemoveDir(ExpandConstant('{app}\Config'));
    RemoveDir(ExpandConstant('{app}\Dependencies'));
    RemoveDir(ExpandConstant('{app}\Logs'));
    RemoveDir(ExpandConstant('{app}'));
  end;
end;

procedure DeinitializeUninstall;
var
  UpgradeFlag: String;
begin
  if RegQueryStringValue(HKLM, 'Software\MCPBridge', 'UpgradeInProgress', UpgradeFlag) and (UpgradeFlag = '1') then
    Exit;

  RegDeleteValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveSettings');
  RegDeleteValue(HKLM, 'Software\MCPBridge', 'UninstallRemovePackages');
  RegDeleteValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveLogs');
  RegDeleteValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveTailscaleKey');
  RegDeleteValue(HKLM, 'Software\MCPBridge', 'UninstallRemoveNodeJs');
  RegDeleteKeyIfEmpty(HKLM, 'Software\MCPBridge');
end;
