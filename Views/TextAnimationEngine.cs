using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using 每日一句.Models;

namespace 每日一句;

/// <summary>
/// 通用文字动画引擎：对任意三个 TextBlock（英文 / 中文 / 署名）播放所选文字动画效果。
/// 浮窗与设置页预览共用同一套实现，保证"所见即所得"。
///
/// 锁尺寸机制：动画期间（打字机/解密文本会先清空再逐字出现，逐字符动画先做全透明再淡入）
/// 文字量随时变化，若不锁死宿主尺寸卡片会抖动。故播放前用宿主最终尺寸冻结、播放后还原。
/// 浮窗宿主是 Window（需附带翻转 SizeToContent），预览卡片宿主是 Border，二者通过
/// onLock / onUnlock 回调各自实现，引擎本身不关心宿主类型。
/// </summary>
public sealed class TextAnimationEngine
{
    private readonly TextBlock _eng;
    private readonly TextBlock _zh;
    private readonly TextBlock _author;
    private readonly Action _updateLayout;
    private readonly Action _onLock;
    private readonly Action _onUnlock;

    private readonly DispatcherTimer _typewriterTimer;
    private readonly DispatcherTimer _cursorTimer;
    private readonly DispatcherTimer _decryptTimer;
    private readonly DispatcherTimer _charFxTimer;

    private TypewriterJob? _tw;
    private DecryptJob? _dw;
    private List<CharFxSlot>? _charFxSlots;
    private DateTime _charFxStart;
    private double _charFxTotal;
    private int _charFxToken;
    private bool _cursorOn = true;
    private Color _textColor = Colors.White;

    // 上次播放的完整句子，Stop() 时用于还原文字（避免残留半句/乱码）
    private string? _lastEng;
    private string? _lastZh;
    private string? _lastAuthor;

    private const string DecryptCharset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz!@#$%^&*()_+";

    private const double GenStaggerMs = 14.0;
    private const double GenDurationMs = 600.0;
    private const double GenMaxBlur = 10.0;

    private const double PullStaggerMs = 50.0;
    private const double PullDurationMs = 400.0;
    private const double PullTranslateFactor = 0.8;

    private const string CursorChar = "|";

    public TextAnimationEngine(TextBlock eng, TextBlock zh, TextBlock author,
                                Action updateLayout, Action onLock, Action onUnlock)
    {
        _eng = eng;
        _zh = zh;
        _author = author;
        _updateLayout = updateLayout;
        _onLock = onLock;
        _onUnlock = onUnlock;

        _typewriterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
        _typewriterTimer.Tick += OnTypewriterTick;

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _cursorTimer.Tick += OnCursorTick;

        _decryptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _decryptTimer.Tick += OnDecryptTick;

        _charFxTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _charFxTimer.Tick += OnCharFxTick;
    }

    /// <summary>是否有动画正在进行（供停用动画时判断是否需立即收尾）。</summary>
    public bool IsAnimating =>
        _typewriterTimer.IsEnabled || _cursorTimer.IsEnabled ||
        _decryptTimer.IsEnabled || _charFxSlots is not null;

    /// <summary>按所选效果播放文字动画。</summary>
    public void Play(Quote q, string effect, Color textColor)
    {
        _textColor = textColor;
        switch (effect)
        {
            case "解密文本":
                RenderDecrypted(q);
                break;
            case "文本生成效果":
                RenderGenerated(q);
                break;
            case "文本上浮":
                RenderPullup(q);
                break;
            case "打字机":
            default:
                RenderTypewriter(q);
                break;
        }
    }

    /// <summary>
    /// 立即停止所有动画并还原为完整文字、解锁宿主尺寸。
    /// 用于：关闭动画、切换句子、窗口关闭、以及设置页预览切回非动画态。
    /// </summary>
    public void Stop()
    {
        StopTimers();
        if (_lastEng is not null)
        {
            _eng.Text = _lastEng;
            _zh.Text = _lastZh;
            _author.Text = _lastAuthor;
        }
        _onUnlock();
    }

    // ===================== 句子渲染分发 =====================

