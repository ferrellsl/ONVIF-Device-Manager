; ONVIF Device Manager installer (Inno Setup 6): iscc installer\odm.iss
; Build the solution first (Release|x64).

#define AppName "ONVIF Device Manager"
#define AppVersion "2.3.0"
#define Bin "..\odm\odm.ui.app\bin\Release\x64"
#define Crt "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\VC\Redist\MSVC\14.29.30133\x64\Microsoft.VC142.CRT"

[Setup]
AppId={{6B0C43E2-6E5A-4D57-9C3A-0D4D2B6A7F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ODM contributors
AppPublisherURL=https://github.com/ferrellsl/ONVIF-Device-Manager
AppSupportURL=https://github.com/ferrellsl/ONVIF-Device-Manager/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\odm.exe
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=ODM-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
WizardStyle=modern
PrivilegesRequired=admin

[Files]
; the program (not debug symbols, nor anything a run of it left behind in the build folder)
Source: "{#Bin}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb,\logs\*,\config\*,\meta\*"
; the Visual C++ runtime the player needs, next to it
Source: "{#Crt}\msvcp140.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Crt}\vcruntime140.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Crt}\vcruntime140_1.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\libs\ffmpeg\COPYING.LGPLv2.1"; DestDir: "{app}\3rd"; DestName: "FFmpeg-LICENSE.txt"
Source: "..\README.md"; DestDir: "{app}"

[Dirs]
; ODM writes its logs here (relative to the program folder)
Name: "{app}\logs"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\odm.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\odm.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
Filename: "{app}\odm.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"

[Code]
// .NET Framework 4.8 (release 528040 or later)
function InitializeSetup(): Boolean;
var
  release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', release) and (release >= 528040);
  if not Result then
    MsgBox('{#AppName} needs the .NET Framework 4.8. Install it from Microsoft (https://dotnet.microsoft.com/download/dotnet-framework/net48), then run this setup again.', mbError, MB_OK);
end;
