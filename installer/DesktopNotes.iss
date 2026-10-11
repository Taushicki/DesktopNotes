; Inno Setup 6.7+; compile through scripts/build-installer.ps1.
#ifndef AppVersion
  #error AppVersion must be supplied by the build script
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by the build script
#endif
#ifndef OutputDirPath
  #error OutputDirPath must be supplied by the build script
#endif

[Setup]
AppId={{31BB084E-0256-46E9-9E56-A72EE3A6A8D4}
AppName=DesktopNotes
AppVersion={#AppVersion}
AppPublisher=Taushicki
AppPublisherURL=https://github.com/Taushicki/DesktopNotes
AppSupportURL=https://github.com/Taushicki/DesktopNotes/issues
AppUpdatesURL=https://github.com/Taushicki/DesktopNotes/releases
DefaultDirName={localappdata}\Programs\DesktopNotes
DefaultGroupName=DesktopNotes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
OutputDir={#OutputDirPath}
OutputBaseFilename=DesktopNotes-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\DesktopNotes.exe
AppMutex=Local\DesktopNotes.Installation
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DesktopNotes"; Filename: "{app}\DesktopNotes.exe"
Name: "{autodesktop}\DesktopNotes"; Filename: "{app}\DesktopNotes.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DesktopNotes.exe"; Description: "{cm:LaunchProgram,DesktopNotes}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove only the shell command owned by this installation. Never touch notes. }
    if RegQueryStringValue(HKCU, 'Software\Classes\DesktopBackground\shell\DesktopNotes\command', '', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\DesktopNotes.exe') + '" --new-note') = 0 then
        RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\DesktopBackground\shell\DesktopNotes');
  end;
end;