    /// <summary>非动画模式：直接显示完整文字（由调用方负责写入 Text）。</summary>
    public void SetPlainText(Quote q)
    {
        Stop();
        _eng.Text = q.English ?? "";
        _zh.Text = q.Chinese ?? "";
        _author.Text = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;
    }

    // ===================== 打字机 =====================

    private void RenderTypewriter(Quote q)
    {
        StopTimers();
        _tw = null;
        _dw = null;
        _charFxSlots = null;

        string eng = q.English ?? "";
        string zh = q.Chinese ?? "";
        string author = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;
        _lastEng = eng; _lastZh = zh; _lastAuthor = author;

        _onUnlock();
        _eng.Text = eng;
        _zh.Text = zh;
        _author.Text = author;
        _updateLayout();
        _onLock();

        _eng.Text = "";
        _zh.Text = "";
        _author.Text = "";

        _tw = new TypewriterJob(eng, zh, author);
        if (_tw.TotalLength == 0)
        {
            FinishTypewriter();
            return;
        }

        _cursorOn = true;
        _typewriterTimer.Start();
        _cursorTimer.Start();
        RenderSegments(_cursorOn);
    }

    private void OnTypewriterTick(object? sender, EventArgs e)
    {
        if (_tw is null) return;

        const int step = 2;
        while (true)
        {
            string seg = _tw.CurrentSegment;
            int segLen = seg.Length;

            if (_tw.Index < segLen)
            {
                _tw.Index = Math.Min(segLen, _tw.Index + step);
                RenderSegments(_cursorOn);
                return;
            }

            if (_tw.Segment >= 2)
            {
                FinishTypewriter();
                return;
            }
            _tw.Segment++;
            _tw.Index = 0;
        }
    }

    private void OnCursorTick(object? sender, EventArgs e)
    {
        if (_tw is null) return;
        _cursorOn = !_cursorOn;
        RenderSegments(_cursorOn);
    }

    private void RenderSegments(bool cursorOn)
    {
        if (_tw is null) return;
        int seg = _tw.Segment;
        int idx = _tw.Index;

        string eng = seg > 0 ? _tw.English : SafeSub(_tw.English, idx);
        string zh = seg > 1 ? _tw.Chinese : (seg == 1 ? SafeSub(_tw.Chinese, idx) : "");
        string au = seg > 2 ? _tw.Author : (seg == 2 ? SafeSub(_tw.Author, idx) : "");

        SetSegText(_eng, eng, cursorOn && seg == 0);
        SetSegText(_zh, zh, cursorOn && seg == 1);
        SetSegText(_author, au, cursorOn && seg == 2);
    }

    private static string SafeSub(string s, int count)
        => count >= s.Length ? s : s.Substring(0, count);

    private static void SetSegText(TextBlock tb, string text, bool cursor)
        => tb.Text = cursor ? text + CursorChar : text;

    private void FinishTypewriter()
    {
        _typewriterTimer.Stop();
        _cursorTimer.Stop();
        if (_tw is not null)
        {
            _eng.Text = _tw.English;
            _zh.Text = _tw.Chinese;
            _author.Text = _tw.Author;
        }
        _onUnlock();
        _tw = null;
    }

    // ===================== 解密文本 =====================

    private void RenderDecrypted(Quote q)
    {
        StopTimers();
        _tw = null;
        _dw = null;
        _charFxSlots = null;

        string eng = q.English ?? "";
        string zh = q.Chinese ?? "";
        string author = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;
        _lastEng = eng; _lastZh = zh; _lastAuthor = author;

        _onUnlock();
        _eng.Text = eng;
        _zh.Text = zh;
        _author.Text = author;
        _updateLayout();
        _onLock();

        _eng.Text = "";
        _zh.Text = "";
        _author.Text = "";

        int lenE = eng.Length;
        int lenZ = zh.Length;
        _dw = new DecryptJob(eng, zh, author, engStart: 0, zhStart: lenE + 1, authorStart: lenE + 1 + lenZ + 1);
        if (_dw.TotalLength == 0)
        {
            FinishDecrypt();
            return;
        }

        _decryptTimer.Start();
        RenderDecrypt();
    }

