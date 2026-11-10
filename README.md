# Class Box

.NET 8 + WPF 的插件化桌面助手。全内置功能均以插件形式实现，可随时增删插件 DLL。

**发布形态：自包含单文件（`win-x64`）——目标电脑无需安装 .NET 运行时，双击即用。**

---

## 1. 功能一览

| 模块 | 说明 |
| --- | --- |
| 控制台 | 系统概览、插件状态、最近日志、常用入口 |
| 窗口置顶与嵌入 | 枚举可见窗口，一键置顶 / 取消置顶；尝试把目标窗口嵌入本程序容器内集中查看 |
| 人像监测 | 调用本机摄像头周期性采样，判断画面中是否有人；可配置无人提醒时长、采样间隔与触发动作 |
| 插件管理 | 列出全部插件，启用 / 停用、热重载、打开插件数据目录 |
| 设置 | 主题（浅色 / 深色 / 跟随系统）、开机自启、日志级别等 |
| 侧边提示 | 统一的通知浮窗，支持自动关闭、紧急样式 |
| 悬浮球 | 主界面一键收成桌面小球，小球与主界面均强制置顶（依赖管理员权限），单击小球还原主界面 |

---

## 2. 快速构建（一条命令）

前置条件：

- Windows 10 1809 及以上 / Windows 11
- .NET 8 SDK（仅**构建机**需要，最终用户不需要）
- Inno Setup 6（仅打包安装包需要，可缺省）

```powershell
# 完整流程：编译 + 发布自包含单文件 + 打包安装程序
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 只发布程序，不打包安装包
powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipInstaller

# 框架依赖模式（体积小，但目标机需装 .NET 8 桌面运行时）
powershell -ExecutionPolicy Bypass -File .\build.ps1 -FrameworkDependent
```

手动等价命令：

```powershell
dotnet publish src\ClassBox\ClassBox.csproj -c Release -r win-x64 `
  -p:SelfContained=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o dist\app
```

构建产物：

```
dist\
├─ ClassBox-Setup-1.2.0.exe     # 安装包（约 64 MB，内已含运行时）
└─ app\
   ├─ ClassBox.exe              # 自包含单文件可执行程序（约 69 MB）
   └─ Plugins\                  # 插件目录（当前 23 个插件 DLL + 示例插件）
      ├─ ClassBox.Plugins.WindowTopmost.dll
      ├─ ClassBox.Plugins.PresenceMonitor.dll
      ├─ ClassBox.Plugins.ClassTalk.dll
      ├─ ClassBox.Plugins.WebBrowser.dll
      ├─ ClassBox.Plugins.LocalServer.dll
      ├─ ClassBox.Plugins.PortScanner.dll
      ├─ ClassBox.Plugins.HashCheck.dll
      ├─ ClassBox.Plugins.DiskAnalyzer.dll
      ├─ ClassBox.Plugins.ProcessManager.dll
      ├─ ClassBox.Plugins.SystemInfo.dll
      ├─ ClassBox.Plugins.JsonTool.dll
      ├─ ClassBox.Plugins.EncodeTool.dll
      ├─ ClassBox.Plugins.RegexTester.dll
      ├─ ClassBox.Plugins.TextDiff.dll
      ├─ ClassBox.Plugins.MarkdownPreview.dll
      ├─ ClassBox.Plugins.PasswordGen.dll
      ├─ ClassBox.Plugins.UnitConverter.dll
      ├─ ClassBox.Plugins.ClipboardHistory.dll
      ├─ ClassBox.Plugins.ScreenshotTool.dll
      ├─ ClassBox.Plugins.QRCodeTool.dll
      ├─ ClassBox.Plugins.TodoPlanner.dll
      ├─ ClassBox.Plugins.NotesPad.dll
      ├─ ClassBox.Plugins.NetworkToolkit.dll
      └─ ClassBox.SamplePlugin.dll
