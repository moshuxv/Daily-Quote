; 拾句 安装脚本（产物与脚本均位于项目内 installer\）
#define MyAppName "拾句"
#define MyAppVersion "1.0.0"
#define MyPublisher "拾句"
; 发布目录：相对本脚本上一级的 publish\（即 拾句\publish\）
#define SourceDir "..\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-1234567890AB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
; 默认安全路径（无需管理员）；选 Program Files 时由下方 admin 提权支持
DefaultDirName={%LOCALAPPDATA}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
; 输出到本脚本所在目录（installer\）
OutputDir=.
OutputBaseFilename={#MyAppName}_setup
Compression=lzma2/ultra64
SolidCompression=yes
; 允许安装到受保护目录（如 Program Files），弹 UAC 确认
PrivilegesRequired=admin
UsePreviousAppDir=no
ArchitecturesInstallIn64BitMode=x64os
DirExistsWarning=no
WizardStyle=modern
; SetupIconFile 跳过：指向 155MB 的 exe 会报 File too large

[Languages]
; 未内置中文 isl，使用默认英文向导

[Files]
; 只打包根目录真正需要的文件（单文件 exe 已内置 .NET 运行时，无需 win-x64/ 等框架依赖副本）
Source: "{#SourceDir}\拾句.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\corpus.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppName}.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Registry]
; 开机自启（HKCU Run 键，卸载时自动清理）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "拾句"; ValueData: """{app}\{#MyAppName}.exe"""; Flags: uninsdeletevalue