    private void OnDecryptTick(object? sender, EventArgs e)
    {
        if (_dw is null) return;

        const int step = 2;
        _dw.Revealed = Math.Min(_dw.TotalLength, _dw.Revealed + step);
        if (_dw.Revealed >= _dw.TotalLength)
        {
            FinishDecrypt();
            return;
        }
        RenderDecrypt();
    }

    private void RenderDecrypt()
    {
        if (_dw is null) return;
        int revealed = _dw.Revealed;
        _eng.Text = Scramble(_dw.Eng, _dw.EngStart, revealed);
        _zh.Text = Scramble(_dw.Zh, _dw.ZhStart, revealed);
        _author.Text = Scramble(_dw.Author, _dw.AuthorStart, revealed);
    }

    private static string Scramble(string trueStr, int segStart, int revealed)
    {
        if (revealed >= segStart + trueStr.Length) return trueStr;

        var sb = new StringBuilder(trueStr.Length);
        for (int i = 0; i < trueStr.Length; i++)
        {
            char c = trueStr[i];
            int gi = segStart + i;
            if (gi < revealed)
            {
                sb.Append(c);
            }
            else if (c == ' ' || c == '\n')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(DecryptCharset[Random.Shared.Next(DecryptCharset.Length)]);
            }
        }
        return sb.ToString();
    }

    private void FinishDecrypt()
    {
        _decryptTimer.Stop();
        if (_dw is not null)
        {
            _eng.Text = _dw.Eng;
            _zh.Text = _dw.Zh;
            _author.Text = _dw.Author;
        }
        _onUnlock();
        _dw = null;
    }

    // ===================== 逐字符动画（文本生成 / 文本上浮 共用） =====================

    private void RenderCharFx(Quote q, double staggerMs, double durationMs, bool blockBlur, double translateFactor)
    {
        StopTimers();
        _tw = null;
        _dw = null;

        string eng = q.English ?? "";
        string zh = q.Chinese ?? "";
        string author = string.IsNullOrWhiteSpace(q.Author) ? "" : "— " + q.Author;
        _lastEng = eng; _lastZh = zh; _lastAuthor = author;

        _onUnlock();
        _eng.Text = eng;
        _zh.Text = zh;
        _author.Text = author;
        _updateLayout();
        _onLock();

        Color c = _textColor;
        var slots = new List<CharFxSlot>();
        BuildCharFxSlots(_eng, eng, c, slots, translateFactor);
        BuildCharFxSlots(_zh, zh, c, slots, translateFactor);
        BuildCharFxSlots(_author, author, c, slots, translateFactor);
        for (int i = 0; i < slots.Count; i++) slots[i].Order = i;

        _charFxSlots = slots;
        if (slots.Count == 0)
        {
            FinishCharFx();
            return;
        }

        double total = (slots.Count - 1) * staggerMs + durationMs;
        _charFxStart = DateTime.UtcNow;
        _charFxTotal = total;
        _charFxToken++;

        int myToken = _charFxToken;

        if (blockBlur)
        {
            foreach (var tb in new[] { _eng, _zh, _author })
            {
                if (string.IsNullOrEmpty(tb.Text)) continue;
                var bb = new BlurEffect { Radius = GenMaxBlur };
                tb.Effect = bb;
                var ba = new DoubleAnimation(GenMaxBlur, 0, TimeSpan.FromMilliseconds(total));
                bb.BeginAnimation(BlurEffect.RadiusProperty, ba);
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            CharFxSlot s = slots[i];
            var begin = TimeSpan.FromMilliseconds(i * staggerMs);
            var dur = TimeSpan.FromMilliseconds(durationMs);
            var opAnim = new DoubleAnimation(0, 1, dur) { BeginTime = begin };
            if (i == slots.Count - 1)
                opAnim.Completed += (_, _) => { if (_charFxToken == myToken) FinishCharFx(); };
            s.Brush.BeginAnimation(SolidColorBrush.OpacityProperty, opAnim);

            if (s.Transform is not null)
            {
                var tfAnim = new DoubleAnimation(s.StartY, 0, dur) { BeginTime = begin };
                s.Transform.BeginAnimation(TranslateTransform.YProperty, tfAnim);
            }
        }

        _charFxTimer.Start();
    }

    private void RenderGenerated(Quote q)
        => RenderCharFx(q, GenStaggerMs, GenDurationMs, blockBlur: true, translateFactor: 0);

    private void RenderPullup(Quote q)
        => RenderCharFx(q, PullStaggerMs, PullDurationMs, blockBlur: false, translateFactor: PullTranslateFactor);

    private void OnCharFxTick(object? sender, EventArgs e)
    {
        if (_charFxSlots is null) return;
        double elapsed = (DateTime.UtcNow - _charFxStart).TotalMilliseconds;
        if (elapsed >= _charFxTotal) FinishCharFx();
    }

    private static void BuildCharFxSlots(TextBlock tb, string text, Color c, List<CharFxSlot> slots, double translateFactor)
    {
        if (string.IsNullOrEmpty(text)) return;
        var fxCol = new TextEffectCollection();
        for (int i = 0; i < text.Length; i++)
        {
            var brush = new SolidColorBrush(c) { Opacity = 0 };
            var fx = new TextEffect
            {
                PositionStart = i,
                PositionCount = 1,
                Foreground = brush
            };
            TranslateTransform? tt = null;
            if (translateFactor > 0)
            {
                double y = translateFactor * tb.FontSize;
                tt = new TranslateTransform(0, y);
                fx.Transform = tt;
            }
            fxCol.Add(fx);
            slots.Add(new CharFxSlot { Tb = tb, Fx = fx, Brush = brush, Transform = tt, StartY = tt is null ? 0 : tt.Y });
        }
        tb.TextEffects = fxCol;
    }

    private void FinishCharFx()
    {
        _charFxTimer.Stop();
        if (_charFxSlots is null) return;
        ClearCharFxEffects();
        _onUnlock();
        _charFxSlots = null;
    }

    private void ClearCharFxEffects()
    {
        _eng.TextEffects = null;
        _zh.TextEffects = null;
        _author.TextEffects = null;
        _eng.Effect = null;
        _zh.Effect = null;
        _author.Effect = null;
    }

    private void StopTimers()
    {
        _typewriterTimer.Stop();
        _cursorTimer.Stop();
        _decryptTimer.Stop();
        _charFxTimer.Stop();
        ClearCharFxEffects();
        _charFxToken++;
        _tw = null;
        _dw = null;
        _charFxSlots = null;
    }

    // ===================== 任务状态 =====================

    private sealed class TypewriterJob
    {
        public string English { get; }
        public string Chinese { get; }
        public string Author { get; }
        public int Segment { get; set; }
        public int Index { get; set; }

        public TypewriterJob(string english, string chinese, string author)
        {
            English = english;
            Chinese = chinese;
            Author = author;
        }

        public string CurrentSegment => Segment switch
        {
            0 => English,
            1 => Chinese,
            _ => Author
        };

        public int TotalLength => English.Length + Chinese.Length + Author.Length;
    }

    private sealed class DecryptJob
    {
        public string Eng { get; }
        public string Zh { get; }
        public string Author { get; }
        public int EngStart { get; }
        public int ZhStart { get; }
        public int AuthorStart { get; }
        public int Revealed { get; set; }

        public DecryptJob(string eng, string zh, string author, int engStart, int zhStart, int authorStart)
        {
            Eng = eng;
            Zh = zh;
            Author = author;
            EngStart = engStart;
            ZhStart = zhStart;
            AuthorStart = authorStart;
            TotalLength = eng.Length + zh.Length + author.Length + 2;
        }

        public int TotalLength { get; }
    }

    private sealed class CharFxSlot
    {
        public TextBlock Tb = null!;
        public TextEffect Fx = null!;
        public SolidColorBrush Brush = null!;
        public TranslateTransform? Transform;
        public double StartY;
        public int Order;
    }
}
