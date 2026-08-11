namespace 每日一句.Models;

/// <summary>
/// 应用设置实体（对应前端 HTML 中的 DEFAULTS，27 字段）。
/// 注意：字段定义此前约定"禁止修改"，但用户明确要求合并三组文字颜色、移除每日更新开关，
/// 故此处按需求重构。新增/重命名字段时务必同步 WidgetWindow / SettingsWindow / ThemeHelper。
/// </summary>
public class AppSettings
{
    // ===== 外观 =====
    /// <summary>主题：light / dark / system</summary>
    public string Theme { get; set; } = "light";

    /// <summary>文字颜色（英文/中文/作者统一使用，十六进制 "#FFFFFF"）</summary>
    public string ColorText { get; set; } = "#FFFFFF";

    /// <summary>浮窗背景透明度 0–100（默认 55）</summary>
    public int Opacity { get; set; } = 55;

    /// <summary>字体：system / sans / serif / mono</summary>
    public string FontFamily { get; set; } = "system";

    /// <summary>正文字号 12–36（默认 18）</summary>
    public int FontSize { get; set; } = 18;

    /// <summary>浮窗背景颜色（十六进制 "#0F6CBD"）；空字符串 "" 表示使用主题默认渐变</summary>
    public string BackgroundColor { get; set; } = "";

    /// <summary>文字动画：开启后句子切换时按所选效果播放文字动画，默认开启</summary>
    public bool TextAnimationEnabled { get; set; } = true;

    /// <summary>当前文字动画效果名称（由动画注册表提供）；默认"打字机"。</summary>
    public string TextAnimationEffect { get; set; } = "打字机";

    // ===== 交互 =====
    /// <summary>单击动作：random / settings / copy / none</summary>
    public string ClickAction { get; set; } = "random";

    /// <summary>双击动作：random / settings / copy / none</summary>
    public string DoubleAction { get; set; } = "settings";

    // ===== 刷新 =====
    /// <summary>刷新间隔（分钟）1–1440（默认 1440）</summary>
    public int RefreshInterval { get; set; } = 1440;

    // ===== 系统 =====
    /// <summary>开机自启（默认开启）</summary>
    public bool Autostart { get; set; } = true;

    /// <summary>锁定位置（防止误拖动）</summary>
    public bool LockPosition { get; set; } = false;

    // ===== 窗口位置记忆 =====
    /// <summary>浮窗 X 坐标（-1 表示未初始化，居中）</summary>
    public double WidgetLeft { get; set; } = double.NaN;

    /// <summary>浮窗 Y 坐标（-1 表示未初始化，居中）</summary>
    public double WidgetTop { get; set; } = double.NaN;
}
