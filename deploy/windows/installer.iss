#define MyAppName "幻兽商域"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\..\artifacts\release\幻兽商域"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\..\artifacts\release"
#endif

[Setup]
AppId={{5B79490B-7997-4C12-BE80-4B7CF20CD7E6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Pal Control
DefaultDirName={localappdata}\Programs\PalControl
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyOutputDir}
OutputBaseFilename=幻兽商域-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayIcon={app}\PalControl.ControlApi.exe
VersionInfoVersion=0.1.0.0
VersionInfoDescription=幻兽商域 Windows 安装程序
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
DialogFontName=Microsoft YaHei UI
DialogFontSize=9
WelcomeFontName=Microsoft YaHei UI
WelcomeFontSize=14

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ClickNext=点击“下一步”继续，或点击“取消”退出安装程序。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=现在将安装 [name/ver] 到您的电脑中。%n%n建议您在继续前关闭其他应用程序。
WizardSelectDir=选择安装位置
SelectDirDesc=您想将 [name] 安装在哪里？
SelectDirLabel3=安装程序将把 [name] 安装到下面的文件夹中。
SelectDirBrowseLabel=点击“下一步”继续；若要选择其他文件夹，请点击“浏览”。
WizardSelectTasks=选择附加任务
SelectTasksDesc=您希望安装程序执行哪些附加任务？
SelectTasksLabel2=请选择附加任务，然后点击“下一步”继续。
WizardReady=准备安装
ReadyLabel1=安装程序已经准备好安装 [name]。
ReadyLabel2a=点击“安装”继续；如需修改设置，请点击“上一步”。
ReadyLabel2b=点击“安装”继续。
ReadyMemoDir=安装位置：
ReadyMemoTasks=附加任务：
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候。
FinishedHeadingLabel=[name] 安装完成
FinishedLabel=安装程序已经在您的电脑中安装了 [name]。
ClickFinish=点击“完成”退出安装程序。
RunEntryExec=运行 %1
StatusCreateDirs=正在创建文件夹...
StatusExtractFiles=正在解压文件...
StatusCreateIcons=正在创建快捷方式...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
ConfirmUninstall=确定要移除 %1 及其所有组件吗？
UninstallStatusLabel=正在从您的电脑中移除 %1，请稍候。
UninstalledAll=已成功从您的电脑中移除 %1。

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\启动幻兽商域"; Filename: "{app}\启动幻兽商域.cmd"; WorkingDir: "{app}"
Name: "{group}\配置幻兽商域"; Filename: "{app}\配置幻兽商域.cmd"; WorkingDir: "{app}"
Name: "{group}\停止幻兽商域"; Filename: "{app}\停止幻兽商域.cmd"; WorkingDir: "{app}"
Name: "{group}\安装与使用说明"; Filename: "{app}\安装使用说明.html"
Name: "{autodesktop}\幻兽商域"; Filename: "{app}\启动幻兽商域.cmd"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项："

[Run]
Filename: "{app}\配置幻兽商域.cmd"; Description: "立即配置 Palworld 服务端连接"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\stop.ps1"" -Silent"; Flags: runhidden waituntilterminated; RunOnceId: "StopPalControl"