```

> `ClassBox.csproj` 中 `SelfContained=true`、`PublishSingleFile=true` 已设为默认值，因此直接 `dotnet publish` 也会得到单文件自包含程序。

---

## 3. 安装与首次运行

1. 运行 `ClassBox-Setup-1.2.0.exe`，按提示安装到 `C:\Program Files\Class Box`。
2. 程序清单要求**管理员权限**：因为需要向自身目录写入 / 删除 `Plugins` 下的插件 DLL，并支持置顶、嵌入其它进程窗口等操作。启动时会弹出 UAC，请选择“是”。
3. 首次启动会在 `%APPDATA%\ClassBox` 建立数据目录。

卸载：控制面板 → 程序和功能 → Class Box，或开始菜单中的卸载快捷方式。卸载会一并清理 `Plugins` 目录；`%APPDATA%\ClassBox` 下的个人配置与日志默认保留，如需彻底清除可手动删除该目录。

---

## 4. 目录与数据

| 路径 | 用途 |
| --- | --- |
| `%APPDATA%\ClassBox\config\settings.json` | 主程序设置 |
| `%APPDATA%\ClassBox\logs\` | 运行日志（按日期分文件） |
| `%APPDATA%\ClassBox\plugins\<插件Id>\` | 各插件私有数据 |
| `<程序目录>\Plugins\` | 插件 DLL，放置即生效（重启后加载） |

程序内「设置 → 打开数据目录」可直接跳转。

---

## 5. 插件机制

### 5.1 加载流程

1. 扫描 `<程序目录>\Plugins\*.dll`；
2. 反射查找实现 `ClassBox.Abstractions.Plugins.IPlugin` 的公开非抽象类型；
3. 单一 `AssemblyLoadContext`（默认上下文）加载，缺失依赖时从插件所在目录补解析；
4. 注册 `PluginPageDescriptor` 页面 → 调用 `InitializeAsync` → 依据启用状态调用 `StartAsync`。

单个插件加载失败不会影响其它插件与主程序，错误会记入日志并在「插件管理」页显示。

### 5.2 接口契约

```csharp
public interface IPlugin : IAsyncDisposable
{
    PluginMetadata Metadata { get; }
    bool IsRunning { get; }
    Task InitializeAsync(IPluginContext context, CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
```

通常继承 `PluginBase` 即可，只需实现 `Metadata` 与 `OnInitializeAsync / OnStartAsync / OnStopAsync`。

`IPluginContext` 提供：

- `PluginId`：插件唯一标识
- `DataDirectory`：插件私有数据目录（已自动创建）
- `IsDarkTheme`：当前主题
- `Navigation`：注册 / 移除 / 跳转插件页面
- `Windows`：窗口置顶、嵌入、枚举能力
- `Alerts`：侧边提示
- `Logger` / `Config`：日志与 JSON 配置存取

### 5.3 新增一个插件

1. 新建 `net8.0-windows10.0.19041.0` 的 WPF 类库，引用 `ClassBox.Abstractions`；
2. 继承 `PluginBase`，实现 `Metadata`（`Id`、`Name`、`Description`、`Category`、`Glyph`）；
3. 在 `OnInitializeAsync` 中注册页面：

```csharp
context.Navigation.RegisterPage(new PluginPageDescriptor
{
    PageId = "your.page.id",
    Title = "示例页面",
    Glyph = "\uE8B7",
    ViewFactory = () => new YourPage()
});
```

4. 构建后把生成的 DLL 复制到 `Plugins\` 目录，重启程序即可。

参考实现：`src\ClassBox.SamplePlugin`（最小可运行示例）。

---

## 6. 内置插件说明

### 6.1 窗口置顶与嵌入

- 窗口列表来源：当前会话中可见、有标题的顶层窗口（可切换是否包含本程序自身）；
- 置顶：`SetWindowPos(HWND_TOPMOST / HWND_NOTOPMOST)`；
- 嵌入：`SetParent` 把目标窗口移入本程序容器，失败时自动回退为普通置顶，并在界面提示原因；
- 退出或关闭程序前会解除全部嵌入与置顶，避免目标窗口停留在异常状态；
- 权限差异：以管理员身份运行的本程序无法操作更高完整性级别的窗口时会失败并提示。

### 6.2 人像监测

- 采集：DirectShow 兼容摄像头（Video for Windows 抓帧，零第三方依赖）；
- 判定：基于肤色占比与连通区域的轻量算法，不依赖云服务，画面不出本机；
- 可配置项：摄像头设备、采样间隔、无人判定阈值、连续无人多久后提醒、触发动作（仅提醒 / 提醒并最小化本程序）；
- 隐私：仅在插件启用且监测开关打开时采集；关闭开关立即释放摄像头；不落盘保存任何画面；
- 兼容性提示：若摄像头被其它程序独占（如会议软件），打开会失败并在界面显示错误。

---

## 7. 常见问题

| 现象 | 处理 |
| --- | --- |
| 启动每次都弹 UAC | 正常，程序清单为 `requireAdministrator`；如需免提示可改用计划任务以最高权限启动，但会改变安全边界 |
| 杀软报毒 | 单文件自解压 + 反射加载插件易被启发式误判，添加信任或改为框架依赖发布验证 |
| 插件列表为空 | 确认 `Plugins` 目录与 `ClassBox.exe` 同级；查看日志 `%APPDATA%\ClassBox\logs` 中的加载错误 |
| 摄像头打不开 | 检查是否被其它程序占用；Windows 设置 → 隐私 → 相机 中允许桌面应用访问 |
| 安装包编译失败提示缺少中文语言文件 | 本仓库默认使用 Inno 内置英文语言并中文覆盖关键文案；如需全中文向导，将官方简体中文语言包命名为 `ChineseSimplified.isl` 放入 `installer\` 目录，脚本会自动优先使用 |
| 想减小体积 | 使用 `-FrameworkDependent`（体积约 1–2 MB，但目标机需装 .NET 8 桌面运行时） |

---

## 8. 项目结构

```
ClassBox\
├─ build.ps1                     # 一键构建（发布 + 安装包）
├─ ClassBox.sln
├─ src\
│  ├─ ClassBox.Abstractions\     # 契约层：IPlugin、服务接口、MVVM 基类、主题样式
│  ├─ ClassBox\                  # 主程序：WPF 外壳、插件宿主、DI、日志、窗口服务
│  ├─ ClassBox.Plugins.WindowTopmost\
│  ├─ ClassBox.Plugins.PresenceMonitor\
│  └─ ClassBox.SamplePlugin\     # 插件开发示例
├─ installer\ClassBox.iss        # Inno Setup 脚本
├─ docs\验收清单.md
└─ dist\                         # 构建产物（生成）
```

---

## 10. 主界面置顶与悬浮球（v1.1.0）

### 10.1 行为

- **主界面强制置顶**：`MainWindow.Topmost = true`，同时 `WindowService.StartTopmostGuard()` 每秒静默重申一次 `SetWindowPos(HWND_TOPMOST)`（`SWP_NOACTIVATE`，不抢焦点、不改尺寸位置），避免被其它置顶窗口或全屏应用压到下层。
- **收成小球**：点击标题栏最左侧的空心圆形按钮（提示“缩小为悬浮球”），主界面 `Hide()`，桌面右下角出现 64×64 蓝色圆球；球窗口同样是 `Topmost + 置顶守卫`，且 `ShowInTaskbar=False`。
- **点击还原**：单击小球 → 主界面 `Show()` + `Topmost` + `Activate()`，并把自身重新置为 `HWND_TOPMOST` 后获得前台焦点；小球随即隐藏。
- **拖动**：按住小球拖动可改位置，松手后自动收敛到虚拟屏幕边界内；仅“按下即抬起”才判定为单击还原，拖动不会误触。
- **右键菜单**：显示主界面 / 退出 Class Box。
- **退出**：主界面关闭时会连带关闭小球窗口，确保进程正常结束。

### 10.2 权限说明

“强制置顶”依赖进程已提权（`app.manifest` 为 `requireAdministrator`）：管理员权限下 `SetWindowPos` 可对绝大多数窗口生效；对更高完整性级别（如以管理员运行的其它程序）的窗口仍可能失败，失败会记入日志。

### 10.3 相关代码

| 文件 | 职责 |
| --- | --- |
| `src\ClassBox\Views\FloatingBallWindow.xaml(.cs)` | 小球窗口：拖动、单击还原、右键菜单、置顶守卫 |
| `src\ClassBox\MainWindow.xaml(.cs)` | `Topmost`、标题栏球形按钮、`CollapseToBall()` / `ShowFromBall()`、关闭联动 |
| `src\ClassBox\Services\WindowService.cs` | `StartTopmostGuard()` / `StopTopmostGuard()`（带句柄失效自动停止） |
| `src\ClassBox\App.xaml.cs` | 启动时 `AttachWindowService()` 注入窗口服务并启用置顶 |

---

## 9. 技术约束

- 零第三方 NuGet 依赖：JSON 用 `System.Text.Json`，摄像头用 VfW，MVVM 基类自实现；
- 目标框架 `net8.0-windows10.0.19041.0`，启用 `PerMonitorV2` DPI 感知；
- 所有面向用户的文本为中文，源码注释为中文。

---

## 11. 变更记录

### v1.3.0（2026-11-09）

- **ClassTalk 隐藏管理入口**：删除聊天页可见的「管理员」按钮与相关提示文案，普通用户界面不再出现任何管理员相关内容；管理员控制台改为浮层，仅在口令触发后出现。
- **口令触发控制台**：在聊天输入框发送 `/admin` 即打开控制台（该指令不会作为聊天内容发给他人）；本机已是管理员时直接打开，尚无管理员时先到先得竞选，已被他人持有时给出提示。
- **加密口令框支持 `/admin`**：聊天页「加密口令」输入框内输入 `/admin` 并确认，即成为当前房间管理员（主机侧本地登记，成员侧经加密通道向主机申请）；口令框本身始终回显房间口令，不影响原口令校验；成为管理员后在该处再次输入 `/admin` 可随时呼出控制台。
- **局域网会话检测**：新增会话发现能力（UDP 55211 广播 + TTL 清理），登录页实时列出局域网内正在进行的 ClassTalk 会话（会话名、主机、端口、在线人数、是否加密），支持手动刷新、自动刷新开关与一键加入；主机侧随房间启停自动开始/停止公告。
- **重新打包**：程序与安装包版本号统一为 1.3.0（`ClassBox.exe` 单文件自包含、安装包 `ClassBox-Setup-1.3.0.exe`）。

### v1.2.0（2026-11-09）

- **插件市场成型**：插件总数由 3 个扩充至 23 个，全部以独立 DLL 形式随安装包分发，安装后即可在「插件」页启用。
- **新增插件**：ClassTalk（局域网加密通讯，由原独立程序移植）、WebBrowser（内置浏览器）、LocalServer（把电脑变成 HTTP 服务器）、PortScanner（端口扫描）、HashCheck（哈希校验）、DiskAnalyzer（磁盘分析）、ProcessManager（进程管理）、SystemInfo（系统信息）、JsonTool（JSON 格式化/校验/转 C# 类）、EncodeTool（编解码工具）、RegexTester（正则测试）、TextDiff（文本对比）、MarkdownPreview（Markdown 预览）、PasswordGen（密码生成）、UnitConverter（单位换算）、ClipboardHistory（剪贴板历史）、ScreenshotTool（截图工具）、QRCodeTool（二维码生成）、TodoPlanner（待办清单）、NotesPad（速记便笺）、NetworkToolkit（网络工具箱）。
- **主程序**：`ClassBox.csproj` 插件登记改为通配（`..\ClassBox.Plugins.*\*.csproj`），构建后自动把全部插件 DLL 复制到输出目录 `Plugins`，新增插件无需再改主程序工程文件。
- **重新打包**：程序与安装包版本号统一为 1.2.0（`ClassBox.exe` 单文件自包含、安装包 `ClassBox-Setup-1.2.0.exe`）。

### v1.1.0（2026-11-06）

- 主界面强制置顶与悬浮球（详见第 10 章）；`WindowService.StartTopmostGuard()` 每秒重申 `HWND_TOPMOST`。
- 自包含单文件发布模式：`SelfContained=true` + `PublishSingleFile=true` + 压缩，目标机器无需安装 .NET 运行时。

### v1.0.0（2026-11-05）

- 首个可分发版本：插件式框架（宿主 + `ClassBox.Abstractions` 契约）、内置「窗口置顶」与「人像监测」插件、Inno Setup 安装包。

*（内容由AI生成，仅供参考）*
