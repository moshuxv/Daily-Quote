namespace 每日一句.Models;

/// <summary>
/// 一句话语料实体（对应 data.json 中单条记录）
/// </summary>
public class Quote
{
    /// <summary>英文原文</summary>
    public string English { get; set; } = "";

    /// <summary>中文翻译</summary>
    public string Chinese { get; set; } = "";

    /// <summary>作者（可为空）</summary>
    public string Author { get; set; } = "";

    /// <summary>所属日期，格式 "2026-08-09"</summary>
    public string Date { get; set; } = "";

    /// <summary>抓取时间戳</summary>
    public DateTime FetchedAt { get; set; } = DateTime.Now;
}
