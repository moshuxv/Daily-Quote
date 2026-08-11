using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using 每日一句.Models;

namespace 每日一句.Services;

/// <summary>
/// data.json 文件模型。字段名按 C# 原样序列化（PropertyNamingPolicy 不设）。
/// 结构：{ TodayQuote, Quotes }（仅语料；设置状态已迁移到独立文件 settings.json）。
/// </summary>
internal sealed class QuoteDataFile
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Quote? TodayQuote { get; set; }

    public List<Quote> Quotes { get; set; } = new();
}

/// <summary>data.json 读写帮助类（async IO，异常静默回退默认值/空文件）。</summary>
internal static class DataStore
{
    /// <summary>
    /// 读写共用同一份选项。
    /// NumberHandling 必须允许 NaN/Infinity 字面量：AppSettings.WidgetLeft/WidgetTop
    /// 默认值为 double.NaN（表示"未初始化"），默认选项下 Serialize 会抛
    /// ArgumentException，导致所有写盘（设置保存、抓取入库）静默失败。
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // 中文不转义成 \uXXXX，方便用户从"打开数据目录"直接查看 data.json
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static async Task<QuoteDataFile> ReadAsync()
    {
        try
        {
            if (!File.Exists(App.DataFile)) return new QuoteDataFile();
            var json = await File.ReadAllTextAsync(App.DataFile).ConfigureAwait(false);
            return JsonSerializer.Deserialize<QuoteDataFile>(json, JsonOptions) ?? new QuoteDataFile();
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return new QuoteDataFile();
        }
    }

    internal static async Task WriteAsync(QuoteDataFile data)
    {
        Directory.CreateDirectory(App.DataDir);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(App.DataFile, json).ConfigureAwait(false);
    }
}

/// <summary>
/// corpus.json 单条记录。
/// 不直接复用 Quote 反序列化：内置语料的 FetchedAt 为 null，而 Quote.FetchedAt 是
/// 非空 DateTime，System.Text.Json 遇 null 会抛 JsonException 导致整包语料加载失败。
/// </summary>
internal sealed class CorpusEntry
{
    public string English { get; set; } = "";
    public string Chinese { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public DateTime? FetchedAt { get; set; }
}

/// <summary>每日一句核心服务：抓取当日句、今日句、随机句、语料统计。</summary>
public static class QuoteService
{
    /// <summary>
    /// 内置语料文件（随程序发布到输出目录）。
    /// 兼容两种落盘位置：输出根目录（csproj 用 Link）与 Assets 子目录（未设 Link 时的默认行为）。
    /// </summary>
    private static readonly string[] CorpusCandidates =
    {
        Path.Combine(AppContext.BaseDirectory, "corpus.json"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "corpus.json")
    };

    /// <summary>种子化只需成功判定一次，之后短路，避免每次取句都读一遍 3598 条语料。</summary>
    private static bool _seeded;

    /// <summary>并发保护：多处 await 同时触发种子化时避免重复写盘。</summary>
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    /// <summary>当前展示句（占位句为默认值）。</summary>
    public static Quote Current { get; private set; } = new Quote
    {
        English = "The best way out is always through.",
        Chinese = "最好的出路永远都是走下去。",
        Author = "Robert Frost"
    };

    /// <summary>
    /// 首次运行时把内置 corpus.json 灌入 AppData 的 data.json。
    /// data.json 已有语料则跳过（用户已有数据优先，绝不覆盖）；
    /// corpus.json 缺失/损坏一律静默跳过，不影响其他功能。
    /// </summary>
    public static async Task EnsureSeededAsync()
    {
        if (_seeded) return;

        await SeedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_seeded) return;

            var data = await DataStore.ReadAsync().ConfigureAwait(false);
            if (data.Quotes.Count > 0)
            {
                _seeded = true;
                return;
            }

            var seed = await LoadCorpusAsync().ConfigureAwait(false);
            if (seed.Count == 0) return; // 没读到语料，下次仍可重试

            data.Quotes = seed;
            await DataStore.WriteAsync(data).ConfigureAwait(false);
            _seeded = true;
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    /// <summary>读取并解析 corpus.json；任何失败返回空列表。</summary>
    private static async Task<List<Quote>> LoadCorpusAsync()
    {
        var result = new List<Quote>();
        try
        {
            var file = Array.Find(CorpusCandidates, File.Exists);
            if (file == null) return result;

            var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<CorpusEntry>>(json, DataStore.JsonOptions);
            if (entries == null) return result;

            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.English)) continue;

