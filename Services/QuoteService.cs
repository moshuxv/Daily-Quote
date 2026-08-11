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

    /// <summary>
    /// 内存缓存：种子化后把整份 data.json 缓存在静态字段，避免每次取句/随机都从磁盘
    /// 重新读 + 反序列化 3598 条语料（否则每分钟刷新都会读 ~947KB）。写盘时同步刷新本缓存。
    /// </summary>
    private static QuoteDataFile? _cache;

    /// <summary>IO 串行化：保护"读缓存→改→写盘"的原子性，避免并发读写 data.json 互相覆盖。</summary>
    private static readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>当前展示句（占位句为默认值）。</summary>
    public static Quote Current { get; private set; } = new Quote
    {
        English = "The best way out is always through.",
        Chinese = "最好的出路永远都是走下去。",
        Author = "Robert Frost"
    };

    /// <summary>今日日期字符串，与 Quote.Date 同格式（"yyyy-MM-dd"）。</summary>
    private static string TodayKey => DateTime.Now.ToString("yyyy-MM-dd");

    /// <summary>该语录是否属于"今天"（Date 缺失或为往日均视为否）。</summary>
    private static bool IsToday(Quote? q) => q != null && q.Date == TodayKey;

    /// <summary>
    /// 读取（并热身）内存缓存。调用方须持有 _ioLock；缓存为空时才从磁盘加载，
    /// 之后所有读都走内存，不再触碰磁盘。
    /// </summary>
    private static async Task<QuoteDataFile> GetCachedAsync()
    {
        if (_cache is null) _cache = await DataStore.ReadAsync().ConfigureAwait(false);
        return _cache;
    }

    /// <summary>
    /// 种子化（调用方须持有 _ioLock）：仅首次把 corpus.json 灌入 data.json；
    /// 之后 _seeded 短路。data.json 已有语料则跳过（用户已有数据优先，绝不覆盖）。
    /// </summary>
    private static async Task SeedIfNeededAsync()
    {
        if (_seeded) return;
        var data = await GetCachedAsync().ConfigureAwait(false);
        if (data.Quotes.Count > 0)
        {
            _seeded = true;
            return;
        }
        var seed = await LoadCorpusAsync().ConfigureAwait(false);
        if (seed.Count == 0) return; // 没读到语料，下次仍可重试
        data.Quotes = seed;
        await DataStore.WriteAsync(data).ConfigureAwait(false);
        _cache = data;
        _seeded = true;
    }

    /// <summary>首次运行时把内置 corpus.json 灌入 AppData 的 data.json（对外公开入口）。</summary>
    public static async Task EnsureSeededAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SeedIfNeededAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
        finally
        {
            _ioLock.Release();
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

    /// <summary>
    /// 抓取/刷新当日句（数据层增量：每次只新增/更新当天那一条，按 Date 去重，写盘仍是整文件）。
    /// 内存缓存使"判定今日句""写回"都不再从磁盘重新读 3598 条语料。
    /// </summary>
    public static async Task<Quote?> FetchAsync(bool force)
    {
        try
        {
            // 阶段1：持锁快速判定是否需要联网（无网络耗时）
            bool needNetwork;
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SeedIfNeededAsync().ConfigureAwait(false);
                var data = await GetCachedAsync().ConfigureAwait(false);
                needNetwork = force || !IsToday(data.TodayQuote);
            }
            finally
            {
                _ioLock.Release();
            }

            // 已是最新今日句，无需联网：直接返回缓存中的今日句
            if (!needNetwork)
            {
                var cached = await GetCachedAsync().ConfigureAwait(false);
                return cached.TodayQuote;
            }

            // 阶段2：联网抓取（不持锁，避免 10s 超时阻塞其它操作）
            var quote = await ShanbayService.FetchTodayAsync().ConfigureAwait(false);
            if (quote == null) return null;

            // 阶段3：持锁写回（重新取缓存，可能已被其它线程更新过）
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = await GetCachedAsync().ConfigureAwait(false);
                data.TodayQuote = quote;
                data.Quotes.RemoveAll(q => q.Date == quote.Date);
                data.Quotes.Insert(0, quote);
                await DataStore.WriteAsync(data).ConfigureAwait(false);
                _cache = data;
            }
            finally
            {
                _ioLock.Release();
            }

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
    /// 手动更新今日语录（按当天去重，供"立即更新"按钮调用）。始终从 API 取数。
    /// 三态返回：(新句, false, false) 成功 / (null, false, NetworkFailed=true) 联网失败。
    /// </summary>
    public static async Task<(Quote? Quote, bool AlreadyUpToDate, bool NetworkFailed)> UpdateTodayAsync()
    {
        try
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try { await SeedIfNeededAsync().ConfigureAwait(false); }
            finally { _ioLock.Release(); }

            var quote = await ShanbayService.FetchTodayAsync().ConfigureAwait(false);
            if (quote == null) return (null, false, true); // 联网失败

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = await GetCachedAsync().ConfigureAwait(false);
                data.TodayQuote = quote;
                data.Quotes.RemoveAll(q => q.Date == quote.Date); // 按日期去重，避免重复
                data.Quotes.Insert(0, quote);
                await DataStore.WriteAsync(data).ConfigureAwait(false);
                _cache = data;
            }
            finally
            {
                _ioLock.Release();
            }

            Current = quote;
            return (quote, false, false); // 成功（已从 API 取得）
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
            return (null, false, true);
        }
    }

    /// <summary>返回当前缓存中的今日句；无则 null。</summary>
    public static async Task<Quote?> GetTodayAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await GetCachedAsync().ConfigureAwait(false)).TodayQuote;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>从内存语料随机取一句并更新 Current；语料为空返回占位句。</summary>
    public static async Task<Quote> GetRandomAsync()
    {
        try
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await SeedIfNeededAsync().ConfigureAwait(false);
                var data = await GetCachedAsync().ConfigureAwait(false);
                if (data.Quotes.Count > 0)
                {
                    var q = data.Quotes[Random.Shared.Next(data.Quotes.Count)];
                    Current = q;
                    return q;
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch (Exception ex)
        {
            App.LogWarn(ex);
        }
        return Current;
    }

    /// <summary>内存语料条数（Quotes 数组长度）。</summary>
    public static async Task<int> GetQuoteCountAsync()
    {
        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SeedIfNeededAsync().ConfigureAwait(false);
            return (await GetCachedAsync().ConfigureAwait(false)).Quotes.Count;
        }
        finally
        {
            _ioLock.Release();
        }
    }
}
