; SailTool Inno Setup script — framework-dependent build with runtime bootstrap
; payload: artifacts\payload-fd2-slim  (needs .NET 10 Desktop Runtime + Windows App Runtime 2.x, auto-downloaded when missing)
;
; All defines below can be overridden from the command line with /D, which is how
; installer\build-setup.ps1 drives this script. The fallbacks resolve the
; repository root from this file's own location, so compiling the script by hand
; also works from any checkout — no drive letter is hardcoded anywhere.
#define MyAppName "SailTool"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AppPublisher"
#define MyAppExeName "SailTool.exe"

#ifndef RepoRoot
  #define RepoRoot RemoveBackslash(ExtractFilePath(SourcePath)) + "\.."
#endif
#ifndef PayloadDir
  #define PayloadDir RepoRoot + "\artifacts\payload-fd2-slim"
#endif
#ifndef MyOutputDir
  #define MyOutputDir RepoRoot + "\artifacts\installer\out"
#endif
#ifndef SetupIcon
  #define SetupIcon RepoRoot + "\src\SailTool\Assets\AppIcon.ico"
#endif

#define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe"
#define DotNetFile "windowsdesktop-runtime-10.0.14-win-x64.exe"
#define DotNetSha "91c2795a7b1e2480caa83fce9514dc2e2d21e277d9a297311faba6040bad7330"
#define WarUrl "https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe"
#define VcUrl "https://aka.ms/vs/17/release/vc_redist.x64.exe"

[Setup]
; New AppId: the product was renamed from 363FastTool. Reusing the old AppId would
; make Windows treat SailTool as an in-place upgrade of 363FastTool and leave a
; mixed install behind; a fresh id installs alongside it and uninstalls cleanly.
AppId={{C33E3811-051E-459E-B079-23156D61B5CF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-x64
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetUrl = '{#DotNetUrl}';
  DotNetFile = '{#DotNetFile}';
  DotNetSha = '{#DotNetSha}';
  WarUrl = '{#WarUrl}';
  VcUrl = '{#VcUrl}';

function DirHasPrefix(const Dir, Prefix: String): Boolean;
var
  FR: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Dir) + '*', FR) then
  try
    repeat
      if (FR.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (Pos(Prefix, FR.Name) = 1) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

function HasVCRuntime: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\msvcp140.dll')) and
            FileExists(ExpandConstant('{sys}\vcruntime140.dll')) and
            FileExists(ExpandConstant('{sys}\vcruntime140_1.dll'));
end;

function HasDotNet10Desktop: Boolean;
begin
  Result := DirHasPrefix(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'), '10.')
         or DirHasPrefix(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App'), '10.');
end;

function HasWindowsAppRuntime2: Boolean;
begin
  Result := DirHasPrefix(ExpandConstant('{commonpf}\WindowsApps'), 'Microsoft.WindowsAppRuntime.2');
end;

// 0 = 成功, 3010 = 成功但需重启, 1638/5100 = 已装同版本或更高版本
function ShouldReboot(const RC: Integer): Boolean;
begin
  Result := RC = 3010;
end;

function IsAcceptable(const RC: Integer): Boolean;
begin
  Result := (RC = 0) or (RC = 3010) or (RC = 1638) or (RC = 5100);
end;

function RunInstaller(const FileName, Params: String; var NeedReboot: Boolean): Boolean;
var
  RC: Integer;
begin
  Result := False;
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, RC) then
    Exit;
  if ShouldReboot(RC) then
    NeedReboot := True;
  Result := IsAcceptable(RC);
  Log(Format('prereq %s -> rc=%d accepted=%d', [FileName, RC, Ord(Result)]));
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then
    WizardForm.StatusLabel.Caption :=
      Format('正在下载 %s ... %d%%', [FileName, (Progress * 100) div ProgressMax])
  else
    WizardForm.StatusLabel.Caption := Format('正在下载 %s ...', [FileName]);
  Result := True;
end;

function FetchAndRun(const Url, FileName, Sha, Params, FriendlyName: String;
                     var NeedReboot: Boolean; var Err: String): Boolean;
var
  Path: String;
begin
  Result := False;
  try
    WizardForm.StatusLabel.Caption := '正在下载 ' + FriendlyName + ' ...';
    DownloadTemporaryFile(Url, FileName, Sha, @OnDownloadProgress);
    Path := ExpandConstant('{tmp}\') + FileName;
  except
    Err := '前置组件下载失败（' + FriendlyName + '）。请检查网络后重试，或手动安装该运行库后再运行本安装程序。';
    Exit;
  end;
  WizardForm.StatusLabel.Caption := '正在安装 ' + FriendlyName + ' ...';
  if not RunInstaller(Path, Params, NeedReboot) then
  begin
    Err := FriendlyName + ' 安装失败（返回码非 0）。';
    Exit;
  end;
  Result := True;
end;

function ForcePrereq: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/FORCEPREREQ') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Err: String;
begin
  Result := '';
  NeedsRestart := False;

  if ForcePrereq or (not HasVCRuntime) then
    if not FetchAndRun(VcUrl, 'vc_redist.x64.exe', '', '/install /quiet /norestart',
                       'Visual C++ 运行库', NeedsRestart, Err) then
    begin
      Result := Err;
      Exit;
    end;

  if ForcePrereq or (not HasDotNet10Desktop) then
    if not FetchAndRun(DotNetUrl, DotNetFile, DotNetSha, '/install /quiet /norestart',
                       '.NET 10 桌面运行时', NeedsRestart, Err) then
    begin
      Result := Err;
      Exit;
    end;

  if ForcePrereq or (not HasWindowsAppRuntime2) then
    if not FetchAndRun(WarUrl, 'windowsappruntimeinstall-x64.exe', '', '--quiet',
                       'Windows App Runtime 2.x', NeedsRestart, Err) then
    begin
      Result := Err;
      Exit;
    end;

  if not HasDotNet10Desktop then
  begin
    Result := '.NET 10 桌面运行时检测未通过，安装中止。';
    Exit;
  end;
  if not HasWindowsAppRuntime2 then
  begin
    Result := 'Windows App Runtime 2.x 检测未通过，安装中止。';
    Exit;
  end;
end;
