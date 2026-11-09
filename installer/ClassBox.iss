; Class Box 安装程序脚本（Inno Setup 6）
; 使用方式：由 build.ps1 自动调用，或手动执行
;   ISCC.exe /DMyAppVersion=1.3.0 /DMySourceDir=..\dist\app ClassBox.iss
;
; 界面语言：若 installer 目录下存在 ChineseSimplified.isl（Inno 官方非官方语言包），
; 会自动优先使用全中文界面；否则使用内置英文语言 + 下方关键文案中文覆盖。

#ifndef MyAppVersion
  #define MyAppVersion "1.3.0"
#endif

#ifndef MySourceDir
  #define MySourceDir "..\dist\app"
#endif

#define MyAppName "Class Box"
#define MyAppPublisher "Class Box Team"
#define MyAppExeName "ClassBox.exe"
#define MyAppId "{{8E3F1C42-5B7A-4C1E-9F64-1A2D3B4C5D6E}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=ClassBox-Setup-{#MyAppVersion}
OutputDir=..\dist
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 程序需要写入自身目录下的 Plugins（插件安装/删除），因此要求管理员权限
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\ClassBox\Assets\ClassBox.ico
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
#if FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
#else
Name: "english"; MessagesFile: "compiler:Default.isl"
#endif

#if !FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
[Messages]
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonCancel=取消
ButtonBrowse=浏览(&R)...
ClickNext=点击“下一步”继续，或点击“取消”退出安装程序。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=即将在您的电脑上安装 [name/ver]。%n%n建议关闭其它正在运行的程序后继续。
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
ReadyLabel1=准备开始安装 [name]。
ReadyLabel2a=点击“安装”开始安装，或点击“上一步”复查修改设置。
InstallingLabel=正在安装 [name]，请稍候...
FinishedHeadingLabel=[name] 安装完成
FinishedLabel=安装程序已完成 [name] 的安装。
FinishedLabelNoIcons=安装程序已完成 [name] 的安装。
SetupAppTitle=安装程序
SetupWindowTitle=安装程序 - [name]
ExitSetupTitle=退出安装程序
ExitSetupMessage=安装尚未完成，确定要退出吗？
#endif

[CustomMessages]
CreateDesktopIcon=创建桌面快捷方式(&D)
AdditionalIcons=附加任务：
UninstallProgram=卸载 %1
LaunchProgram=运行 %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序为自包含单文件，无需额外运行时
Source: "{#MySourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 插件目录：整体安装，便于后续增删插件
Source: "{#MySourceDir}\Plugins\*"; DestDir: "{app}\Plugins"; Flags: ignoreversion recursesubdirs createallsubdirs
; 语言资源（若存在）
Source: "{#MySourceDir}\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 插件运行期可能生成 .deps.json 等文件，卸载时一并清理
Type: filesandordirs; Name: "{app}\Plugins"
