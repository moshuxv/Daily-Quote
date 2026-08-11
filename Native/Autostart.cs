using System;
using System.Windows;
using Microsoft.Win32;

namespace 每日一句.Native;

/// <summary>
/// 开机自启：通过 HKCU 注册表 Run 键实现。
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "拾句";
    // 改名前的旧值名：新版写入"拾句"后，清理旧的"每日一句"残留（指向已不存在的旧 exe），避免注册表留垃圾
    private const string LegacyValueName = "每日一句";

    /// <summary>设置/取消开机自启（注册表读写失败静默）。</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                string path = GetExecutablePath();
                if (!string.IsNullOrEmpty(path)) key.SetValue(ValueName, path);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            // 清理改名前的旧值（指向已不存在的 每日一句.exe），保持注册表整洁
            if (LegacyValueName != ValueName)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
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

    /// <summary>
    /// 可执行文件路径（带引号）。单文件应用下 Assembly.Location 恒为空串（会触发 IL3000 警告），
    /// Environment.ProcessPath 才是单文件模式下可靠的 exe 路径来源。
    /// </summary>
    private static string GetExecutablePath()
    {
        string? path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? "" : "\"" + path + "\"";
    }
}
