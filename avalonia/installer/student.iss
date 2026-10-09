#define AppVersion "0.1.0"
[Setup]
AppId={{1F89D440-C52A-45AF-8A0B-AE149B3EA5F2}
AppName=SQL · Ученик
AppVersion={#AppVersion}
AppPublisher=SQL Classroom
DefaultDirName={localappdata}\Programs\SQL-Classroom-Student
DefaultGroupName=SQL Classroom
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installers
OutputBaseFilename=SQL-Student-{#AppVersion}-win-x64
SetupIconFile=..\src\Classroom.Desktop\classroom.ico
UninstallDisplayIcon={app}\Classroom.Student.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "..\artifacts\publish\Student\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\SQL · Ученик"; Filename: "{app}\Classroom.Student.exe"
Name: "{autodesktop}\SQL · Ученик"; Filename: "{app}\Classroom.Student.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
[Run]
Filename: "{app}\Classroom.Student.exe"; Description: "Запустить SQL · Ученик"; Flags: nowait postinstall skipifsilent
; User data live outside the installation folder. Uninstall and upgrades preserve them.
