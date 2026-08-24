; Windows installer for the launcher's *first* copy.
;
; This exists only to put the first build on somebody's machine. Every update after it is the
; launcher replacing itself from a signed archive (Documentation/self-update.md), so this file
; is not part of the release loop: bumping a version and shipping an update does not require
; rebuilding the installer, and the installer is never published to the server.
;
; Requires Inno Setup 6.3 or later (for `x64compatible`). Build the payload first:
;
;   dotnet publish src/GameLauncher.App -c Release -r win-x64 --self-contained -o dist/win-x64
;   ISCC.exe installer.iss
;
; The result lands in Output/.

; Anchored to the script, not to wherever ISCC was launched from: the preprocessor resolves a
; relative path against the current directory, while [Files] resolves it against this file, and
; the two disagreeing is a build that passes its checks and packs nothing. The doubled
; backslash is deliberate — SourcePath may or may not bring its own, and Windows ignores it.
#define SourceDir SourcePath + "\dist\win-x64"
#define AppExeName "GameLauncher.exe"

; The name here cannot be read out of launcher.config.json — Inno has no JSON — so a fork that
; renames itself has two places to change instead of one. They are allowed to disagree without
; anything failing, which is exactly why it is worth keeping them in step: this is the name in
; the Start menu and in "Installed apps", and `appName` is the name in the window.
#define AppName "Custom Game Launcher"
#define AppPublisher "Custom Game Launcher"
#define AppURL "https://github.com/Ruy41321/Custom-Game-Launcher-Frontend"

; The version is read out of the executable rather than written here, because the number that
; matters is the one in Directory.Build.props: it is what the update check compares. An
; installer that says 1.1.0 while the binary inside it says 1.0.0 would produce a machine that
; is offered the update it just installed, forever.
#define FullVersion GetFileVersion(SourceDir + "\" + AppExeName)
#if FullVersion == ""
  #error Build the payload first: dotnet publish src/GameLauncher.App -c Release -r win-x64 --self-contained -o dist/win-x64
#endif
; "1.0.0.0" -> "1.0.0", to match the version the release documents carry.
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)

; A build without this directory downloads updates and then refuses to install them, which is a
; failure nobody sees until the second release. Catch it here, where it costs a rebuild.
#if !DirExists(SourceDir + "\updater")
  #error No updater/ in the payload — this build cannot self-update. Publish GameLauncher.App, not the bare project.
#endif

[Setup]
AppId={{8F3A1C2E-4B5D-4E6F-9A7B-0C1D2E3F4A5B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
VersionInfoVersion={#FullVersion}
UninstallDisplayIcon={app}\{#AppExeName}

; Per-user, and not by accident. A self-update renames the installation directory aside to
; `<install>.previous` and puts the new one in its place, so the user must be able to write both
; the installation directory *and its parent* without elevation — an update runs as the launcher
; is exiting and has nobody to ask for a UAC prompt. `lowest` is what makes {autopf} resolve to
; %LOCALAPPDATA%\Programs instead of C:\Program Files.
PrivilegesRequired=lowest
DefaultDirName={autopf}\CustomGameLauncher
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
MinVersion=10.0

; Ask the user to close a running launcher instead of failing halfway through the copy. There is
; no single-instance mutex to name, so this relies on the Restart Manager noticing the files.
CloseApplications=yes
RestartApplications=no

OutputDir=Output
OutputBaseFilename=CustomGameLauncher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=LICENSE

; Enable once branding/windowIconPath points at a real file — see DISTRIBUTING.md §3.3.
; SetupIconFile=assets\icon.ico

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; After the first self-update the files on disk are no longer the ones Inno recorded — the
; installation directory has been replaced wholesale. Its uninstall log would then list files
; that no longer exist and leave the current installation behind, so the directory goes as a
; whole. `.previous` is the copy the last update set aside; Inno has never seen it at all.
;
; What deliberately stays is the user's data directory (%LOCALAPPDATA%\CustomGameLauncher):
; settings, logs, and the games they installed. Uninstalling the launcher is not a request to
; delete somebody's library.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.previous"

[Code]
{ The parent directory has to be writable or the first self-update fails at the rename, with
  nothing on screen to say why — the failure would arrive weeks later, on a release, and look
  like a broken update rather than a bad install path. Checking it here turns that into a
  sentence on the directory page. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Parent, Probe: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    Exit;

  Parent := ExtractFileDir(RemoveBackslashUnlessRoot(WizardDirValue));
  { A parent that does not exist yet is created by the installer under a location it could
    already write, so there is nothing to probe and nothing to warn about. }
  if (Parent = '') or not DirExists(Parent) then
    Exit;

  Probe := AddBackslash(Parent) + 'cgl-write-probe.tmp';
  if SaveStringToFile(Probe, '', False) then
  begin
    DeleteFile(Probe);
    Exit;
  end;

  Result := False;
  MsgBox('The launcher updates itself by replacing its own folder, which means it also has to'
    + ' write into the folder above it:' + #13#10#13#10 + Parent + #13#10#13#10
    + 'That folder is not writable, so updates would fail. Choose a location inside your user'
    + ' profile — the suggested one always works.', mbError, MB_OK);
end;
