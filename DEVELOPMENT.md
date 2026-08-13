# 拾句（Daily Quote）开发文档

> 面向开发者的技术文档，覆盖技术栈、项目结构、构建打包、架构设计与已知问题。
> 最终用户的使用说明见 [README.md](./README.md)。

## 1. 技术栈与环境要求

| 项 | 说明 |
|---|---|
| 语言/框架 | C# 12、.NET 9 (`net9.0-windows`)、WPF (`UseWPF=true`) |
| 桌面集成 | 同时启用 `UseWindowsForms=true`（仅借用 `NotifyIcon`/`ContextMenuStrip` 做托盘，隐式 using 已移除避免与 WPF 类型冲突，见 `每日一句.csproj` 注释） |
| 外部数据 | 扇贝「每日一句」公开 API（HTTPS，无需鉴权） |
| 安装包 | Inno Setup 6.x（编译器放在 `.tools/InnoSetup/`，不入库） |
| 开发机 | Windows 10/11，需 .NET 9 SDK（`dotnet` 命令可用） |
| 运行权限 | 普通用户即可；装到 `Program Files` 时安装包会弹 UAC 提权 |

> **CS0104 同名类型陷阱**：`UseWPF + UseWindowsForms` 会隐式引入 `System.Windows.Forms` / `System.Drawing`，与 WPF 的 `Application/Brush/Color/Point` 等冲突。各 `.xaml.cs` 顶部都用 `using X = ...` 显式钉死到 WPF 版本（见 `WidgetWindow.xaml.cs`、`SettingsWindow.xaml.cs` 头部）。

## 2. 项目结构

```
仓库根目录（磁盘目录名：每日一句）
├── 每日一句.csproj              # 工程文件（WinExe / net9.0-windows / 单文件相关 Link 与 Resource 处理）
├── App.xaml / App.xaml.cs      # 应用入口：单实例、加载设置/语料、托盘、每日0点更新、崩溃日志
├── AssemblyInfo.cs
│
├── Models/
│   ├── AppSettings.cs          # 设置实体（主题/颜色/透明度/字号/刷新间隔/自启/位置记忆等）
│   └── Quote.cs                # 语料实体（English/Chinese/Author/Date/FetchedAt）
│
├── Services/                   # 业务逻辑层
│   ├── SettingsService.cs       # settings.json 读写（与语料分离）+ Changed 事件（UI 线程编组）
│   ├── QuoteService.cs          # 语料核心：种子化、今日句、随机句、抓取入库、统计
│   ├── ShanbayService.cs        # 扇贝 API 抓取（10s 超时、UA 头、字段兼容 string/dict）
│   └── TimerService.cs          # 分钟级倒计时封装（Elapsed 在非 UI 线程）
│
├── Native/                     # Win32 互操作（user32 P/Invoke）
│   ├── Autostart.cs            # 开机自启：HKCU\...\Run 键
│   ├── WorkerW.cs              # 桌面层挂载（挂到桌面图标之下的 WorkerW 层）+ 桌面刷新自愈监控
│   └── WindowExtensions.cs     # 点击穿透、位置记忆/还原、拖拽移动（SetWindowPos）、真实光标坐标
│
├── Views/                      # UI 层（窗口与界面相关代码）
│   ├── App.xaml(.cs)           # 应用程序入口（无 StartupUri，启动窗体在 App.xaml.cs 代码创建）；因位于 Views/，csproj 显式声明为 ApplicationDefinition 以生成 Main
│   ├── WidgetWindow.xaml(.cs)  # 主浮窗：透明渐变卡片、文字动画、拖拽、单击/双击、内联右键、刷新
│   ├── SettingsWindow.xaml(.cs)# 设置窗：外观/交互/刷新/系统/数据 五类，实时预览、单例
│   ├── ContextMenuWindow.xaml(.cs) # 浮窗右键菜单（独立顶层窗口，避免被主窗口边界裁切）
│   ├── ColorPickerWindow.xaml(.cs) # 自建 HSB 拾色器（1670 万色，无 WinForms ColorDialog）
│   ├── ThemeHelper.cs          # 系统深浅判定（注册表 AppsUseLightTheme）+ 主题画刷应用
│   └── TextAnimationEngine.cs  # 文字动画共享引擎（打字机/解密文本/文本生成效果/文本上浮；浮窗与设置预览共用）
│
├── Assets/
│   ├── app.ico                 # 应用图标（嵌入资源，供 exe/窗口/托盘使用）
│   └── corpus.json             # 内置种子语料（随发布落到输出根目录，首次运行灌入 data.json）
│
├── installer/
│   └── 拾句.iss                # Inno Setup 安装脚本（产物 setup.exe 不入库）
├── publish/                   # dotnet publish 输出（被 .gitignore 忽略，可重建）
├── .tools/InnoSetup/          # Inno Setup 编译器（被 .gitignore 忽略，不入库）
├── bin/ obj/                  # 构建中间产物（被 .gitignore 忽略）
├── archive/                  # 已归档的废弃/调查脚本与旧语料源（不参与构建，可随时整体删除）
└── .workbuddy/                # 项目记忆与开发笔记（overview_*.md 等，不入库）
```

