#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef DeliveryDir
  #error DeliveryDir is required
#endif
[Setup]
AppId={{94A2BE85-EB11-422A-934E-42AE4CDD8AB8}
AppName=iPhoneMirror Alejandro
AppVersion=1.8.5-pre3.alejandro.1
AppPublisher=Alejandro / RayrenSX (proyecto original)
VersionInfoVersion=1.8.5.0
DefaultDirName={localappdata}\Programs\iPhoneMirror Alejandro
DefaultGroupName=iPhoneMirror Alejandro
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#DeliveryDir}
OutputBaseFilename=iPhoneMirror-Alejandro-Setup-1.8.5-pre3.alejandro.1-x64
SetupIconFile=..\src\App\Assets\iPhoneMirror.ico
UninstallDisplayIcon={app}\iPhoneMirror.exe
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=iPhoneMirror.exe,iPhoneMirror.Driver.exe
RestartApplications=no
SetupLogging=yes
[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; Flags: checkedonce
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\iPhoneMirror Alejandro"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\iPhoneMirror Alejandro"; Filename: "{app}\iPhoneMirror.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Guía de uso"; Filename: "{app}\LEEME.txt"
[Run]
Filename: "{app}\iPhoneMirror.exe"; Description: "Abrir iPhoneMirror Alejandro"; Flags: nowait postinstall skipifsilent unchecked