                var q = new Quote
                {
                    English = e.English,
                    Chinese = e.Chinese,
                    Author = e.Author,
                    Date = e.Date
                };
                // 内置语料 FetchedAt 为 null，保留 Quote 的默认值（入库时间）
                if (e.FetchedAt.HasValue) q.FetchedAt = e.FetchedAt.Value;
                result.Add(q);
            }
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            result.Clear();
        }
        return result;
    }

    /// <summary>今日日期字符串，与 Quote.Date 同格式（"yyyy-MM-dd"）。</summary>
    private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>该语录是否属于"今天"（Date 缺失或为往日均视为否）。</summary>
    private static bool IsToday(Quote? q) => q != null && q.Date == TodayKey;

    /// <summary>
    /// 抓取/刷新当日句。
    /// force=false 且 data.json 已有"当天"的今日句时直接返回；否则调 ShanbayService，
    /// 成功则写 data.json（today_quote + 按 Date 去重追加 quotes）并更新 Current。
    /// </summary>
    public static async Task<Quote?> FetchAsync(bool force)
    {
        try
        {
            // 先种子化：否则首次抓取会把仅含 1 条的 quotes 写盘，
            // 之后 EnsureSeededAsync 因"已有语料"永久跳过，内置语料再也进不来。
            await EnsureSeededAsync().ConfigureAwait(false);

            if (!force)
            {
                var today = await GetTodayAsync().ConfigureAwait(false);
                // 必须校验日期：跨天后 TodayQuote 仍是昨天那条，
                // 只判 null 会让 0 点自动更新永远短路、再也不联网。
                if (IsToday(today)) return today;
            }

            var quote = await ShanbayService.FetchTodayAsync().ConfigureAwait(false);
            if (quote == null) return null;

            var data = await DataStore.ReadAsync().ConfigureAwait(false);
            data.TodayQuote = quote;
            data.Quotes.RemoveAll(q => q.Date == quote.Date);
            data.Quotes.Insert(0, quote);
            await DataStore.WriteAsync(data).ConfigureAwait(false);

            Current = quote;
            return quote;
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return null;
        }
    }

    /// <summary>
    /// 手动更新今日语录（按当天去重，供"立即更新"按钮调用）。
    /// 始终从 API 取数，不再因本地已有当天句而短路。
    /// 三态返回：
    /// <list type="bullet">
    /// <item>(新句, false, false)：联网抓取成功并已写盘；</item>
    /// <item>(null, false, NetworkFailed=true)：联网失败或发生异常。</item>
    /// </list>
    /// 注意：成功时 AlreadyUpToDate 恒为 false（总是联网取数），设置页会走"已更新今日句子"分支。
    /// </summary>
    public static async Task<(Quote? Quote, bool AlreadyUpToDate, bool NetworkFailed)> UpdateTodayAsync()
    {
        try
        {
            // 与 FetchAsync 一致：先种子化，避免首次写盘挡住内置语料灌入
            await EnsureSeededAsync().ConfigureAwait(false);

            // "立即更新"始终从 API 取数，不再因本地已有当天句而短路。
            var quote = await ShanbayService.FetchTodayAsync().ConfigureAwait(false);
            if (quote == null) return (null, false, true);          // 联网失败

            var data = await DataStore.ReadAsync().ConfigureAwait(false);
            data.TodayQuote = quote;
            data.Quotes.RemoveAll(q => q.Date == quote.Date);        // 按日期去重，避免重复
            data.Quotes.Insert(0, quote);
            await DataStore.WriteAsync(data).ConfigureAwait(false);

            Current = quote;
            return (quote, false, false);                            // 成功（已从 API 取得）
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return (null, false, true);
        }
    }

    /// <summary>返回 data.json 中的今日句；无则 null。</summary>
    public static async Task<Quote?> GetTodayAsync()
    {
        var data = await DataStore.ReadAsync().ConfigureAwait(false);
        return data.TodayQuote;
    }

    /// <summary>从本地语料随机取一句并更新 Current；语料为空返回占位句。</summary>
    public static async Task<Quote> GetRandomAsync()
    {
        try
        {
            await EnsureSeededAsync().ConfigureAwait(false);

            var data = await DataStore.ReadAsync().ConfigureAwait(false);
            if (data.Quotes.Count > 0)
            {
                var q = data.Quotes[Random.Shared.Next(data.Quotes.Count)];
                Current = q;
                return q;
            }
        }
        catch (Exception ex)
        {
            // 忽略读取异常，回退占位句
            App.LogWarn(ex);
        }
        return Current;
    }

    /// <summary>本地语料条数（quotes 数组长度）。</summary>
    public static async Task<int> GetQuoteCountAsync()
    {
        await EnsureSeededAsync().ConfigureAwait(false);

        var data = await DataStore.ReadAsync().ConfigureAwait(false);
        return data.Quotes.Count;
    }
}
