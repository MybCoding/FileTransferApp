[Setup]
AppId={{8F4E9B2A-3D5C-4A1B-9E7F-2C6D8A5B3E1F}
AppName=FileTransferApp
AppVersion=1.1.1
AppPublisher=Yazdani
AppPublisherURL=https://github.com/MybCoding/FileTransferApp
AppSupportURL=https://github.com/MybCoding/FileTransferApp
AppUpdatesURL=https://github.com/MybCoding/FileTransferApp
DefaultDirName={autopf}\FileTransferApp
DefaultGroupName=FileTransferApp
DisableProgramGroupPage=yes
OutputDir=D:\mostafa\FileTransferApp_1404106\FileTransferApp_14040216\dist
OutputBaseFilename=FileTransferApp-Setup-1.1.1
SetupIconFile=D:\mostafa\FileTransferApp_1404106\FileTransferApp_14040216\FileTransferApp\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\appicon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\FileTransferApp.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "D:\mostafa\FileTransferApp_1404106\FileTransferApp_14040216\FileTransferApp\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FileTransferApp"; Filename: "{app}\FileTransferApp.exe"
Name: "{group}\{cm:UninstallProgram,FileTransferApp}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\FileTransferApp"; Filename: "{app}\FileTransferApp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FileTransferApp.exe"; Description: "{cm:LaunchProgram,FileTransferApp}"; Flags: nowait postinstall skipifsilent