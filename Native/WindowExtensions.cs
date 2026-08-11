using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using 每日一句;

namespace 每日一句.Native;

/// <summary>
/// 窗口扩展：点击穿透与位置记忆。
/// </summary>
public static class WindowExtensions
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    /// <summary>
    /// 取真实光标屏幕位置（DIP）。窗口挂在 WorkerW 当桌面子窗口时，WPF 的
    /// PointToScreen 基于错误的坐标参考系，返回的坐标会偏到很远；改用 Win32
    /// GetCursorPos 直接读真实屏幕光标，再按 DPI 转 DIP。
    /// </summary>
    public static Point GetCursorPosDip(System.Windows.Window w)
    {
        try
        {
            if (!GetCursorPos(out POINT p)) return new Point(0, 0);
            double sx = 1, sy = 1;
            if (w != null && PresentationSource.FromVisual(w) is HwndSource src
                && src.CompositionTarget != null)
            {
                var t = src.CompositionTarget.TransformToDevice;
                sx = t.M11; sy = t.M22;
            }
            return new Point(p.X / sx, p.Y / sy);
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>启用点击穿透：设置 WS_EX_TRANSPARENT | WS_EX_LAYERED。</summary>
    public static void EnableClickThrough(System.Windows.Window w)
    {
        try
        {
            if (w == null) return;
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }
        catch
        {
            // 点击穿透失败静默
        }
    }

    /// <summary>禁用点击穿透：移除 WS_EX_TRANSPARENT（保留 WS_EX_LAYERED）。</summary>
    public static void DisableClickThrough(System.Windows.Window w)
    {
        try
        {
            if (w == null) return;
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            ex &= ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        }
        catch
        {
        }
    }

    /// <summary>记忆窗口位置到设置（用 GetWindowRect 获取屏幕像素坐标，转 DIP 保存）。</summary>
    public static void SavePosition(System.Windows.Window w, Models.AppSettings s)
    {
        try
        {
            if (w == null || s == null) return;
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!GetWindowRect(hwnd, out RECT r)) return;

            double sx = 1, sy = 1;
            if (PresentationSource.FromVisual(w) is HwndSource src
                && src.CompositionTarget != null)
            {
                var t = src.CompositionTarget.TransformToDevice;
                sx = t.M11; sy = t.M22;
            }
            double left = r.Left / sx;
            double top = r.Top / sy;

            if (!IsWithinScreen(left, top)) return;
            s.WidgetLeft = left;
            s.WidgetTop = top;
        }
        catch
        {
        }
    }

    /// <summary>还原窗口位置（用 SetWindowPos 设屏幕像素坐标）；保存值无效或在工作区外则居中。</summary>
    public static void RestorePosition(System.Windows.Window w, Models.AppSettings s)
    {
        try
        {
            if (w == null || s == null) return;
            double left = s.WidgetLeft;
            double top = s.WidgetTop;
            if (double.IsNaN(left) || double.IsNaN(top) || !IsWithinWorkArea(left, top))
            {
                CenterOnWorkArea(w);
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            double sx = 1, sy = 1;
            if (PresentationSource.FromVisual(w) is HwndSource src
                && src.CompositionTarget != null)
            {
                var t = src.CompositionTarget.TransformToDevice;
                sx = t.M11; sy = t.M22;
            }
            int px = (int)Math.Round(left * sx);
            int py = (int)Math.Round(top * sy);

            SetWindowPos(hwnd, IntPtr.Zero, px, py, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch
        {
        }
    }

    /// <summary>左上角是否落在屏幕（含多显示器虚拟屏幕）内。</summary>
    private static bool IsWithinScreen(double left, double top)
    {
        var vs = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return left >= vs.Left && left < vs.Right && top >= vs.Top && top < vs.Bottom;
    }

    /// <summary>左上角是否落在主屏工作区内。</summary>
    private static bool IsWithinWorkArea(double left, double top)
    {
        var wa = SystemParameters.WorkArea;
        return left >= wa.Left && left < wa.Right && top >= wa.Top && top < wa.Bottom;
    }

    private static void CenterOnWorkArea(System.Windows.Window w)
    {
        var wa = SystemParameters.WorkArea;
        double ww = double.IsNaN(w.Width) ? (w.ActualWidth > 0 ? w.ActualWidth : 300) : w.Width;
        double wh = double.IsNaN(w.Height) ? (w.ActualHeight > 0 ? w.ActualHeight : 150) : w.Height;
        w.Left = wa.Left + (wa.Width - ww) / 2;
        w.Top = wa.Top + (wa.Height - wh) / 2;
    }

    /// <summary>
    /// 手动移动窗口（供 WorkerW 子窗口拖拽用）。WPF 的 DragMove() 在 SetParent 后的
    /// 子窗口上会抛 InvalidOperationException，故用 SetWindowPos 直接移动 HWND。
    /// dx/dy 为屏幕 DIP 增量；同时回写 w.Left/w.Top 以便 SavePosition 读取。
    /// </summary>
    public static void MoveBy(System.Windows.Window w, double dx, double dy)
    {
        try
        {
            if (w == null) return;
            IntPtr hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            double sx = 1, sy = 1;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } src)
            {
                var t = src.CompositionTarget.TransformToDevice;
                sx = t.M11; sy = t.M22;
            }

            int px = (int)Math.Round(dx * sx);
            int py = (int)Math.Round(dy * sy);

            if (!GetWindowRect(hwnd, out RECT r)) return;
            SetWindowPos(hwnd, IntPtr.Zero, r.Left + px, r.Top + py, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            // 回写 DIP 坐标（父窗口为全屏 WorkerW 时，屏幕 DIP ≈ 相对父客户区坐标）
            try { w.Left = (r.Left + px) / sx; w.Top = (r.Top + py) / sy; } catch { }
        }
        catch
        {
        }
    }
}
