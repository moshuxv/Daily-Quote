using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using System.Diagnostics;
using 每日一句.Models;
using 每日一句.Services;

// 项目同时启用 UseWPF / UseWindowsForms，隐式 global using 会把 System.Drawing
// 与 System.Windows.Forms 一起引入，造成同名类型二义（CS0104）。
// 这里用别名把同名类型钉死到 WPF 版本；取色统一走自绘的 ColorPickerWindow，
// 不再依赖 WinForms 的 ColorDialog。
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using FontFamily = System.Windows.Media.FontFamily;
using RadioButton = System.Windows.Controls.RadioButton;

namespace 每日一句;

/// <summary>
/// 设置窗口（Agent B — 设置 UI）。
/// 依赖 SettingsService / QuoteService 由其他 Agent 提供。
/// </summary>
public partial class SettingsWindow : Window
{
    private bool _loading;

    /// <summary>输入框 ⇄ 下拉互相同步时的重入守卫</summary>
    private bool _syncingInterval;

    /// <summary>预览卡片动画引擎：与浮窗共用 TextAnimationEngine，选择动画效果时预览实时生效。</summary>
    private TextAnimationEngine? _previewAnim;

    /// <summary>预览卡片固定展示的示例句（与 XAML 里的默认文案一致）。</summary>
    private static readonly Quote PreviewQuote = new Quote
    {
        English = "The best way out is always through.",
        Chinese = "最好的出路永远都是走下去。",
        Author = "Robert Frost"
    };

    public SettingsWindow()
    {
        // 置位加载标志，屏蔽 XAML 加载期触发的事件副作用（IsChecked=True 会触发
        // Checked/Checked 事件，此时各字段尚未全部实例化）
        _loading = true;
        InitializeComponent();
        Loaded += OnLoaded;

        // 预览卡片动画引擎：锁尺寸回调针对预览卡片（Border），避免动画期间卡片抖动
        _previewAnim = new TextAnimationEngine(PvEnglish, PvChinese, PvAuthor,
            updateLayout: () => UpdateLayout(),
            onLock: () =>
            {
                if (PreviewCard.ActualWidth > 0 && PreviewCard.ActualHeight > 0)
                {
                    PreviewCard.Width = PreviewCard.ActualWidth;
                    PreviewCard.Height = PreviewCard.ActualHeight;
                }
            },
            onUnlock: () =>
            {
                PreviewCard.Width = double.NaN;
                PreviewCard.Height = double.NaN;
            });
    }

