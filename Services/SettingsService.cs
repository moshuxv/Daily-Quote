using System.IO;
using System.Text.Json;
using System.Windows;
using 每日一句.Models;

namespace 每日一句.Services;

/// <summary>
/// 应用设置读写服务（独立文件 settings.json；data.json 仅存语料，二者分离）。
/// 启动时若 settings.json 不存在，则从旧 data.json 的 Settings 节点迁移一次，
/// 保证老用户已保存的设置不丢，且 data.json 不再混入设置状态。
/// </summary>
public static class SettingsService
{
    /// <summary>设置保存成功后触发（Widget 监听以刷新 UI）。</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// 读取设置：
    /// - settings.json 存在 → 直接反序列化（缺失字段保留属性默认值）；
    /// - settings.json 不存在 → 从旧 data.json 的 Settings 节点迁移（仅一次），并落盘 settings.json；
    /// - 两者皆无/异常 → 返回 new AppSettings()（全默认）；
    /// - 加载结果同步到 App.CurrentSettings。
    /// </summary>
    public static async Task<AppSettings> LoadAsync()
    {
        AppSettings loaded;
        try
        {
            loaded = await SettingsStore.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            loaded = new AppSettings();
        }
        App.CurrentSettings = loaded;
        return loaded;
    }

    /// <summary>
    /// 保存设置到 settings.json（不再触碰 data.json 语料），成功后更新 App.CurrentSettings
    /// 并触发 Changed 事件；失败不抛异常，返回 false 供调用方如实提示。
    /// </summary>
    /// <param name="notify">是否触发 Changed（仅记忆窗口位置时可传 false，避免重置刷新计时器）</param>
    public static async Task<bool> SaveAsync(AppSettings settings, bool notify = true)
    {
        try
        {
            await SettingsStore.SaveAsync(settings).ConfigureAwait(false);
            App.CurrentSettings = settings;
        }
        catch (Exception ex)
        {
            // 保存失败不触发 Changed，也不抛异常
            App.LogWarn(ex);
            return false;
        }

        if (notify) RaiseChanged();
        return true;
    }

    /// <summary>
    /// 在 UI 线程上触发 Changed。
    /// SaveAsync 内部用了 ConfigureAwait(false)，续体跑在线程池线程上；
    /// 若直接 Invoke，订阅方（WidgetWindow.ApplyVisual）会因跨线程访问 WPF 元素
    /// 抛 InvalidOperationException 并被吞掉 —— 表现为"保存后浮窗毫无变化"。
    /// </summary>
    private static void RaiseChanged()
    {
        EventHandler? handler = Changed;
        if (handler is null) return;

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(() => SafeInvoke(handler));
            }
            else
            {
                SafeInvoke(handler);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private static void SafeInvoke(EventHandler handler)
    {
        try { handler(null, EventArgs.Empty); }
        catch (Exception ex) { App.LogCrash(ex); }
    }
}

/// <summary>
/// settings.json 读写帮助类（与 data.json 语料文件完全分离）。
/// 复用 DataStore.JsonOptions（同一套序列化选项：缩进 / 允许 NaN 字面量 / 中文不转义）。
/// </summary>
internal static class SettingsStore
{
    /// <summary>
    /// 读取设置：
    /// 1) settings.json 存在 → 优先使用；
    /// 2) 否则从旧 data.json 的 Settings 节点迁移（一次性），迁移后落盘 settings.json；
    /// 3) 仍无 → 返回默认 AppSettings。
    /// </summary>
    internal static async Task<AppSettings> LoadAsync()
    {
        var fromFile = await ReadSettingsFileAsync().ConfigureAwait(false);
        if (fromFile is not null) return fromFile;

        var legacy = await MigrateFromDataAsync().ConfigureAwait(false);
        var result = legacy ?? new AppSettings();

        // 迁移或全新安装：立即落盘，使 settings.json 成为设置唯一来源，
        // 之后 data.json 再被语料写入时便不再含 Settings 节点。
        try { await WriteSettingsFileAsync(result).ConfigureAwait(false); }
        catch (Exception ex) { App.LogWarn(ex); }

        return result;
    }

    internal static async Task SaveAsync(AppSettings settings)
    {
        await WriteSettingsFileAsync(settings).ConfigureAwait(false);
    }

    private static async Task<AppSettings?> ReadSettingsFileAsync()
    {
        try
        {
            if (!File.Exists(App.SettingsFile)) return null;
            var json = await File.ReadAllTextAsync(App.SettingsFile).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, DataStore.JsonOptions);
            if (loaded is null) return null;

            // 迁移：旧字段 Typewriter → TextAnimationEnabled（仅首次读到旧数据时执行一次）
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("Typewriter", out var tw)
                    && (tw.ValueKind == JsonValueKind.True || tw.ValueKind == JsonValueKind.False)
                    && !doc.RootElement.TryGetProperty("TextAnimationEnabled", out _))
                {
                    loaded.TextAnimationEnabled = tw.GetBoolean();
                    if (string.IsNullOrEmpty(loaded.TextAnimationEffect)) loaded.TextAnimationEffect = "打字机";
                }
            }
            catch { /* 迁移失败不影响主流程 */ }

            return loaded;
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return null;
        }
    }

    /// <summary>从旧 data.json 的 Settings 节点迁移（仅当 settings.json 尚不存在时调用）。</summary>
    private static async Task<AppSettings?> MigrateFromDataAsync()
    {
        try
        {
            if (!File.Exists(App.DataFile)) return null;
            var json = await File.ReadAllTextAsync(App.DataFile).ConfigureAwait(false);
            var legacy = JsonSerializer.Deserialize<LegacyDataFile>(json, DataStore.JsonOptions);
            return legacy?.Settings;
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return null;
        }
    }

    private static async Task WriteSettingsFileAsync(AppSettings settings)
    {
        Directory.CreateDirectory(App.WritableDataDir);
        var json = JsonSerializer.Serialize(settings, DataStore.JsonOptions);
        await File.WriteAllTextAsync(App.SettingsFile, json).ConfigureAwait(false);
    }

    /// <summary>仅用于读取旧 data.json 中 Settings 节点的临时模型（Settings 已从 QuoteDataFile 移除）。</summary>
    private sealed class LegacyDataFile
    {
        public AppSettings? Settings { get; set; }
    }
}
