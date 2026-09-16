; Inno Setup script for the BitKraken Windows installer (setup .exe).
;
; Compiled by scripts/package-windows.sh, which supplies every value that varies between builds:
;   /DAppVersion=1.0.3-preview  /DVersionCore=1.0.3  /DSourceDir=...\publish\portable-win-x64\...
;   /DOutputDir=...\dist  /DOutputBaseName=BitKraken-1.0.3-preview-win-x64-setup
;   /DArchitecture=x64compatible  /DSetupIconFile=...\Assets\bitkraken.ico

#define AppName "BitKraken"
#define AppPublisher "BitKraken"
#define AppUrl "https://github.com/Moonwalker1307/BitKraken"
#define AppExeName "BitKraken.exe"
#define TorrentProgId "BitKraken.torrent"

[Setup]
; Never change AppId: it is what lets an installer upgrade an existing install in place
; instead of leaving two copies of BitKraken in "Apps & features".
AppId={{78a3fc34-6f26-4bfc-b088-193c82d9facc}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#VersionCore}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; Installing per-user needs no elevation, which is all a torrent client requires; the dialog still
; offers "for all users" to anyone who wants it (and has an admin to approve it).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile={#SetupIconFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ChangesAssociations=yes
; Offer to shut a running BitKraken down rather than failing on a locked BitKraken.exe.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "torrentfiles"; Description: "Open .torrent files with {#AppName}"; GroupDescription: "File associations:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; HKA writes under HKLM for an all-users install and HKCU for a per-user one, so the
; associations match the scope BitKraken was installed in.
;
; The .torrent file type. The ProgId is registered either way - it is what makes BitKraken show up
; in "Open with" - while the extension is only pointed at it when the user asks for it.
Root: HKA; Subkey: "Software\Classes\{#TorrentProgId}"; ValueType: string; ValueData: "BitTorrent file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#TorrentProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#TorrentProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".torrent"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueData: "{#TorrentProgId}"; Tasks: torrentfiles; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: string; ValueName: "{#TorrentProgId}"; ValueData: ""; Flags: uninsdeletevalue
;
; magnet: links are deliberately not registered here. BitKraken claims (and releases) that scheme
; itself from its "Handle magnet links" setting - see Services/ShellIntegration.cs - and an installer
; writing the same keys would either fight that setting or have the uninstaller drop an association
; the user had pointed at another client.

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
