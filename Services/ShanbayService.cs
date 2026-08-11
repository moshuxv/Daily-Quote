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
    /// <summary>10s 超时：默认 100s，遇到网络黑洞时会让"手动更新"假死。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private const string ApiUrl = "https://apiv3.shanbay.com/weapps/dailyquote/quote/";

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

    /// <summary>抓取当日句；任何异常/非 2xx/缺英文正文均返回 null，不抛异常。</summary>
    public static async Task<Quote?> FetchTodayAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BuildUrl()).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // API 实际字段为 content；english 作为兜底兼容旧/异常响应
            var english = ExtractText(root, "content");
            if (string.IsNullOrWhiteSpace(english)) english = ExtractText(root, "english");
            if (string.IsNullOrWhiteSpace(english)) return null;

            return new Quote
            {
                English = english,
                Chinese = ExtractText(root, "translation"),
                Author = ExtractAuthor(root),
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                FetchedAt = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            // 网络/解析失败一律优雅返回 null，只留日志
            App.LogWarn(ex);
            return null;
        }
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
