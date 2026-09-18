; OctoConverter 설치 스크립트 (Inno Setup 6)
; 빌드 방법: installer\build-installer.ps1 실행 (게시 → 설치파일 생성까지 자동)
; 결과물: installer\output\OctoConverter-Setup-<버전>.exe
;
; v1.0.x 까지는 WiX MSI(Program Files, 전체 사용자 설치)로 배포했다. 그 설치본이 남아 있으면
; 아래 [Code] 가 설치 전에 자동으로 제거한다 (관리자 권한 확인 창이 한 번 뜬다).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "OctoConverter"
#define AppPublisher "OctoBrain Softworks"
#define AppExeName "OctoConverter.exe"
#define PublishDir "..\bin\Release\Publish"

[Setup]
; AppId는 업그레이드 인식용 고유 값 - 절대 변경하지 말 것
AppId={{5C2F8A7E-9D14-4B6A-8E3F-0A1B2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\Assets\OctoConverter.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 관리자 권한 없이 사용자 단위 설치 (프로그램 파일 대신 LocalAppData)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 실행 중인 OctoConverter를 감지해 종료 안내
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb,*.xml"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 제거 전에 실행 중인 앱 종료
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
; 앱이 내려받은 FFmpeg 등 정리
Type: filesandordirs; Name: "{localappdata}\OctoConverter"

[Code]
{ ============================================================================
  옛 WiX MSI 설치본(v1.0.x, Program Files) 제거.
  제어판 '프로그램 제거' 항목 중 DisplayName=OctoConverter / Publisher=OctoBrain Softworks 인
  MSI 제품 코드(GUID 형식 키)를 찾아 msiexec /x 로 조용히 제거한다. Burn 부트스트래퍼(setup.exe)
  항목(BundleCachePath 보유)은 exe 를 /uninstall /quiet 로 실행한다.
  전체 사용자 설치였으므로 UAC 확인 창이 한 번 뜬다.
  ============================================================================ }
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

function IsOldOctoConverter(Root: Integer; const SubKey: String): Boolean;
var
  Name, Pub: String;
begin
  Result := False;
  if RegQueryStringValue(Root, SubKey, 'DisplayName', Name) and (Name = '{#AppName}') then
  begin
    if RegQueryStringValue(Root, SubKey, 'Publisher', Pub) then
      Result := (Pub = '{#AppPublisher}')
    else
      Result := True;
  end;
end;

{ 지정한 레지스트리 루트에서 옛 설치본을 찾아 제거. 제거를 시도했으면 True. }
function RemoveOldMsi(Root: Integer): Boolean;
var
  Names: TArrayOfString;
  I, ResultCode: Integer;
  SubKey, Cache: String;
begin
  Result := False;
  if not RegGetSubkeyNames(Root, UninstallKey, Names) then
    exit;
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if (Length(Names[I]) = 0) or (Names[I][1] <> '{') then
      continue;
    SubKey := UninstallKey + '\' + Names[I];
    if not IsOldOctoConverter(Root, SubKey) then
      continue;
    { Inno Setup 자기 자신의 항목(AppId_is1)은 건너뛴다 }
    if Pos('_is1', Names[I]) > 0 then
      continue;
    Log('옛 OctoConverter 설치본 발견: ' + Names[I]);
    if RegQueryStringValue(Root, SubKey, 'BundleCachePath', Cache) and FileExists(Cache) then
      ShellExec('runas', Cache, '/uninstall /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      ShellExec('runas', 'msiexec.exe', '/x ' + Names[I] + ' /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Log('제거 결과 코드: ' + IntToStr(ResultCode));
    Result := True;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { MSI(x64)는 64비트 뷰, Burn 번들(32비트 exe)은 32비트 뷰에 등록된다 }
  RemoveOldMsi(HKLM64);
  RemoveOldMsi(HKLM32);
  RemoveOldMsi(HKCU);
end;
