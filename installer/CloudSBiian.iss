; Inno Setup 6 script for CloudSBiian (built in CI by build/package.ps1)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\CloudSBiian"
#endif
#ifndef DepsDir
  #define DepsDir "..\artifacts\deps"
#endif

[Setup]
AppId={{6C1C2B0E-5E4A-4E8B-9F57-0B6C1A7E5D21}
AppName=CloudSBiian
AppVersion={#AppVersion}
AppPublisher=CloudSBiian contributors
AppPublisherURL=https://github.com/aldiianshr-commits/CloudsBi-Win
DefaultDirName={autopf}\CloudSBiian
DefaultGroupName=CloudSBiian
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=CloudSBiian-Setup
SetupIconFile=..\src\CloudSBiian.Desktop\Assets\cloudsbiian.ico
UninstallDisplayIcon={app}\CloudSBiian.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; WinFsp is a kernel driver and needs administrator rights to install.
PrivilegesRequired=admin
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "winfsp"; Description: "Install the WinFsp file system driver (required for the virtual drive; GPLv3 with FLOSS exception)"; GroupDescription: "Dependencies:"; Check: not IsWinFspInstalled

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DepsDir}\rclone.exe"; DestDir: "{app}\rclone"; Flags: ignoreversion
Source: "{#DepsDir}\winfsp.msi"; DestDir: "{app}\dependencies"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CloudSBiian"; Filename: "{app}\CloudSBiian.exe"
Name: "{autodesktop}\CloudSBiian"; Filename: "{app}\CloudSBiian.exe"; Tasks: desktopicon

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{app}\dependencies\winfsp.msi"" /qb INSTALLLEVEL=1000 /norestart"; StatusMsg: "Installing WinFsp…"; Tasks: winfsp; Flags: waituntilterminated
Filename: "{app}\CloudSBiian.exe"; Description: "Start CloudSBiian"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Only program files are removed. User data (%LOCALAPPDATA%\CloudSBiian: settings, cache, pending
; uploads) and anything in the cloud are never deleted by the uninstaller. WinFsp stays installed
; because other programs may use it (uninstall it separately from Windows Settings if desired).
Type: filesandordirs; Name: "{app}\rclone"

[Code]
function IsWinFspInstalled: Boolean;
var
  Dir: String;
begin
  Result := RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir) and (Dir <> '');
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  SuppressibleMsgBox('CloudSBiian will be removed. Your settings, cache and any changes not yet uploaded stay in %LOCALAPPDATA%\CloudSBiian, and no file in your cloud accounts is touched.', mbInformation, MB_OK, IDOK);
end;
