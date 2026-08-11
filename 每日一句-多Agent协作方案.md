# 每日一句 · 多 Agent 协作方案（v2）

> ⚠️ 本文档为**重构版**：原 v2 文档未留存于任何 git 仓库或本地磁盘，内容依据 `2026-08-09` / `2026-08-10` 工作日志中的多 Agent 协作实践还原，作为本工作区协作开发的基准。
> 适用范围：C# WPF（.NET 9）阶段的协作开发。Tauri v1 的 `每日一句桌面插件-开发规划.md`（v1.1）仅作历史参考，已作废。

---

## 0. 角色定义

| 角色 | 职责 |
|---|---|
| **Lead（主智能体 / 你）** | 三重角色 = **架构师 + 协调者 + 集成者**。**无独立 PM**。负责定契约、拆子任务、派发、汇总、集成验证、构建回归。 |
| **文件归属 Agent（实现）** | 每个 Agent 负责一组**独占文件**，并行编写/修改，互不冲突。 |
| **专职 QA+Fix Agent（验证兜底）** | 全项目访问权，独立做静态审计 + `dotnet build` 验证 + 回归清单，并可直接修真 bug。bug 修复阶段统一兜底，而非按开发期文件归属分散修。 |
| **测试子 Agent（可选）** | general-purpose 后台，只做审计 + 构建验证，**不修改文件**。 |

---

## 1. 标准协作波次

1. **规划 / 拆子任务**：Lead 读需求 → 定契约（接口/字段/事件签名/命名约定）→ 拆成可并行的子任务，每个子任务绑定到一组独占文件。
2. **并行分派**：同时派出多个文件归属 Agent（如 `WidgetWindow` / `SettingsWindow` / `Services×N` / `Native×N`），各写自己的文件组。
3. **收口**：指定一个 Agent（或 Lead）做接口对齐 / UI 收口（如设置页由 B 收口）。
4. **QA 独立验证**：专职 QA+Fix Agent 全量审计 + 真编译（`dotnet build -o <tmp>` 绕过运行进程锁）+ 回归清单，修掉真 bug。
5. **Lead 集成**：汇总、统一构建回归、出验证报告。

> 实例：设置页大改造 = Lead 定契约 → 并行 A(拾色器)/C(后端)/D(浮窗) → B 收口设置页 → QA 9/9 审计。

---

## 2. 跨 Agent 契约（先用文字定清，再写码）

- 事件签名：`SettingsService.Changed` = `event EventHandler`（**非** `Action`）。
- 属性访问：`QuoteService.Current` 私有 `set`。
- 数据格式：`data.json` 字段 PascalCase（`Settings/TodayQuote/Quotes`）；设置已迁出为独立 `settings.json`。
- 颜色：`AppSettings.ColorText`（三色合一）；背景 `BackgroundColor`（空 = 主题渐变）。
- 换肤真相源：`ThemeHelper.IsSystemDark()`（注册表 `AppsUseLightTheme`）。
- 拾色器：`ColorPickerWindow.Show(owner, initialHex)→string?`，HSB 1670 万色，无色板，无 WinForms `ColorDialog`。
- 序列化：`DataStore.JsonOptions`（缩进 / `AllowNamedFloatingPointLiterals` 允许 NaN 字面量 / `UnsafeRelaxedJsonEscaping` 中文不转义）。

---

## 3. 铁律（用户惯例）

1. **先汇报、确认后再提交/推送**：任何 `git commit` / `git push` / 部署必须等用户明确确认；**绝不自动推送**。
2. **多 Agent 协作贯穿 bug 修复**：修 bug 派给文件归属 Agent，验证派 QA Agent，不闷修。
3. **每步先汇报计划、确认后再执行**（尤其启动多 Agent 协作前）。
4. 构建验证：`dotnet build`（本机无沙箱为准）；运行进程锁 `bin` 时用 `dotnet build -o <tmp>` 复验；独立测试工程放主工程目录**外**（SDK 风格 `**/*.cs` 会误并入导致"多入口点"编译失败）。

---

## 4. 启动一次协作（模板）

对任务「〈具体任务〉」：
- **规划 Agent** 拆分子任务 → **并行分派** 实现 / 测试 / 文档 子 Agent → **Lead 汇总**并做集成验证。
- 每步先汇报计划，确认后再执行。

## 5. 续做某功能（模板）

- 基于 `overview_*.md` 的设计实现〈功能〉，完成后同步更新 `.workbuddy/memory/` 记忆。

---

## 6. 已沉淀的流程经验（来自日志）

- GUI 交互失效优先查"统一写盘 / 事件链"而非单点 handler（曾因 `JsonOptions` 缺 `AllowNamedFloatingPointLiterals` 致 `double.NaN` 序列化抛错被静默吞掉，全写盘失败）。
- XAML 加载期事件空引用：构造函数 `InitializeComponent()` 前 `_loading=true`，每个事件处理器首行 `if (_loading) return;`。
- 冻结画刷（`Brushes.White` 等）改 `.Opacity` 前需 `Clone()`，否则崩启动。
- 沙箱回收站不可靠 → 用同盘 `shutil.move` 归档代替；清理严格走"扫描→选力度→可逆搬迁→确认回收"。
