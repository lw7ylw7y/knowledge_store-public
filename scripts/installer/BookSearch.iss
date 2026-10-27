; Inno Setup script (free, https://jrsoftware.org/isinfo.php). Built by scripts\build-release.ps1, which passes:
;   /DAppVersion=0.1.0  /DSourceDir=<publish folder>  /DOutputDir=<folder for the installer>
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required (the folder made by scripts\publish-app.ps1).
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; A new id for the renamed product: an install made under the old name is a separate entry and is removed from Apps first (keep the data when asked).
AppId={{D4CDE503-3D70-41A4-B6B3-AEAE3A4CA05F}
AppName=Docent
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Docent
DefaultGroupName=Docent
DisableProgramGroupPage=yes
; Per-user install: no administrator rights, no UAC prompt. {autopf} then means %LOCALAPPDATA%\Programs.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 22H2 (build 19045) and newer.
MinVersion=10.0.19045
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
OutputDir={#OutputDir}
OutputBaseFilename=Docent-Setup-{#AppVersion}
UninstallDisplayIcon={app}\BookSearch.exe
SetupIconFile=..\..\src\BookSearch.App\Assets\Docent.ico

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Docent"; Filename: "{app}\BookSearch.exe"

[Run]
Filename: "{app}\BookSearch.exe"; Description: "Start Docent"; Flags: nowait postinstall skipifsilent

[Code]
{ The library, search history, settings and downloaded models live in %LOCALAPPDATA%\BookSearch, apart from the program.
  Uninstalling asks before touching them; the default is to keep them, and a silent uninstall always keeps them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\BookSearch');
  if UninstallSilent() or not DirExists(DataDir) then Exit;
  if MsgBox('Do you also want to delete your library, search history, settings and downloaded models?' + #13#10 + #13#10 +
            DataDir + #13#10 + #13#10 +
            'Choose No to keep them; installing the program again brings your library back. Your book files are never touched.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
