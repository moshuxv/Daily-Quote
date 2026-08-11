using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using 每日一句.Models;
using 每日一句.Native;
using 每日一句.Services;

// 项目同时启用了 UseWPF + UseWindowsForms，隐式 using 引入了 System.Windows.Forms /
// System.Drawing，下列类型名两边都有。显式取 WPF 版本，避免 CS0104 歧义。
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace 每日一句;

/// <summary>
/// 桌面浮窗主窗口（Agent A）。
/// 无边框透明渐变卡片，支持手动拖拽移动、单击/双击区分、内联右键菜单、定时刷新、
/// 桌面层挂载（WorkerW）与位置记忆。
/// </summary>
public partial class WidgetWindow : Window
{
    /// <summary>拖拽阈值（像素），超过才开始移动窗口，避免吞掉单击/双击</summary>
    private const double DragThreshold = 5.0;

    /// <summary>单击/双击区分计时器</summary>
    private readonly DispatcherTimer _clickTimer;

    /// <summary>定时刷新计时器</summary>
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>位置落盘防抖计时器（停止移动 800ms 后写一次 data.json）</summary>
    private readonly DispatcherTimer _positionSaveTimer;

    /// <summary>打字机逐字动画计时器</summary>
    private readonly DispatcherTimer _typewriterTimer;

    /// <summary>当前打字机任务状态（null 表示当前无动画进行中）</summary>
    private TypewriterJob? _tw;

    /// <summary>XAML 里的主题渐变画刷，BackgroundColor 清空时用它还原</summary>
    private readonly Brush _themeGradient;

    /// <summary>当前渲染的句子（供复制使用）</summary>
    private Quote? _currentQuote;

    /// <summary>按下时的鼠标位置（窗口坐标，仅用于拖拽阈值判定）</summary>
    private Point _downPosition;

    /// <summary>上一次记录的光标屏幕位置（DIP），用于手动拖拽的增量计算</summary>
    private Point _lastScreen;

    /// <summary>是否已进入拖拽（拖拽结束后不再触发点击动作）</summary>
    private bool _isDragging;

    /// <summary>是否处于恢复位置阶段（避免 RestorePosition 触发 LocationChanged 反向保存）</summary>
    private bool _restoringPosition;

    public WidgetWindow()
    {
        InitializeComponent();

        _themeGradient = Card.Background;

        // 单击/双击区分计时器
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clickTimer.Tick += OnClickTimerTick;

        // 定时刷新计时器
        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += async (_, _) => await AutoRefreshAsync();

        // 位置落盘防抖：拖拽过程中不一定会派发 MouseUp，故统一由 LocationChanged 驱动
        _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _positionSaveTimer.Tick += OnPositionSaveTick;

        // 打字机逐字动画计时器
        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
        _typewriterTimer.Tick += OnTypewriterTick;

        // 设置变更监听（其他窗口保存设置后实时生效）
        SettingsService.Changed += OnSettingsChanged;

        // 系统主题变更监听（Theme=="system" 时用户切换系统浅色/深色要实时跟随）
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Closed += OnWindowClosed;
        Loaded += OnWindowLoaded;
    }

    // ===================== 生命周期 =====================

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 挂载到桌面壁纸层（WorkerW），沉到桌面图标之下
        WorkerW.Attach(this);

        // 恢复位置记忆（在 WorkerW.Attach 之后，坐标体系已稳定为屏幕坐标）
        _restoringPosition = true;
        WindowExtensions.RestorePosition(this, App.CurrentSettings);
        _restoringPosition = false;

        // 先应用视觉（字体/字号/主题），打字机量尺寸前必须拿到正确的字号，否则窗口尺寸算错
        ApplyVisual();

        // 初始句子：优先取全局 CurrentQuote，为空则显示占位句
        Quote? q = App.CurrentQuote;
        Quote initial = q is not null && !string.IsNullOrEmpty(q.English)
            ? q
            : new Quote
            {
                English = "The best way out is always through.",
                Chinese = "最好的出路永远都是走下去。",
                Author = "Robert Frost"
            };

