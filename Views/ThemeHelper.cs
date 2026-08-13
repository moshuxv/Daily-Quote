using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace 每日一句;

/// <summary>
/// 主题工具：统一"系统是否深色"的判定，以及把主题画刷应用到任意窗口的 Resources。
/// 此前 WidgetWindow 用 WindowGlassColor 亮度启发式（易误判），SettingsWindow 用注册表，
/// 两者不一致导致"跟随系统"在浮窗上解析错。现统一为注册表 AppsUseLightTheme。
/// </summary>
public static class ThemeHelper
{
    /// <summary>
    /// 系统是否处于深色模式。读 HKCU 个人信息中的 AppsUseLightTheme：
    /// 值 0 = 深色，1 = 浅色。读不到时保守回退浅色（false）。
    /// </summary>
    public static bool IsSystemDark()
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

    /// <summary>
    /// 把 light/dark/system 解析为最终深浅，并在 theme=="system" 时按系统实际状态判定。
    /// </summary>
    public static bool IsDark(string theme) =>
        theme == "dark" || (theme == "system" && IsSystemDark());

    /// <summary>
    /// 将主题画刷应用到目标窗口的资源字典（覆盖 10 个 DynamicResource 键）。
    /// 与 SettingsWindow.ApplyTheme 的调色板完全一致，保证设置页与拾色器同款换肤。
    /// </summary>
    public static void ApplyTo(ResourceDictionary resources, string theme)
    {
        bool dark = IsDark(theme);
        Set(resources, "BgBrush", dark ? "#1F1F1F" : "#FFFFFF");
        Set(resources, "SidebarBrush", dark ? "#252528" : "#F7F7F7");
        Set(resources, "TextBrush", dark ? "#FFFFFF" : "#1F1F1F");
        Set(resources, "Text2Brush", dark ? "#BDBDBD" : "#5E5E5E");
        Set(resources, "HintBrush", dark ? "#9A9A9A" : "#8A8A8A");
        Set(resources, "BorderBrush", dark ? "#3A3A3D" : "#E8E8E8");
        Set(resources, "AccentBrush", dark ? "#4A9BE0" : "#0F6CBD");
        Set(resources, "AccentSoftBrush", dark ? "#223E57" : "#EAF3FB");
        Set(resources, "NavHoverBrush", dark ? "#1AFFFFFF" : "#1A7F7F7F");
        Set(resources, "TrackOffBrush", dark ? "#5A5A5E" : "#C8C8C8");
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
