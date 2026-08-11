using System.IO;
using System.Windows;
using 每日一句.Models;
using 每日一句.Services;
using WinForms = System.Windows.Forms;

namespace 每日一句;

/// <summary>
/// 应用入口（Phase 2 集成版）。
/// 启动流程：单实例检查 → 加载设置 → 加载今日句 → 显示浮窗。
/// </summary>
public partial class App : Application
{
    // ===== 全局实例（各 Agent 直接引用）=====
    public static AppSettings CurrentSettings { get; set; } = new();
    public static Quote CurrentQuote { get; set; } = new();

    /// <summary>数据目录：%APPDATA%/每日一句</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "每日一句");

    /// <summary>数据文件：data.json（仅语料：今日句 + 语料库）</summary>
    public static string DataFile => Path.Combine(DataDir, "data.json");

    /// <summary>设置文件：settings.json（仅设置状态，与语料文件分离，保存设置时不再重写整个语料）</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    private Mutex? _mutex;
    private bool _ownsMutex;
    private const string MutexName = "每日一句_SingleInstance";

    /// <summary>系统托盘图标（通知区）。程序无主窗口，托盘是任务栏图标的合理出口。</summary>
    private WinForms.NotifyIcon? _notifyIcon;

    /// <summary>每日 0 点自动更新定时器（一次性触发，每次触发后重新对准下一个 00:00）。</summary>
    private System.Timers.Timer? _dailyTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：所有未捕获异常写入 crash.log（含 StackTrace），便于定位
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.Handled = false; // 仍按默认崩溃，但先留档
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception);
        };

        base.OnStartup(e);

        // async void：任何逸出的异常都会直接终结进程，故整体兜底
        try
        {
            // 单实例：已有一个实例则退出
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            // 确保数据目录存在
            try { Directory.CreateDirectory(DataDir); } catch (Exception ex) { LogWarn(ex); }

            // 加载设置 + 今日句（失败静默，保留默认值）
            try { CurrentSettings = await SettingsService.LoadAsync(); } catch (Exception ex) { LogWarn(ex); }

            bool hasTodayQuote = false;
            try
            {
                var today = await QuoteService.GetTodayAsync();
                if (today != null)
                {
                    CurrentQuote = today;
                    hasTodayQuote = today.Date == DateTime.Now.ToString("yyyy-MM-dd");
                }
            }
            catch (Exception ex) { LogWarn(ex); }

            // 首次开机/首次打开：本地没有当天语录时后台补抓一次。
            // 用弃元不 await，避免网络耗时拖慢启动；FetchAsync 内部已自带兜底与日志。
            if (!hasTodayQuote) _ = QuoteService.FetchAsync(false);

            // 同步开机自启状态（设置与注册表一致）
            try { Native.Autostart.Set(CurrentSettings.Autostart); } catch (Exception ex) { LogWarn(ex); }

            // 显示浮窗
            var widget = new WidgetWindow();
            widget.Show();

            // 系统托盘图标（通知区）：本程序无主窗口，托盘是任务栏图标的合理出口
            SetupTrayIcon();

            // 常驻期间每天 0 点自动更新
            StartDailyUpdateTimer();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            // 浮窗是唯一入口，创建失败则没有任何 UI，直接退出而不是留下僵尸进程
            try { Shutdown(); } catch { }
        }
    }

    /// <summary>
    /// 启动"每日 0 点更新"定时器。
    /// 用一次性（AutoReset=false）定时器对准下一个 00:00，触发后重新计算并对准，
    /// 而不是简单挂 24h 周期——这样系统休眠唤醒、改系统时间后也不会持续漂移。
    /// </summary>
    private void StartDailyUpdateTimer()
    {
        try
        {
            _dailyTimer = new System.Timers.Timer { AutoReset = false };
            _dailyTimer.Elapsed += async (_, _) =>
            {
                // Elapsed 在线程池线程触发；网络失败静默，仅刷新数据不动 UI
                try { await QuoteService.FetchAsync(false); }
                catch (Exception ex) { LogWarn(ex); }
                finally { ScheduleNextMidnight(); }
            };
            ScheduleNextMidnight();
        }
        catch (Exception ex) { LogWarn(ex); }
    }

    /// <summary>把定时器的到期时间设为距下一个 00:00 的毫秒数（下限 1 秒，防止 0/负间隔）。</summary>
    private void ScheduleNextMidnight()
    {
        if (_dailyTimer == null) return;
        try
        {
            double ms = (DateTime.Today.AddDays(1) - DateTime.Now).TotalMilliseconds;
            _dailyTimer.Interval = ms < 1000 ? 1000 : ms;
            _dailyTimer.Start();
        }
        catch (Exception ex) { LogWarn(ex); }
    }

    /// <summary>
    /// 创建系统托盘图标（通知区）。本程序无传统主窗口，托盘是用户在任务栏区
    /// 识别/操作程序的主要入口：左键双击或右键「打开设置」→ 设置窗口；
    /// 右键「退出」→ 关闭程序。图标直接从已嵌入 exe 的资源提取，无需额外文件。
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            var icon = string.IsNullOrEmpty(exePath)
                ? null
                : System.Drawing.Icon.ExtractAssociatedIcon(exePath);

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = "每日一句",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            var openItem = new WinForms.ToolStripMenuItem("打开设置");
            openItem.Click += (_, _) => OpenSettings();
            var exitItem = new WinForms.ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => Shutdown();
            menu.Items.Add(openItem);
            menu.Items.Add(exitItem);
            _notifyIcon.ContextMenuStrip = menu;

            _notifyIcon.DoubleClick += (_, _) => OpenSettings();
        }
        catch (Exception ex)
        {
            LogWarn(ex);
        }
    }

    /// <summary>
    /// 打开设置窗口。直接复用 WidgetWindow 的单例入口，而不是自己遍历 Windows：
    /// 设置窗口的关闭按钮走的是 Hide() 而非 Close()，只 Activate() 隐藏窗口不会让它重新出现；
    /// 且各自新建实例会与浮窗右键菜单开出的那个窗口重复。
    /// </summary>
    private void OpenSettings() => WidgetWindow.OpenSettings();

    protected override void OnExit(ExitEventArgs e)
    {
        // 释放系统托盘图标，避免退出后通知区残留幽灵图标
        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch { }

        // 释放每日定时器，避免退出后残留线程池回调
        try
        {
            _dailyTimer?.Stop();
            _dailyTimer?.Dispose();
            _dailyTimer = null;
        }
        catch { }

        // 仅当本线程真正拥有 Mutex 时才释放，否则 ReleaseMutex 会抛异常
        try
        {
            if (_ownsMutex) _mutex?.ReleaseMutex();
        }
        catch { }
        base.OnExit(e);
    }

    /// <summary>崩溃级：未捕获异常或会让功能整体不可用的意外错误。</summary>
    internal static void LogCrash(Exception? ex) => WriteLog("CRASH", ex);

    /// <summary>
    /// 警告级：已被 catch 处理、程序可继续运行的错误（如网络不可达、剪贴板占用）。
    /// 原来这些位置是空 catch，故障完全无痕迹，排查只能靠猜。
    /// </summary>
    internal static void LogWarn(Exception? ex) => WriteLog("WARN", ex);

    /// <summary>写入 %APPDATA%/每日一句/crash.log（追加）。</summary>
    private static void WriteLog(string level, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{level}] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n";
            File.AppendAllText(Path.Combine(DataDir, "crash.log"), line);
        }
        catch { }
    }
}