> **不入库的文件**（见 `.gitignore`）：`bin/`、`obj/`、`.tools/`、`.workbuddy/`、`publish/`、`installer/*.exe`。
> 即 Git 仓库只保留源码 + `拾句.iss` 脚本 + 文档，编译器与发布产物本地生成即可。

## 3. 构建与运行

### 3.1 开发构建
在项目根目录执行：
```bash
dotnet build -c Debug       # 调试构建
dotnet run --project .      # 直接运行（F5 亦可）
```

### 3.2 调试要点
- 程序**无主窗口**，入口是托盘图标（通知区）。任务栏看不到主窗口是正常的（浮窗 `ShowInTaskbar=false`，挂在桌面层）。
- 崩溃会写日志到 `%APPDATA%\拾句\crash.log`（含 StackTrace）。功能异常（被 catch 的）写同一文件前缀 `[WARN]`。
- 数据目录：`%APPDATA%\拾句\`（含 `data.json` 语料、`settings.json` 设置）。
- 本机已有运行进程会锁住 `bin`，导致 `dotnet build` 复制 exe 失败（MSB3027）。**先退出旧进程再构建**，或 `dotnet build -o <临时目录>` 绕开。

## 4. 打包与发布

> 目标：产出一个**自包含、可独立运行、体积小（约 48MB）** 的安装包。体积控制是本项目打包的关键难点。

### 4.1 自包含单文件发布
```bash
dotnet publish -c Release -r win-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -o publish
```
- 产物：`publish/拾句.exe`（约 155MB，已内置 .NET 9 运行时）。
- 同目录带 5 个 WPF 原生 DLL（`wpfgfx_cor3.dll` 等，须与 exe 同目录）和 `corpus.json`。

### 4.2 用 Inno Setup 压缩成安装包
```bash
.tools/InnoSetup/ISCC.exe installer/拾句.iss
```
- 输出：`installer/拾句_setup.exe`（约 48MB，LZMA 压缩）。
- 安装特性：可选安装目录（默认 `%LOCALAPPDATA%\Programs\拾句`，选 `Program Files` 会弹 UAC）、桌面快捷方式、开机自启（HKCU Run 键）、标准卸载程序。

### 4.3 体积为什么是 48MB（而不是 96MB 或 1MB）
| 情形 | 体积 | 原因 |
|---|---|---|
| 框架依赖发布（漏加 `SelfContained`） | ~1MB | 只打包自己代码，运行时没打进去，换机器跑不了 |
| 自包含 + 整目录通配打包 | ~96MB | `publish/` 里混入 `win-x64/`（171MB 框架依赖副本）冗余 |
| **本项目做法（正确）** | **~48MB** | 自包含 155MB − 剔除冗余 − LZMA 压缩 |

**关键坑：剔除冗余目录**。`dotnet publish` 会在 `publish/` 下生成 `win-x64/`（完整框架依赖副本）等子目录，但单文件 exe 已自带全部内容——这些是多余的。安装脚本**不能**用 `Source: "{#SourceDir}\*"` 通配整目录（会把冗余全打进去 → 96MB），而是**显式只列根目录需要的文件**：
```ini
[Files]
Source: "{#SourceDir}\拾句.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\corpus.json";  DestDir: "{app}"; Flags: ignoreversion
```
> `*.dll` 通配安全（根目录只有那 5 个 WPF 原生 DLL）；**绝不能**通配 `*` 把 `win-x64/` 等子目录收进来。

### 4.4 安装脚本要点（`installer/拾句.iss`）
- `SourceDir=..\publish`：发布目录相对脚本上一级（即 `每日一句/publish/`）。
- `DefaultDirName={%LOCALAPPDATA}\Programs\{#MyAppName}`：默认安全路径（普通用户可写，无需管理员）。
- `PrivilegesRequired=admin`：选 `Program Files` 等受保护目录时弹 UAC 提权（不要设成 `lowest`，否则写入被拒 → Error 5 拒绝访问）。
- `Compression=lzma2/ultra64` + `SolidCompression=yes`：压到 ~48MB。
- `AppId`：固定 GUID，避免重复安装项。
- `SetupIconFile` 已省略：指向 155MB 的 exe 会报 `File too large`。
- 语言向导用默认英文（未内置中文 `.isl`）。

### 4.5 编译器获取
`.tools/InnoSetup/` 不存在时，从 GitHub Release（jrsoftware/innosetup）下载 `innosetup-6.x.x.exe`，**静默释放**到 `每日一句/.tools/InnoSetup/`：
```bash
innosetup-6.x.x.exe /VERYSILENT /SUPPRESSMSGBOXES /DIR="每日一句/.tools/InnoSetup" /NORESTART
```
> 不用系统级安装、不污染工程根、不弹 UAC——完全在项目目录内完成。

### 4.6 复现命令（一句话版）
```
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o publish
.tools/InnoSetup/ISCC.exe installer/拾句.iss
```

## 5. 架构与数据流

### 5.1 启动流程（`App.OnStartup`）
```
单实例检查(Mutex "拾句_SingleInstance")
  → 确保 %APPDATA%/拾句 目录
  → 加载 settings.json（无则迁移旧 data.json 的 Settings 节点）
  → 加载今日句（无则后台 FetchAsync 补抓）
  → 同步开机自启状态（Autostart.Set）
  → 显示 WidgetWindow 浮窗
  → 建系统托盘图标（双击/右键「打开设置」「退出」）
  → 启动每日 0 点自动更新定时器（一次性，每次重算 00:00，抗休眠漂移）
```

### 5.2 两个数据文件分离（重要设计）
| 文件 | 内容 | 说明 |
|---|---|---|
| `data.json` | `{ TodayQuote, Quotes }` | **仅语料**。首次运行把 `Assets/corpus.json`（~3600 条）灌入；之后抓取/更新/随机都读写它 |
| `settings.json` | `AppSettings` 全部字段 | **仅设置状态**。保存设置时只写这个文件，不再重写整个语料，避免写放大 |

> 历史坑：早期 `data.json` 混存设置，每次保存设置都重写整份语料（含 3598 条）→ 写放大 1500×。已拆分为独立 `settings.json`（`SettingsService` + `QuoteDataFile` 去掉 `Settings`）。启动时若 `settings.json` 不存在，会从旧 `data.json` 的 `Settings` 节点一次性迁移。

### 5.3 抓取流程（每日句来源）
```
WidgetWindow 刷新 / 设置页「立即更新」
  → QuoteService.FetchAsync / UpdateTodayAsync
      → EnsureSeededAsync（先确保内置语料已灌入，否则首次抓取的 1 条会永久挡住种子化）
      → ShanbayService.FetchTodayAsync（10s 超时，容错返回 null）
      → 写 data.json（TodayQuote=新句；Quotes 按 Date 去重 Insert(0)）
      → 更新 Current / 浮窗 RenderQuote
```
- API：`https://apiv3.shanbay.com/weapps/dailyquote/quote/?date=YYYY-MM-DD`
- 字段兼容：`content`/`english`、`translation`、`author` 可能为 string 或 dict，代码做了兼容提取。
- 失败优雅降级：返回占位句/保留本地语料，仅写 `[WARN]` 日志，不抛异常、不卡 UI。

### 5.4 桌面层挂载（WorkerW）
- `WidgetWindow.OnWindowLoaded` 调用 `WorkerW.Attach(this)`，把浮窗挂到桌面图标**之下**的 WorkerW 层（经典桌面 Widget 手法），沉到壁纸层、不抢焦点。
- 挂载失败有自检回滚（变回普通顶层窗口），不会"存在但看不见"。
- 桌面刷新/换壁纸/explorer 重启会导致 WorkerW 失效，`WorkerW` 内置 **2 秒监控自愈**重新挂载。
- 挂在 WorkerW 后窗口是子窗口，拖拽用 `WindowExtensions.MoveBy`（SetWindowPos）而非 WPF `DragMove()`（后者会抛 InvalidOperationException）。

### 5.5 主题与深浅模式
- 真相源：`ThemeHelper.IsSystemDark()` 读注册表 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（0=深，1=浅）。
- `AppSettings.Theme = light/dark/system`；`system` 时监听 `SystemEvents.UserPreferenceChanged` 实时跟随系统切换。
- 设置页与拾色器共用 `ThemeHelper.ApplyTo` 的 10 个 DynamicResource 调色板，保证同款换肤。

### 5.6 单实例与托盘
- 单实例：命名 Mutex `拾句_SingleInstance`，已运行则 `Shutdown()`。
- 设置窗单例：`WidgetWindow.OpenSettings()` 复用同一个 `SettingsWindow`（关闭走 `Hide()` 非 `Close()`，再次打开重新 `Show`/`Activate`）。托盘菜单与浮窗右键共用此单例，避免开两个窗口。
- 退出：`App.OnExit` 释放托盘图标（`Visible=false` + `Dispose`，避免幽灵图标）、停定时器、释放 Mutex。

## 6. 数据模型与文件格式

**`data.json`**（`%APPDATA%\拾句\`，仅语料）
```json
{
  "TodayQuote": { "English": "...", "Chinese": "...", "Author": "...", "Date": "2026-08-11", "FetchedAt": "..." },
  "Quotes": [ { "English": "...", "Chinese": "...", "Author": "...", "Date": "2026-08-11", "FetchedAt": "..." } ]
}
```

**`settings.json`**（`%APPDATA%\拾句\`，仅设置）
```json
{
  "Theme": "light",
  "ColorText": "#FFFFFF",
  "Opacity": 55,
  "FontFamily": "system",
  "FontSize": 18,
  "BackgroundColor": "",
  "TextAnimationEnabled": true,
  "TextAnimationEffect": "打字机",
  "ClickAction": "random",
  "DoubleAction": "settings",
  "RefreshInterval": 1440,
  "Autostart": true,
  "LockPosition": false,
  "WidgetLeft": "NaN",
  "WidgetTop": "NaN"
}
```
> 序列化选项（`DataStore.JsonOptions`）：缩进 + `AllowNamedFloatingPointLiterals`（允许 `NaN` 字面量，否则 `WidgetLeft/Top` 默认 `double.NaN` 序列化抛错）+ `UnsafeRelaxedJsonEscaping`（中文不转义，方便肉眼查看）。

**`Assets/corpus.json`**（内置种子，发布时落到 exe 同目录）
```json
[ { "English": "...", "Chinese": "...", "Author": "...", "Date": "...", "FetchedAt": null } ]
```
> 字段名与 `Quote` 不同（`FetchedAt` 可空），故有独立 `CorpusEntry` 模型转换。

## 7. 外部依赖

| 依赖 | 类型 | 说明 |
|---|---|---|
| 扇贝每日一句 API | HTTPS GET | `https://apiv3.shanbay.com/weapps/dailyquote/quote/?date=YYYY-MM-DD`，无需鉴权，10s 超时，失败静默降级 |
| .NET 9 运行时 | 自包含 | 已打进 exe，目标机无需安装 |
| Inno Setup 6.x | 本地工具 | 仅打包机需要，放 `.tools/InnoSetup/` |

> 没有任何需要登录/密钥/第三方服务的强依赖。断网时程序退化为显示本地语料 + 占位句。

## 8. 关键设计决策与已知问题

1. **IL3000 警告（`Native/Autostart.cs`，已修复）**：单文件模式下 `Assembly.Location` 返回空串。自启路径已改为只用 `Environment.ProcessPath`，删除两处 `.Location` 兜底分支后 `dotnet publish` 不再报 IL3000；若 `ProcessPath` 为空则跳过写注册表，不影响其它功能。
2. **`publish/win-x64/` 冗余导致安装包膨胀到 96MB**：见 §4.3，安装脚本必须显式列文件，勿通配整目录。
3. **`double.NaN` 序列化历史 bug（已修）**：曾因 `JsonOptions` 缺 `AllowNamedFloatingPointLiterals`，导致所有写盘静默失败。新增/重命名字段务必同步 WidgetWindow / SettingsWindow / ThemeHelper。
4. **冻结画刷**：`Brushes.White` 等是冻结画刷，改 `.Opacity` 前需 `Clone()`，否则崩启动。
5. **WPF + WinForms 同名类型**：各 `.xaml.cs` 顶部用 `using X = ...` 钉死 WPF 版本，新增 using 时注意 CS0104。
6. **坐标体系**：挂在 WorkerW 后 WPF `PointToScreen` 参考系错误，光标/位置统一走 `WindowExtensions.GetCursorPosDip`（Win32 `GetCursorPos` + DPI 换算）；位置用 `GetWindowRect`（物理像素）→ DIP 保存。
7. **XAML 加载期事件**：`SettingsWindow` 构造函数 `InitializeComponent()` 前 `_loading=true`，事件处理器首行 `if(_loading) return;`，避免字段未实例化时空引用。
8. **托盘右键菜单不跟随 WPF 深浅主题**（WinForms 经典样式，深色下白底菜单）——如需换肤需自绘。

## 9. 测试与验证

以**静态审计 + 本机（Windows 真机）实跑**为主，无独立测试报告文件：
- 浮窗：显示/透明度/右键/今日句/拖拽/位置记忆/自启/单双击/刷新
- 设置窗：实时预览/保存落盘/失败提示/自启回读/字号色宽/单例/动画预览即时生效
- 后端/原生：NaN 序列化/事件链/抓取超时/本地优先/注册表自启/HttpClient 头

验证方式：静态审计为主 + 后端 console 子项目实测（测完删除）+ 真实 STA+WPF GUI 探针 + 启动真实 exe 看 `crash.log`。

> 需本机（Windows 真机，非沙箱）确认项：四种文字动画的视觉观感、联网抓取、开机自启重启效果、中文路径下读写。

## 10. 协作与开发流程

- **工作区边界**：所有工作文件位于本仓库根目录内；`installer/`、`publish/`、`.tools/` 均为仓库内目录，不向仓库外散落文件。
- **版本控制**：改动先沟通确认再提交/推送，不自动推送远程。
- **构建验证**：`dotnet build`（以本机为准）；`bin` 被进程锁住时用 `dotnet build -o <tmp>` 复验；独立测试工程须置于主工程目录之外（SDK 风格 `**/*.cs` 会误并入导致多入口点编译失败）。
- **跨模块契约（保持一致）**：事件 `SettingsService.Changed` 为 `event EventHandler`；文字颜色统一 `AppSettings.ColorText`；换肤真相源 `ThemeHelper.IsSystemDark()`；拾色器 `ColorPickerWindow.Show(owner, initialHex) → string?`；序列化共用 `DataStore.JsonOptions`。

## 11. 本地环境准备

- [ ] 安装 .NET 9 SDK，`dotnet --version` 正常
- [ ] 打开仓库根目录
- [ ] `dotnet build -c Debug` 通过（若 MSB3027 失败，先退出旧进程）
- [ ] `dotnet run` 看到桌面浮窗 + 托盘图标
- [ ] 改一行代码 → 重新构建 → 验证行为变化
- [ ] 想打包：`dotnet publish ... -o publish` → `ISCC.exe installer/拾句.iss` → 验证 `installer/拾句_setup.exe` 约 48MB
