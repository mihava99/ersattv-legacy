; Inno Setup script for the ErsatzTV Legacy Windows installer.
;
; Built by .github/workflows/install-packages.yml, which passes:
;   /DAppVersion=<version label>   /DPackageDir=<folder with the packaged app>   /DOutputDir=<where to write Setup.exe>
;
; Users keep their channels, settings and logs in %LOCALAPPDATA%\ersatztv, outside of the install folder,
; so upgrading and uninstalling never touch them (the uninstaller asks before deleting them).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PackageDir
  #error Pass the packaged app folder with /DPackageDir=<folder>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "ErsatzTV Legacy"
#define Launcher "ErsatzTV-Windows.exe"
#define FirewallRule "ErsatzTV Legacy"

[Setup]
; never change AppId: it is how an upgrade finds the existing installation
AppId={{1048F42F-E46D-41DA-88C4-9267B18BE20A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ErsatzTV
AppPublisherURL=https://ersatztv.org
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir={#OutputDir}
OutputBaseFilename=ErsatzTV-Legacy-Setup-win-x64
SetupIconFile=..\..\artwork\Ersatztv.ico
UninstallDisplayIcon={app}\{#Launcher}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
PrivilegesRequired=admin
; running ErsatzTV is stopped by the [Code] section below, not by Restart Manager prompts
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start ErsatzTV when I sign in to Windows"; GroupDescription: "Startup:"
Name: "firewall"; Description: "Let other devices on my home network connect (adds a Windows Firewall rule)"; GroupDescription: "Network:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#Launcher}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#Launcher}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#Launcher}"; Tasks: startup

[Run]
; a fresh rule every time, scoped to the local network on private/domain networks only
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow program=""{app}\ErsatzTV.exe"" enable=yes profile=private,domain remoteip=LocalSubnet"; Flags: runhidden; Tasks: firewall
Filename: "{app}\{#Launcher}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Stops ErsatzTV, its scanner, ffmpeg and the tray launcher, but only the copies running from the install folder,
// so an unrelated ffmpeg.exe elsewhere on the machine is left alone.
procedure StopErsatzTV;
var
  ResultCode: Integer;
  Folder: String;
begin
  Folder := ExpandConstant('{app}');
  StringChangeEx(Folder, '''', '''''', True);
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith(''' + Folder + '\'', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopErsatzTV;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopErsatzTV;

  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    if MsgBox('Also delete your ErsatzTV channels, settings and logs?' + #13#10 + #13#10 +
              'Choose No to keep them for a future install.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\ersatztv'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\etv-transcode'), True, True, True);
    end;
  end;
end;