    // ==================== 初始化 ====================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            var loaded = await SettingsService.LoadAsync();
            if (loaded is not null)
            {
                App.CurrentSettings = loaded; // LoadAsync 已合并默认值
            }
        }
        catch (Exception ex)
        {
            // 读取失败保留 App.CurrentSettings 默认值
            App.LogWarn(ex);
        }

        ApplyToUI();
        _loading = false;

        // 进入即可见动画效果：延迟到首帧布局完成后再播，避免预览卡片还没量到尺寸就锁定（会轻微抖动）
        _ = Dispatcher.BeginInvoke(new Action(PlayPreview), System.Windows.Threading.DispatcherPriority.Render);

        // 订阅外部设置变更（右键菜单锁定/取消锁定等），实时刷新本页显示
        SettingsService.Changed += OnExternalSettingsChanged;
    }

    /// <summary>外部（右键菜单等）改了设置后刷新本页控件；_loading 守卫避免程序化赋值触发回写循环。</summary>
    private void OnExternalSettingsChanged(object? sender, EventArgs e)
    {
        _loading = true;
        try { ApplyToUI(); }
        finally { _loading = false; }
    }

    // ==================== 控件 → 设置 与 设置 → 控件 ====================

    private void ApplyToUI()
    {
        var s = App.CurrentSettings;

        switch (s.Theme)
        {
            case "dark": ThemeDark.IsChecked = true; break;
            case "system": ThemeSystem.IsChecked = true; break;
            default: ThemeLight.IsChecked = true; break;
        }

        // 文字色/背景色没有输入框做中间层，由色块与拾色器直接写 App.CurrentSettings，
        // 这里只需让预览把它们画出来（见末尾 UpdatePreview）。

        OpacitySlider.Value = s.Opacity;
        OpacityValue.Text = $"{s.Opacity}%";

        SelectComboByTag(FontFamilyCombo, s.FontFamily);
        FontSizeSlider.Value = s.FontSize;
        FontSizeValue.Text = $"{s.FontSize} px";

        SelectComboByTag(ClickActionCombo, s.ClickAction);
        SelectComboByTag(DoubleActionCombo, s.DoubleAction);

        SelectPreset(s.RefreshInterval);
        UpdateIntervalBoxState();

        AutostartToggle.IsChecked = s.Autostart;
        LockToggle.IsChecked = s.LockPosition;
        TextAnimationToggle.IsChecked = s.TextAnimationEnabled;
        SelectComboByTag(EffectCombo, s.TextAnimationEffect);
        EffectRow.Visibility = s.TextAnimationEnabled ? Visibility.Visible : Visibility.Collapsed;

        ApplyTheme(s.Theme);
        UpdatePreview();
    }

    private void CollectFromUI()
    {
        var s = App.CurrentSettings;
        s.Theme = ThemeDark.IsChecked == true ? "dark"
                 : ThemeSystem.IsChecked == true ? "system" : "light";
        // ColorText / BackgroundColor 由色块与拾色器实时写入，无需从控件回读
        s.Opacity = (int)OpacitySlider.Value;
        s.FontFamily = SelectedTag(FontFamilyCombo) ?? s.FontFamily;
        s.FontSize = (int)FontSizeSlider.Value;
        s.ClickAction = SelectedTag(ClickActionCombo) ?? s.ClickAction;
        s.DoubleAction = SelectedTag(DoubleActionCombo) ?? s.DoubleAction;
        // 输入框在预设档位下会被清空禁用，只有自定义档位才有值可回读
        if (int.TryParse(RefreshIntervalBox.Text, out int ri))
        {
            s.RefreshInterval = Math.Clamp(ri, 1, 1440);
        }
        s.Autostart = AutostartToggle.IsChecked == true;
        s.LockPosition = LockToggle.IsChecked == true;
        s.TextAnimationEnabled = TextAnimationToggle.IsChecked == true;
        s.TextAnimationEffect = SelectedTag(EffectCombo) ?? "打字机";
    }

    // ==================== 主题 ====================

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        // 加载期（含 OnLoaded→ApplyToUI 写 IsChecked）统一屏蔽，主题由 ApplyToUI 直接设置
        if (_loading) return;
        if (sender is not RadioButton rb || rb.IsChecked != true || rb.Tag is not string theme) return;
        App.CurrentSettings.Theme = theme;
        ApplyTheme(App.CurrentSettings.Theme);
        UpdatePreview();
    }

    private void ApplyTheme(string theme)
    {
        bool dark = theme == "dark" || (theme == "system" && IsSystemDark());
        SetBrush("BgBrush", dark ? "#1F1F1F" : "#FFFFFF");
        SetBrush("SidebarBrush", dark ? "#252528" : "#F7F7F7");
        SetBrush("TextBrush", dark ? "#FFFFFF" : "#1F1F1F");
        SetBrush("Text2Brush", dark ? "#BDBDBD" : "#5E5E5E");
        SetBrush("HintBrush", dark ? "#9A9A9A" : "#8A8A8A");
        SetBrush("BorderBrush", dark ? "#3A3A3D" : "#E8E8E8");
        SetBrush("AccentBrush", dark ? "#4A9BE0" : "#0F6CBD");
        SetBrush("AccentSoftBrush", dark ? "#223E57" : "#EAF3FB");
        SetBrush("NavHoverBrush", dark ? "#1AFFFFFF" : "#1A7F7F7F");
        SetBrush("TrackOffBrush", dark ? "#5A5A5E" : "#C8C8C8");
    }

    private void SetBrush(string key, string hex)
    {
        Resources[key] = HexToBrush(hex);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 预览 ====================

    private void UpdatePreview()
    {
        var s = App.CurrentSettings;
        var family = FontFamilyFor(s.FontFamily);
        // 英文/中文/署名共用一个文字色
        var textBrush = HexToBrush(s.ColorText);

        PvEnglish.FontFamily = family;
        PvEnglish.FontSize = s.FontSize;
        PvEnglish.Foreground = textBrush;

        PvChinese.FontFamily = family;
        PvChinese.FontSize = Math.Max(10, s.FontSize - 3);
        PvChinese.Foreground = textBrush;

        PvAuthor.FontFamily = family;
        PvAuthor.FontSize = 12;
        PvAuthor.Foreground = textBrush;

        // 背景色留空则回到主题默认渐变（与浮窗同一套配色，随浅色/深色切换）
        Brush bg = string.IsNullOrWhiteSpace(s.BackgroundColor)
            ? ThemeGradientBrush(s.Theme)
            : HexToBrush(s.BackgroundColor);

        // 资源里的渐变笔刷（以及 Brushes.White 这类静态笔刷）是冻结的，
        // 直接改 Opacity 会抛 InvalidOperationException，必须先克隆
        if (bg.IsFrozen) bg = bg.Clone();
        bg.Opacity = Math.Clamp(s.Opacity, 0, 100) / 100.0;
        PreviewCard.Background = bg;
    }

    /// <summary>
    /// 返回与浮窗同一套的主题默认渐变（随浅色/深色切换）：
    /// 深色 #0A3D6B→#15558C，浅色 #0F6CBD→#3B8CDF。
    /// 背景色留空时回退到此，保证预览与浮窗配色一致、且跟随主题。
    /// </summary>
    private static Brush ThemeGradientBrush(string theme)
    {
        bool dark = theme == "dark" || (theme == "system" && ThemeHelper.IsSystemDark());
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(ColorFromHex(dark ? "#0A3D6B" : "#0F6CBD"), 0.0),
                new GradientStop(ColorFromHex(dark ? "#15558C" : "#3B8CDF"), 1.0),
            }
        };
        return brush;
    }

    private static Color ColorFromHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static FontFamily FontFamilyFor(string key) => key switch
    {
        "serif" => new FontFamily("Georgia, SimSun"),
        "mono" => new FontFamily("Consolas, Courier New"),
        "sans" => new FontFamily("Microsoft YaHei UI"),
        _ => new FontFamily("Microsoft YaHei UI, Segoe UI"),
    };

    // ==================== 颜色（#RRGGBB 字符串 ⇄ Brush）====================

    /// <summary>把 "#RRGGBB" / "#AARRGGBB" 字符串转成 Brush，非法时回退白色。</summary>
    private static Brush HexToBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Brushes.White;
        string t = hex.Trim().TrimStart('#');
        if (t.Length == 6 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return new SolidColorBrush(Color.FromRgb(
                (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
        if (t.Length == 8 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            return new SolidColorBrush(Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
        return Brushes.White;
    }

    /// <summary>规范化 hex 输入为 "#RRGGBB"，非法时返回 fallback。</summary>
    private static string NormalizeHex(string? text, string fallback)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith('#'))
        {
            if (t.Length == 7) { /* 已规范 */ }
            else if (t.Length == 6) { /* 去掉 # 后重新处理 */ }
        }
        else if (t.Length == 6)
        {
            t = "#" + t;
        }
        if (t.Length != 7) return fallback;
        return uint.TryParse(t.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? t.ToUpperInvariant()
            : fallback;
    }

    // ==================== 外观事件 ====================

    /// <summary>文字色预设色块：Tag 直接是 "#RRGGBB"。</summary>
    private void SwatchText_Click(object sender, MouseButtonEventArgs e)
    {
        if (_loading || sender is not Ellipse el || el.Tag is not string hex) return;
        App.CurrentSettings.ColorText = hex;
        UpdatePreview();
    }

    /// <summary>背景色预设色块：Tag 为 "#RRGGBB"，空串表示默认渐变。</summary>
    private void SwatchBg_Click(object sender, MouseButtonEventArgs e)
    {
        if (_loading || sender is not Ellipse el || el.Tag is not string hex) return;
        App.CurrentSettings.BackgroundColor = hex;
        UpdatePreview();
    }

    private void TextColorPicker_Click(object sender, RoutedEventArgs e)
    {
        string cur = NormalizeHex(App.CurrentSettings.ColorText, "#FFFFFF");
        string? picked = ColorPickerWindow.Show(this, cur);
        if (string.IsNullOrEmpty(picked)) return; // 取消

        App.CurrentSettings.ColorText = picked;
        UpdatePreview();
    }

    private void BgColorPicker_Click(object sender, RoutedEventArgs e)
    {
        // 背景为"默认渐变"（空串）时没有具体色值可回显，用主题蓝作为拾色起点
        string cur = string.IsNullOrWhiteSpace(App.CurrentSettings.BackgroundColor)
            ? "#0F6CBD"
            : NormalizeHex(App.CurrentSettings.BackgroundColor, "#0F6CBD");

        string? picked = ColorPickerWindow.Show(this, cur);
        if (string.IsNullOrEmpty(picked)) return; // 取消

        App.CurrentSettings.BackgroundColor = picked;
        UpdatePreview();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 与 FontSizeSlider 同模式：先守卫再访问，防加载期 OpacityValue 尚未实例化
        if (_loading) return;
        int v = (int)OpacitySlider.Value;
        OpacityValue.Text = $"{v}%";
        App.CurrentSettings.Opacity = v;
        UpdatePreview(); // 透明度也要实时反映在预览卡上
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期（Minimum=12 强转默认 Value）会提前触发，此时 FontSizeValue 尚未实例化
        if (_loading) return;
        int v = (int)FontSizeSlider.Value;
        FontSizeValue.Text = $"{v} px";
        App.CurrentSettings.FontSize = v;
        UpdatePreview();
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(FontFamilyCombo);
        if (tag is not null)
        {
            App.CurrentSettings.FontFamily = tag;
            UpdatePreview();
        }
    }

    // ==================== 交互事件 ====================

    private void ClickActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(ClickActionCombo);
        if (tag is not null) App.CurrentSettings.ClickAction = tag;
    }

    private void DoubleActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = SelectedTag(DoubleActionCombo);
        if (tag is not null) App.CurrentSettings.DoubleAction = tag;
    }

    // ==================== 刷新间隔 ====================

    private void RefreshPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // _syncingInterval：由输入框反向同步下拉时，不要再回写输入框/改禁用态
        if (_loading || _syncingInterval) return;

        // 预设档位下输入框会被清空禁用，设置值只能在这里落地
        if (SelectedTag(RefreshPreset) is string tag && tag != "custom" && int.TryParse(tag, out int v))
        {
            App.CurrentSettings.RefreshInterval = Math.Clamp(v, 1, 1440);
        }
        UpdateIntervalBoxState();
    }

    /// <summary>
    /// 按当前下拉档位同步间隔输入框：
    /// 预设档位 → 清空、禁用、压暗；自定义 → 启用、回填当前分钟数并聚焦。
    /// </summary>
    private void UpdateIntervalBoxState()
    {
        bool custom = SelectedTag(RefreshPreset) == "custom";

        // 程序化写 Text 不能再反向同步下拉，否则刚选"自定义"就会被弹回预设档位
        _syncingInterval = true;
        try
        {
            RefreshIntervalBox.Text = custom ? App.CurrentSettings.RefreshInterval.ToString() : "";
        }
        finally
        {
            _syncingInterval = false;
        }

        RefreshIntervalBox.IsEnabled = custom;
        RefreshIntervalBox.Opacity = custom ? 1.0 : 0.35;

        if (custom)
        {
            RefreshIntervalBox.Focus();
            RefreshIntervalBox.CaretIndex = RefreshIntervalBox.Text.Length;
        }
    }

    private void RefreshInterval_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 禁用态不会有用户输入；_syncingInterval 挡掉程序化回填
        if (_loading || _syncingInterval) return;
        if (int.TryParse(RefreshIntervalBox.Text, out int v))
        {
            v = Math.Clamp(v, 1, 1440);
            App.CurrentSettings.RefreshInterval = v;

            // 手输时只同步下拉显示，不能顺手把输入框禁用掉（会打断用户继续输入）
            _syncingInterval = true;
            try { SelectPreset(v); }
            finally { _syncingInterval = false; }
        }
    }

    private void SelectPreset(int minutes)
    {
        string tag = minutes.ToString();
        bool isPreset = tag is "5" or "15" or "30" or "60" or "1440";
        RefreshPreset.SelectedItem = FindComboItem(RefreshPreset, isPreset ? tag : "custom");
    }

    // ==================== 开关 ====================

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, AutostartToggle))
        {
            App.CurrentSettings.Autostart = AutostartToggle.IsChecked == true;
            // 注册表立即生效，不等"保存"
            Native.Autostart.Set(App.CurrentSettings.Autostart);
        }
        else if (ReferenceEquals(sender, LockToggle))
        {
            App.CurrentSettings.LockPosition = LockToggle.IsChecked == true;
        }
        else if (ReferenceEquals(sender, TextAnimationToggle))
        {
            App.CurrentSettings.TextAnimationEnabled = TextAnimationToggle.IsChecked == true;
            EffectRow.Visibility = App.CurrentSettings.TextAnimationEnabled ? Visibility.Visible : Visibility.Collapsed;
            PlayPreview();
        }
    }

    /// <summary>动画效果下拉：写入当前效果名称，并让预览卡片立即用该效果重放一遍。</summary>
    private void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        App.CurrentSettings.TextAnimationEffect = SelectedTag(EffectCombo) ?? "打字机";
        PlayPreview();
    }

    /// <summary>
    /// 让预览卡片按当前选中的动画效果播放一遍：开启则播放对应效果，关闭则停掉动画并显示完整文字。
    /// </summary>
    private void PlayPreview()
    {
        if (_previewAnim is null) return;
        if (App.CurrentSettings.TextAnimationEnabled)
            _previewAnim.Play(PreviewQuote, App.CurrentSettings.TextAnimationEffect, ColorFromHex(App.CurrentSettings.ColorText));
        else
            _previewAnim.Stop();
    }

    // ==================== 刷新面板：立即更新 ====================

    private async void UpdateToday_Click(object sender, RoutedEventArgs e)
    {
        UpdateTodayButton.IsEnabled = false;
        try
        {
            var r = await QuoteService.UpdateTodayAsync();
            if (r.Quote is not null)
            {
                PushToWidgets(r.Quote);
                ShowToast("已更新今日句子");
            }
            else
            {
                // 据抓取状态给出准确提示，而不是一律"网络不可用"
                string msg = r.Status switch
                {
                    ShanbayService.FetchStatus.HttpError => "更新失败（服务器异常，稍后重试）",
                    ShanbayService.FetchStatus.ParseError => "更新失败（数据解析异常）",
                    _ => "更新失败（网络不可用）"
                };
                ShowToast(msg);
            }
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            ShowToast("更新失败");
        }
        finally
        {
            UpdateTodayButton.IsEnabled = true;
        }
    }

    /// <summary>把句子同步给所有浮窗，否则"立即更新"看起来毫无效果。</summary>
    private static void PushToWidgets(Quote? q)
    {
        if (q is null) return;
        App.CurrentQuote = q;
        foreach (Window w in Application.Current.Windows)
        {
            if (w is WidgetWindow widget) widget.ShowQuote(q);
        }
    }

    // ==================== 保存 / 取消 ====================

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CollectFromUI();
        try
        {
            // SaveAsync 现在返回是否成功，并在 UI 线程触发 Changed 让浮窗实时刷新
            bool ok = await SettingsService.SaveAsync(App.CurrentSettings);
            if (!ok)
            {
                ShowToast("保存失败");
                return;
            }

            ShowToast("设置已保存");
            await Task.Delay(450); // 让 toast 展示片刻再关窗
            Close();
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            ShowToast("保存失败");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close(); // 丢弃修改，不 Save
    }

    // ==================== 标题栏 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (FindVisualParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (WindowState == WindowState.Maximized) return;
        try { DragMove(); } catch { /* 拖动中异常忽略 */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide(); // 隐藏窗口，不退出程序

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxIcon.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 导航 ====================

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        // 加载期首个导航项 IsChecked="True" 会提前触发，Sec* 尚未实例化，直接跳过
        if (_loading) return;
        if (sender is RadioButton rb && rb.Tag is string key) ShowSection(key);
    }

    private void ShowSection(string key)
    {
        // XAML 加载期间，第一个导航项 IsChecked="True" 会在 Section 元素
        // 实例化之前触发 Checked 事件，此时字段为 null，必须跳过。
        if (SecAppearance is null || SecInteraction is null ||
            SecRefresh is null || SecSystem is null || SecAbout is null)
            return;

        SecAppearance.Visibility = key == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SecInteraction.Visibility = key == "interaction" ? Visibility.Visible : Visibility.Collapsed;
        SecRefresh.Visibility = key == "refresh" ? Visibility.Visible : Visibility.Collapsed;
        SecSystem.Visibility = key == "system" ? Visibility.Visible : Visibility.Collapsed;
        SecAbout.Visibility = key == "about" ? Visibility.Visible : Visibility.Collapsed;

        // 切回外观页时让预览卡片按当前效果重放一遍，方便对比不同效果
        if (key == "appearance") PlayPreview();
    }

    // ==================== 关于页：超链接 ====================

    /// <summary>关于页里的 GitHub / 博客链接：用系统默认浏览器打开。</summary>
    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // 浏览器不可用等异常静默忽略，不抛出
        }
    }

    // ==================== Toast ====================

    private async void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastHost.BeginAnimation(UIElement.OpacityProperty, null);
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.Opacity = 0;
        ToastHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        await Task.Delay(1500);
        if (ToastText.Text == message) // 期间没有新 toast 才淡出
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
            fade.Completed += (_, _) =>
            {
                if (ToastHost.Opacity < 0.05) ToastHost.Visibility = Visibility.Collapsed;
            };
            ToastHost.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    // ==================== 通用小工具 ====================

    private static string? SelectedTag(ComboBox cb) =>
        (cb.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static ComboBoxItem? FindComboItem(ComboBox cb, string tag)
    {
        foreach (object o in cb.Items)
        {
            if (o is ComboBoxItem ci && ci.Tag?.ToString() == tag) return ci;
        }
        return null;
    }

    private static void SelectComboByTag(ComboBox cb, string tag)
    {
        var item = FindComboItem(cb, tag)
                   ?? (cb.Items.Count > 0 ? cb.Items[0] as ComboBoxItem : null);
        if (item is not null) cb.SelectedItem = item;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null && child is not T)
        {
            child = VisualTreeHelper.GetParent(child);
        }
        return child as T;
    }
}