        // 首次渲染延迟到首帧布局/呈现之后：WorkerW 挂载改了窗口样式与父子关系，若在此同步立即
        // 测量，会拿到未稳定下来的小尺寸并锁死（刷新时窗口已稳定故正常）。等窗口完成一次布局后再
        // 量尺寸，打字机即可锁定到正确宽度。
        Dispatcher.BeginInvoke(new Action(() => RenderQuote(initial)), System.Windows.Threading.DispatcherPriority.Render);

        RestartRefreshTimer();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _clickTimer.Stop();
        _typewriterTimer.Stop();

        // 退出前把还没到点的位置变更冲刷落盘（同步等待，内部无 UI 线程续体，不会死锁）
        bool pending = _positionSaveTimer.IsEnabled;
        _positionSaveTimer.Stop();
        if (pending)
        {
            try { SettingsService.SaveAsync(App.CurrentSettings, notify: false).GetAwaiter().GetResult(); }
            catch (Exception ex) { App.LogWarn(ex); }
        }

        SettingsService.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; // 静态事件，不退订会泄漏窗口实例
        WorkerW.Detach(this);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_restoringPosition) return;
        if (!IsLoaded) return;
        WindowExtensions.SavePosition(this, App.CurrentSettings);

        // 防抖：移动过程中反复重置，停下来 800ms 后才真正写盘
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    /// <summary>把内存里的新坐标持久化到 data.json（notify:false，避免重置刷新计时器）。</summary>
    private void OnPositionSaveTick(object? sender, EventArgs e)
    {
        _positionSaveTimer.Stop();
        _ = SettingsService.SaveAsync(App.CurrentSettings, notify: false);
    }

    // ===================== 外观应用 =====================

    /// <summary>根据 App.CurrentSettings 刷新全部视觉属性</summary>
    private void ApplyVisual()
    {
        AppSettings s = App.CurrentSettings;

        // 整窗恒不透明：Opacity 设置项只作用于背景画刷，文字始终清晰可见
        Opacity = 1.0;

        // 字体族映射：system→Segoe UI, sans→微软雅黑, serif→Georgia, mono→Courier New
        FontFamily font = FontFamilyMap(s.FontFamily);
        EngText.FontFamily = font;
        ZhText.FontFamily = font;
        AuthorText.FontFamily = font;

        // 字号：英文=FontSize 粗体，中文=FontSize-3，作者=FontSize-6（对应前端 1 / 0.83 / 0.67 比例）
        EngText.FontSize = s.FontSize;
        ZhText.FontSize = Math.Max(9, s.FontSize - 3);
        AuthorText.FontSize = Math.Max(8, s.FontSize - 6);

        // 文字颜色：英文/中文/作者三处统一用 ColorText（旧的三色字段已从 AppSettings 移除）
        Brush textBrush = HexToBrush(s.ColorText);
        EngText.Foreground = textBrush;
        ZhText.Foreground = textBrush;
        AuthorText.Foreground = textBrush;

        // 主题背景：dark 或 system+深色时使用深蓝渐变（与前端一致）
        bool dark = s.Theme == "dark" || (s.Theme == "system" && IsSystemDark());
        GradStart.Color = ColorFromHex(dark ? "#0A3D6B" : "#0F6CBD");
        GradEnd.Color = ColorFromHex(dark ? "#15558C" : "#3B8CDF");

        // 自定义背景色：空 → 用主题渐变；否则纯色覆盖
        Card.Background = string.IsNullOrWhiteSpace(s.BackgroundColor)
            ? _themeGradient
            : HexToBrush(s.BackgroundColor);

        // 背景不透明度 = Opacity / 100：0 时背景全透明露出桌面，文字不受影响。
        // BrushConverter / Brushes.White 可能返回冻结画刷，冻结后不可改属性，先解冻副本。
        if (Card.Background.IsFrozen) Card.Background = Card.Background.Clone();
        Card.Background.Opacity = Math.Clamp(s.Opacity, 0, 100) / 100.0;
    }

    private static FontFamily FontFamilyMap(string family) => family switch
    {
        "sans" => new FontFamily("微软雅黑"),
        "serif" => new FontFamily("Georgia"),
        "mono" => new FontFamily("Courier New"),
        _ => new FontFamily("Segoe UI") // system 默认
    };

    private static Brush HexToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.White;
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(hex)!;
        }
        catch (Exception)
        {
            return Brushes.White;
        }
    }

    private static Color ColorFromHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>
    /// 系统深色模式判断。原先用 WindowGlassColor 亮度启发式，容易误判导致"跟随系统"失效，
    /// 现统一委托给 ThemeHelper（读注册表 AppsUseLightTheme），与设置页判定保持一致。
    /// </summary>
    private static bool IsSystemDark() => ThemeHelper.IsSystemDark();

    // ===================== 句子渲染 =====================

    private void RenderQuote(Quote? q)
    {
        if (q is null) return;
        _currentQuote = q;
        App.CurrentQuote = q; // 同步全局当前句，保证设置窗/下次启动取到同一句

        if (App.CurrentSettings.Typewriter)
            RenderQuoteTypewriter(q);
        else
            SetPlainText(q);
    }

    /// <summary>非打字机模式：直接显示完整文字，并确保窗口未被固定尺寸锁住。</summary>
    private void SetPlainText(Quote q)
    {
        _typewriterTimer.Stop();
        _tw = null;
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = double.NaN;
        Height = double.NaN;
        EngText.Text = q.English ?? "";
        ZhText.Text = q.Chinese ?? "";
        AuthorText.Text = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;
    }

    /// <summary>
    /// 打字机模式渲染：先把全文铺上并强制布局，量出最终窗口尺寸并锁死（动画期间不随文字伸缩），
    /// 再清空文字、从英文开始逐字浮现（英文 → 中文 → 作者）。
    /// </summary>
    private void RenderQuoteTypewriter(Quote q)
    {
        _typewriterTimer.Stop();

        string eng = q.English ?? "";
        string zh = q.Chinese ?? "";
        string author = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;

        // 0) 测量前先复位成内容自适应尺寸，否则动画未播完时连续切换句子会量到上一轮被钉死的旧尺寸
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = double.NaN;
        Height = double.NaN;

        // 1) 先把全文铺上 + 强制同步布局，量出最终尺寸
        EngText.Text = eng;
        ZhText.Text = zh;
        AuthorText.Text = author;
        UpdateLayout();

        // 2) 锁死为最终尺寸（防御 0 尺寸：极端情况下退回自适应，不把窗口钉成 0 大小）
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Width = ActualWidth;
            Height = ActualHeight;
        }

        // 3) 清空文字，准备逐字浮现
        EngText.Text = "";
        ZhText.Text = "";
        AuthorText.Text = "";

        _tw = new TypewriterJob(eng, zh, author);
        if (_tw.TotalLength == 0)
        {
            FinishTypewriter(); // 没有可敲的字，直接收尾
            return;
        }

        _typewriterTimer.Start();
    }

    private void OnTypewriterTick(object? sender, EventArgs e)
    {
        if (_tw is null) return;

        const int step = 2; // 每次浮现的字符数
        while (true)
        {
            string seg = _tw.CurrentSegment;
            int segLen = seg.Length;

            if (_tw.Index < segLen)
            {
                _tw.Index = Math.Min(segLen, _tw.Index + step);
                SetSegmentText(_tw.Segment, seg.Substring(0, _tw.Index));
                return; // 已浮现一部分，等下一拍
            }

            // 当前段已完成（或本就为空）：推进到下一段
            if (_tw.Segment >= 2)
            {
                FinishTypewriter();
                return;
            }
            _tw.Segment++;
            _tw.Index = 0;
            // 空段会在下一轮循环立即再推进，直到遇到非空段或结束
        }
    }

    private void SetSegmentText(int seg, string text)
    {
        switch (seg)
        {
            case 0: EngText.Text = text; break;
            case 1: ZhText.Text = text; break;
            default: AuthorText.Text = text; break;
        }
    }

    private void FinishTypewriter()
    {
        _typewriterTimer.Stop();
        if (_tw is not null)
        {
            EngText.Text = _tw.English;
            ZhText.Text = _tw.Chinese;
            AuthorText.Text = _tw.Author;
        }
        // 动画结束：恢复 SizeToContent，使后续字号/颜色等设置变更仍能自适应
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = double.NaN;
        Height = double.NaN;
        _tw = null;
    }

    /// <summary>供设置窗口"立即更新"成功后推送新句子到浮窗（必须在 UI 线程调用）。</summary>
    internal void ShowQuote(Quote? q) => RenderQuote(q);

    // ===================== 拖拽移动 =====================

    /// <summary>
    /// 光标当前的屏幕位置（DIP）。不能用 Mouse.GetPosition(null)：它返回的是相对窗口
    /// 根视觉的坐标，窗口跟着光标移动后前后两次的差值会互相抵消，拖拽会原地抖动。
    /// </summary>
    private Point CursorOnScreen(MouseEventArgs e)
    {
        Point device = PointToScreen(e.GetPosition(this));
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new Point(device.X / dpi.DpiScaleX, device.Y / dpi.DpiScaleY);
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _downPosition = e.GetPosition(this);
        _lastScreen = CursorOnScreen(e);
        _isDragging = false;
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (App.CurrentSettings.LockPosition) return;

        // 未超过阈值前不动窗口，保住单击/双击手势
        if (!_isDragging)
        {
            Point pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _downPosition.X) <= DragThreshold &&
                Math.Abs(pos.Y - _downPosition.Y) <= DragThreshold) return;
            _isDragging = true;
        }

        // 挂到 WorkerW 后窗口是子窗口，DragMove() 会抛 InvalidOperationException，
        // 改为按光标屏幕增量手动 SetWindowPos。
        Point now = CursorOnScreen(e);
        double dx = now.X - _lastScreen.X;
        double dy = now.Y - _lastScreen.Y;
        _lastScreen = now;
        if (dx != 0 || dy != 0) WindowExtensions.MoveBy(this, dx, dy);
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_isDragging)
        {
            _isDragging = false;
            return;
        }

        // 区分单击/双击：250ms 内再次按下视为双击
        if (_clickTimer.IsEnabled)
        {
            _clickTimer.Stop();
            ExecuteAction(App.CurrentSettings.DoubleAction);
        }
        else
        {
            _clickTimer.Start();
        }
    }

    private void OnClickTimerTick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        ExecuteAction(App.CurrentSettings.ClickAction);
    }

    // ===================== 单击 / 双击动作 =====================

    private void ExecuteAction(string action)
    {
        switch (action)
        {
            case "random":
                _ = RandomRefreshAsync();
                break;
            case "settings":
                OpenSettings();
                break;
            case "copy":
                CopyQuote();
                break;
            // "none"：无操作
        }
    }

    // ===================== 右键菜单 =====================

    /// <summary>右键：在光标处弹出独立菜单窗口（顶层窗口，避免被主窗口边界裁切）。</summary>
    private void Card_PreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        // 用 Win32 GetCursorPos 取真实屏幕光标：窗口挂在 WorkerW 当桌面子窗口时，
        // WPF 的 PointToScreen 坐标参考系错误，会偏到很远。
        Point screen = WindowExtensions.GetCursorPosDip(this);
        var menu = new ContextMenuWindow(this);
        menu.Loaded += (_, _) =>
        {
            double menuW = menu.ActualWidth;
            double menuH = menu.ActualHeight;
            // 钳制到整块虚拟屏幕内，避免菜单溢出屏幕被裁切（多显示器也适用）
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;
            double vRight = vLeft + SystemParameters.VirtualScreenWidth;
            double vBottom = vTop + SystemParameters.VirtualScreenHeight;
            double x = Math.Min(screen.X, vRight - menuW - 4);
            double y = Math.Min(screen.Y, vBottom - menuH - 4);
            menu.Left = Math.Max(vLeft, x);
            menu.Top = Math.Max(vTop, y);
        };
        menu.Show();
        e.Handled = true;
    }

    // ===================== 定时刷新 =====================

    private void RestartRefreshTimer()
    {
        _refreshTimer.Stop();
        AppSettings s = App.CurrentSettings;
        int minutes = s.RefreshInterval <= 0 ? 1440 : s.RefreshInterval;
        _refreshTimer.Interval = TimeSpan.FromMinutes(minutes);
        _refreshTimer.Start();
    }

    private async Task AutoRefreshAsync()
    {
        try
        {
            AppSettings s = App.CurrentSettings;
            // DailyUpdate 开关已从 AppSettings 移除：间隔为一天（1440 分钟）时取"今日一句"，否则随机
            Quote? q = s.RefreshInterval == 1440
                ? await QuoteService.FetchAsync(false)
                : await QuoteService.GetRandomAsync();
            RenderQuote(q);
        }
        catch (Exception ex)
        {
            // 定时刷新失败静默
            App.LogWarn(ex);
        }
    }

    // ===================== 其他交互动作 =====================

    private async Task RandomRefreshAsync()
    {
        try
        {
            Quote q = await QuoteService.GetRandomAsync();
            RenderQuote(q);
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
    }

    /// <summary>复制：`"英文" —— 作者｜中文`</summary>
    private void CopyQuote()
    {
        if (_currentQuote is null) return;
        string author = (_currentQuote.Author ?? "").Trim().TrimStart('—', '-', ' ');
        string text = $"\"{_currentQuote.English}\" —— {author}｜{_currentQuote.Chinese}";
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
    }

    // ===================== 右键菜单动作（供 ContextMenuWindow 调用）=====================

    internal void ActionRefresh() => _ = RandomRefreshAsync();

    internal void ActionCopy() => CopyQuote();

    internal void ActionSettings() => OpenSettings();

    internal void ActionQuit() => Application.Current.Shutdown();

    internal void ActionLock(bool locked)
    {
        App.CurrentSettings.LockPosition = locked;
        _ = SettingsService.SaveAsync(App.CurrentSettings);
    }

    /// <summary>设置窗口单例，避免每次点击都新开一个</summary>
    private static SettingsWindow? _settingsWindow;

    /// <summary>
    /// 打开设置窗口：单例复用 + 异常兜底（构造失败也不能崩掉整个程序）。
    /// internal：托盘菜单（App.OpenSettings）也走这里，两个入口共用同一个单例，
    /// 否则托盘与浮窗会各开一个设置窗口。
    /// </summary>
    internal static void OpenSettings()
    {
        try
        {
            if (_settingsWindow is null)
            {
                var w = new SettingsWindow();
                w.Closed += (_, _) => _settingsWindow = null; // 关闭后允许再次新建
                _settingsWindow = w;
            }

            if (!_settingsWindow.IsVisible) _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            App.LogCrash(ex);
        }
    }

    // ===================== 设置变更监听 =====================

    /// <summary>
    /// 系统外观（浅色/深色）变化时刷新浮窗，仅在"跟随系统"下生效。
    /// UserPreferenceChanged 在非 UI 线程触发，ApplyVisual 访问 WPF 元素必须调度回 Dispatcher，
    /// 否则会抛 InvalidOperationException（同 SettingsService.RaiseChanged 的处理方式）。
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (App.CurrentSettings.Theme != "system") return;

        try
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher ?? this.Dispatcher;
            if (dispatcher.CheckAccess()) ApplyVisual();
            else dispatcher.InvokeAsync(ApplyVisual);
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
    }

    /// <summary>设置保存后实时刷新本窗口（事件由 SettingsService 提供）</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            // 关闭打字机时若动画正在进行，立即补全文字并解除固定尺寸
            if (!App.CurrentSettings.Typewriter && _typewriterTimer.IsEnabled)
            {
                _typewriterTimer.Stop();
                if (_tw is not null)
                {
                    EngText.Text = _tw.English;
                    ZhText.Text = _tw.Chinese;
                    AuthorText.Text = _tw.Author;
                    _tw = null;
                }
                SizeToContent = SizeToContent.WidthAndHeight;
                Width = double.NaN;
                Height = double.NaN;
            }

            ApplyVisual();
            RestartRefreshTimer();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    // ===================== 打字机任务状态 =====================

    /// <summary>打字机任务状态：三段（英文 / 中文 / 作者）依次逐字浮现。</summary>
    private sealed class TypewriterJob
    {
        public string English { get; }
        public string Chinese { get; }
        public string Author { get; }
        public int Segment { get; set; } // 0=英文 1=中文 2=作者
        public int Index { get; set; }   // 当前段已浮现字符数

        public TypewriterJob(string english, string chinese, string author)
        {
            English = english;
            Chinese = chinese;
            Author = author;
        }

        public string CurrentSegment => Segment switch
        {
            0 => English,
            1 => Chinese,
            _ => Author
        };

        public int TotalLength => English.Length + Chinese.Length + Author.Length;
    }
}
