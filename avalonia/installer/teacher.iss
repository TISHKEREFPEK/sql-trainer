#define AppVersion "0.1.0"
[Setup]
AppId={{A183AAE8-DC11-4058-B80C-78A77E09A019}
AppName=SQL · Преподаватель
AppVersion={#AppVersion}
AppPublisher=SQL Classroom
DefaultDirName={localappdata}\Programs\SQL-Classroom-Teacher
DefaultGroupName=SQL Classroom
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installers
OutputBaseFilename=SQL-Teacher-{#AppVersion}-win-x64
SetupIconFile=..\src\Classroom.Desktop\classroom.ico
UninstallDisplayIcon={app}\Classroom.Teacher.exe
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Files]
Source: "..\artifacts\publish\Teacher\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\SQL · Преподаватель"; Filename: "{app}\Classroom.Teacher.exe"
Name: "{autodesktop}\SQL · Преподаватель"; Filename: "{app}\Classroom.Teacher.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
[Run]
Filename: "{app}\Classroom.Teacher.exe"; Description: "Запустить SQL · Преподаватель"; Flags: nowait postinstall skipifsilent
; User data live outside the installation folder. Uninstall and upgrades preserve them.
