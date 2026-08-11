using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace 每日一句.Native;

/// <summary>
/// 桌面层挂载：把 WPF 窗口通过经典 WorkerW 手法挂载到桌面图标之下（WorkerW 层）。
/// 流程：FindWindow("Progman") → SendMessageTimeout(0x052C) 创建 WorkerW →
/// 枚举窗口找「类名 WorkerW 且不是 Progman 直接子窗口」的目标 → SetParent →
/// WS_CHILD|WS_VISIBLE → SetWindowPos 贴底并移到桌面工作区位置。
/// </summary>
public static class WorkerW
{
    // ===== Win32 常量 =====
    private const uint GW_CHILD = 5;
    private const uint GW_HWNDNEXT = 2;
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int HWND_BOTTOM = 1;

    // ===== 内部辅助常量 =====
    private const uint SMTO_NORMAL = 0x0000;
    private const uint WM_SPAWN_WORKERW = 0x052C;
    private static readonly IntPtr SpawnWorkerWParam = (IntPtr)0xD;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint GA_PARENT = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // ===== P/Invoke（user32.dll）=====
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // ===== 挂载状态 =====
    private static bool _attached;
    private static Window? _attachedWindow;
    private static IntPtr _attachedHwnd;
    private static IntPtr _targetWorkerW;
    private static long _originalStyle;
    private static DispatcherTimer? _watchTimer;

