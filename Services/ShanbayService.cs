using System.Net.Http;
using System.Text.Json;
using 每日一句.Models;

namespace 每日一句.Services;

/// <summary>
/// 扇贝每日一句抓取服务。
/// API: https://apiv3.shanbay.com/weapps/dailyquote/quote/
/// 兼容 english / translation / author 字段可能为 string 或 dict 两种形态。
/// </summary>
public static class ShanbayService
{
    /// <summary>单次请求超时：15s（原 10s 偏短，弱网/首包慢时易被误判为"网络不可用"）。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>失败重试次数（不含首次），弱网偶发抖动时避免直接报"网络不可用"。</summary>
    private const int MaxRetries = 1;

    private const string ApiUrl = "https://apiv3.shanbay.com/weapps/dailyquote/quote/";

    /// <summary>抓取结果状态：用于把"网络不可用"等模糊提示细分为具体原因。</summary>
    public enum FetchStatus { Ok, NetworkError, HttpError, ParseError }

    /// <summary>在 API 基址后拼上当天日期参数，扇贝按 date 返回当日句。</summary>
    private static string BuildUrl() => $"{ApiUrl}?date={DateTime.Now:yyyy-MM-dd}";

    static ShanbayService()
    {
        try
        {
            Http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
    }

    /// <summary>
    /// 抓取当日句：带重试与超时；返回 (Quote, 状态)。
    /// 任何不可恢复失败都优雅降级为 (null, 对应状态)，不抛异常；调用方据状态给出准确提示。
    /// </summary>
    public static async Task<(Quote? Quote, FetchStatus Status)> FetchTodayAsync()
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(BuildUrl()).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    // 5xx 服务端瞬时错误可重试；4xx（含 429）属客户端/限流问题，不再重试
                    bool serverErr = (int)resp.StatusCode >= 500;
                    if (attempt < MaxRetries && serverErr)
                    {
                        await Task.Delay(800).ConfigureAwait(false);
                        continue;
                    }
                    return (null, FetchStatus.HttpError);
                }

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return (null, FetchStatus.ParseError);

                // API 实际字段为 content；english 作为兜底兼容旧/异常响应
                var english = ExtractText(root, "content");
                if (string.IsNullOrWhiteSpace(english)) english = ExtractText(root, "english");
                if (string.IsNullOrWhiteSpace(english)) return (null, FetchStatus.ParseError);

                return (new Quote
                {
                    English = english,
                    Chinese = ExtractText(root, "translation"),
                    Author = ExtractAuthor(root),
                    Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    FetchedAt = DateTime.Now
                }, FetchStatus.Ok);
            }
            catch (Exception ex)
            {
                // TaskCanceledException（超时）也归为网络类错误；其余异常同理
                App.LogWarn(ex);
                if (attempt < MaxRetries)
                {
                    await Task.Delay(800).ConfigureAwait(false);
                    continue;
                }
                return (null, FetchStatus.NetworkError);
            }
        }
        return (null, FetchStatus.NetworkError);
    }

    /// <summary>从 JSON 对象取指定属性文本，属性可能为 string 或 dict。</summary>
    private static string ExtractText(JsonElement root, string prop)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (!root.TryGetProperty(prop, out var el)) return "";
        return TextOf(el);
    }

    /// <summary>提取 JsonElement 的文本：string 直接取；dict 依次尝试 content/text/value/cn/zh 键。</summary>
    private static string TextOf(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString() ?? "";
            case JsonValueKind.Object:
                foreach (var key in new[] { "content", "text", "value", "cn", "zh" })
                {
                    if (el.TryGetProperty(key, out var sub) && sub.ValueKind == JsonValueKind.String)
                        return sub.GetString() ?? "";
                }
                return el.GetRawText();
            default:
                return "";
        }
    }

    /// <summary>
    /// 作者字段兼容：可能为 string；或 dict（含 name/content 等）；或嵌在 english dict 内。
    /// </summary>
    private static string ExtractAuthor(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";

        if (root.TryGetProperty("author", out var a))
        {
            if (a.ValueKind == JsonValueKind.String) return a.GetString() ?? "";
            if (a.ValueKind == JsonValueKind.Object)
            {
                if (a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    return n.GetString() ?? "";
                var t = TextOf(a);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }

        // english 为 dict 时 author 可能嵌在其中
        if (root.TryGetProperty("english", out var en) && en.ValueKind == JsonValueKind.Object
            && en.TryGetProperty("author", out var ea))
        {
            if (ea.ValueKind == JsonValueKind.String) return ea.GetString() ?? "";
            if (ea.ValueKind == JsonValueKind.Object && ea.TryGetProperty("name", out var eaName)
                && eaName.ValueKind == JsonValueKind.String)
                return eaName.GetString() ?? "";
        }

        return "";
    }
}
