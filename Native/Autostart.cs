using System;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace 每日一句.Native;

/// <summary>
/// 开机自启：通过 HKCU 注册表 Run 键实现。
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "每日一句";

    /// <summary>设置/取消开机自启（注册表读写失败静默）。</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                key.SetValue(ValueName, GetExecutablePath());
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时静默，不打扰用户
        }
    }

    /// <summary>查询当前是否已设置开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>可执行文件路径（带引号）。</summary>
    private static string GetExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            try { path = Application.ResourceAssembly.Location; } catch { }
        }
        if (string.IsNullOrEmpty(path))
        {
            path = Assembly.GetEntryAssembly()?.Location;
        }
        return "\"" + path + "\"";
    }
}