    private static void Dbg(string s)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerw_attach.log"),
                System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\n");
        }
        catch { }
    }

    /// <summary>挂载窗口到桌面层（WorkerW，图标之下）。成功返回 true，失败返回 false（不抛异常）。</summary>
    public static bool Attach(System.Windows.Window w)
    {
        try
        {
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "workerw_attach.log"), ""); } catch { }
            if (w == null) return false;

            var helper = new WindowInteropHelper(w);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) hwnd = helper.EnsureHandle();
            if (hwnd == IntPtr.Zero) return false;

            // 1) 找到 Progman（桌面窗口）
            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return false;

            // 2) 通知 Progman 创建 WorkerW（若尚未创建）
            SendMessageTimeout(progman, WM_SPAWN_WORKERW, SpawnWorkerWParam, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

            // 3) 找到目标 WorkerW：类名 WorkerW 且不是 Progman 的直接子窗口
            IntPtr workerW = FindWorkerW(progman);
            Dbg($"progman={progman} workerW={workerW}");
            if (workerW == IntPtr.Zero)
            {
                // 少数环境（部分 Win11 内部版本 / 第三方壳）对 wParam=0xD 不响应，
                // 用 wParam=0 再触发一次再找。两次都失败才放弃。
                SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
                workerW = FindWorkerW(progman);
            }
            if (workerW == IntPtr.Zero) return false;

            // 4) 挂到 WorkerW 之下
            SetParent(hwnd, workerW);
            Dbg($"after SetParent GetParent={GetParent(hwnd)} target={workerW}");
            if (GetParent(hwnd) != workerW) return false;

            // 5) 记录原始样式，再追加 WS_CHILD | WS_VISIBLE。
            //    只在"首次挂载该窗口"时记录：重新挂载时读到的已是子窗口样式，
            //    若每次都覆盖，Detach 还原的就不再是真正的原始样式。
            long style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
            if (_originalStyle == 0 || _attachedHwnd != hwnd)
                _originalStyle = style & ~((long)WS_CHILD);
            // WPF 无边框透明窗口默认带 WS_POPUP，WS_POPUP|WS_CHILD 非法会导致窗口不显示、
            // 可见性自检误判回滚。挂载前必须先剥掉 WS_POPUP 再追加 WS_CHILD。
            style = (style & ~((long)WS_POPUP)) | WS_CHILD | WS_VISIBLE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

            // 6) 贴底显示，并把窗口移到桌面工作区位置（WPF 坐标是 DIP，先换算成物理像素）
            PositionOnDesktop(hwnd, w);

            // 7) 自检：挂载后必须可见（父 WorkerW 可见）。若仍不可见，回滚为普通顶层窗口，
            //    避免"窗口存在但看不见"的隐身态。
            Dbg($"before selfcheck IsWindowVisible={IsWindowVisible(hwnd)} style=0x{(long)GetWindowLongPtr(hwnd, GWL_STYLE):X}");
            if (!IsWindowVisible(hwnd))
            {
                if (_originalStyle != 0)
                    SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(_originalStyle));
                SetParent(hwnd, IntPtr.Zero);
                _originalStyle = 0;
                // 清空目标，让监控在下个 tick 判定为"需要重挂"而不是误以为还挂着
                _targetWorkerW = IntPtr.Zero;
                return false;
            }

            _attached = true;
            _attachedWindow = w;
            _attachedHwnd = hwnd;
            Dbg($"ATTACH OK targetWorkerW={_targetWorkerW}");
            _targetWorkerW = workerW;

            try { StartWatch(w); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解除挂载：还原窗口样式，从 WorkerW 挂回普通窗口。</summary>
    public static void Detach(System.Windows.Window w)
    {
        try
        {
            StopWatch();
            if (w == null) return;

            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero && _attached)
            {
                if (_originalStyle != 0)
                    SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(_originalStyle));
                SetParent(hwnd, IntPtr.Zero);
            }

            _attached = false;
            _attachedWindow = null;
            _attachedHwnd = IntPtr.Zero;
            _targetWorkerW = IntPtr.Zero;
            _originalStyle = 0;
        }
        catch
        {
        }
    }

    /// <summary>用 SetWindowPos 把窗口贴底并定位到桌面工作区坐标。</summary>
    private static void PositionOnDesktop(IntPtr hwnd, Window w)
    {
        // WPF 的 Left/Top/Width/Height 是 DIP，HWND 坐标是物理像素，先换算
        double sx = 1.0, sy = 1.0;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } src)
        {
            var t = src.CompositionTarget.TransformToDevice;
            sx = t.M11;
            sy = t.M22;
        }

        uint flags = SWP_SHOWWINDOW | SWP_NOACTIVATE;
        int x = 0, y = 0, cx = 0, cy = 0;

        if (double.IsNaN(w.Left) || double.IsNaN(w.Top)) flags |= SWP_NOMOVE;
        else { x = (int)Math.Round(w.Left * sx); y = (int)Math.Round(w.Top * sy); }

        if (double.IsNaN(w.Width) || double.IsNaN(w.Height)) flags |= SWP_NOSIZE;
        else { cx = Math.Max(1, (int)Math.Round(w.Width * sx)); cy = Math.Max(1, (int)Math.Round(w.Height * sy)); }

        SetWindowPos(hwnd, new IntPtr(HWND_BOTTOM), x, y, cx, cy, flags);
    }

    /// <summary>
    /// 查找目标 WorkerW：类名 "WorkerW"、可见、且不是 Progman 的直接子窗口。
    /// 排除两类错误目标：
    /// 1) Progman 的直接子 WorkerW —— 那是图标层（挂上去会盖住图标/事件被图标层吸走）；
    /// 2) 不可见的幽灵 WorkerW —— Win11 桌面刷新会留下大量 135×37 的残留窗口，
    ///    挂上去会导致窗口不可见（父窗口不可见）。真正要挂的是可见的壁纸 WorkerW（铺满屏幕）。
    /// 3) 承载 SHELLDLL_DefView 的顶层 WorkerW —— 那同样是图标层。Win10 与部分 Win11 布局下
    ///    图标层是"顶层 WorkerW"而非 Progman 子窗口，仅靠 GetParent 判断会漏掉，
    ///    且它在 Z 序上排在壁纸 WorkerW 之前，会被先枚举到而选错。
    /// </summary>
    private static IntPtr FindWorkerW(IntPtr progman)
    {
        // 先按严格条件找：可见、非 Progman 直接子窗口、且不承载 SHELLDLL_DefView（图标层）。
        // 若严格条件一无所获（部分 Win11 布局下壁纸 WorkerW 也恰好承载 DefView），
        // 退回"仅可见且非 Progman 子窗口"的宽松条件，避免完全挂不上而回滚成普通窗口。
        IntPtr strict = IntPtr.Zero;
        IntPtr relaxed = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (!IsClass(top, "WorkerW")) return true;
            if (!IsWindowVisible(top)) return true;        // 排除幽灵 WorkerW（不可见）
            if (GetParent(top) == progman) return true;   // 排除 Progman 直接子窗口（图标层）
            if (relaxed == IntPtr.Zero) relaxed = top;    // 宽松候补
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                strict = top;                              // 严格命中（非图标层）
                return false;
            }
            return true;
        }, IntPtr.Zero);
        Dbg($"FindWorkerW strict={strict} relaxed={relaxed}");
        return strict != IntPtr.Zero ? strict : relaxed;
    }

    private static bool IsClass(IntPtr hwnd, string className)
    {
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 && sb.ToString() == className;
    }

    // ===== 桌面刷新监控（每 2 秒检测一次，WorkerW 失效则重新挂载）=====
    private static void StartWatch(Window w)
    {
        StopWatch();
        var timer = new DispatcherTimer(DispatcherPriority.Background, w.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        timer.Tick += OnWatchTick;
        _watchTimer = timer;
        timer.Start();
    }

    private static void StopWatch()
    {
        if (_watchTimer != null)
        {
            _watchTimer.Stop();
            _watchTimer.Tick -= OnWatchTick;
            _watchTimer = null;
        }
    }

    private static void OnWatchTick(object? sender, EventArgs e)
    {
        try
        {
            Window? w = _attachedWindow;
            IntPtr hwnd = _attachedHwnd;
            if (w == null || hwnd == IntPtr.Zero || !_attached)
            {
                StopWatch();
                return;
            }

            // 窗口已关闭 → 停止监控
            if (!w.IsLoaded && !w.IsVisible)
            {
                StopWatch();
                return;
            }

            // 句柄已销毁（窗口真的没了）→ 收摊，绝不能去 EnsureHandle 复活一个僵尸窗口，
            // 否则会变成每 2 秒重试一次的无限循环。
            if (!IsWindow(hwnd))
            {
                _attached = false;
                StopWatch();
                return;
            }

            // 目标 WorkerW 已消失/变不可见（桌面刷新、切壁纸、explorer 重启），
            // 或窗口的父窗口已不是目标 → 重新挂载
            if (_targetWorkerW == IntPtr.Zero
                || !IsWindow(_targetWorkerW)
                || !IsWindowVisible(_targetWorkerW)
                || GetAncestor(hwnd, GA_PARENT) != _targetWorkerW)
            {
                Reattach(w);
            }
        }
        catch
        {
        }
    }

    private static void Reattach(Window w)
    {
        try
        {
            // 不预先 StopWatch：Attach 成功时会自行重启监控；失败时旧监控仍在跑，
            // 下个 tick 继续重试。否则一次重挂失败就会永久失去监控（桌面恢复后再也挂不回去）。
            if (Attach(w)) return;
            if (_watchTimer == null) StartWatch(w);
        }
        catch
        {
        }
    }
}
