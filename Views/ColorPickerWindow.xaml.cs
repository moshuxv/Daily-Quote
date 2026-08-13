using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 项目同时启用 UseWPF / UseWindowsForms。csproj 虽已移除 System.Drawing /
// System.Windows.Forms 的隐式 global using，但同名类型（Border/Color）仍易在
// 后续维护中被误引入，这里按项目惯例用别名钉死到 WPF 版本。
using Border = System.Windows.Controls.Border;
using Color = System.Windows.Media.Color;

namespace 每日一句;

/// <summary>
/// Photoshop 风格 HSB 拾色器（1670 万色）。
/// 色域面板（S×B 渐变）+ 色相滑块 + HSB/RGB/Hex 数值输入 + 当前色/新色对比预览。
/// 窗口打开时通过 <see cref="ThemeHelper.ApplyTo"/> 应用主题画刷。
/// </summary>
public partial class ColorPickerWindow : Window
{
    // ===== 常量 =====
    private const int FieldW = 256;
    private const int FieldH = 256;
    private const int HueW = 15;
    private const int HueH = 256;

    private static readonly Regex HexPattern =
        new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ===== 内部状态 =====
    private float _h, _s, _b; // 当前 HSB（数据源）
    private WriteableBitmap _fieldBmp = null!;
    private WriteableBitmap _hueBmp = null!;
    private bool _syncing;
    private string? _result;
    private Color _initialColor;

    // ===== 静态 Show 方法（签名不变）=====
    public static string? Show(Window? owner, string initialHex)
    {
        var win = new ColorPickerWindow(initialHex)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };
        win.ShowDialog();
        return win._result;
    }

    // ===== 构造 =====
    public ColorPickerWindow(string initialHex)
    {
        InitializeComponent();

        // 主题换肤
        ThemeHelper.ApplyTo(Resources, App.CurrentSettings.Theme);

        // 创建 WriteableBitmap
        _fieldBmp = new WriteableBitmap(FieldW, FieldH, 96, 96, PixelFormats.Bgra32, null);
        FieldImage.Source = _fieldBmp;

        _hueBmp = new WriteableBitmap(HueW, HueH, 96, 96, PixelFormats.Bgra32, null);
        HueImage.Source = _hueBmp;

        // 解析初始颜色
        _initialColor = TryParseHex(initialHex, out var c) ? c : Colors.White;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetCurrent(ToHex(_initialColor));
        CurrentPreview.Background = new SolidColorBrush(_initialColor);
        HexBox.Focus();
        HexBox.CaretIndex = HexBox.Text.Length;
    }

    // ==================== HSB ↔ RGB 算法 ====================

    private static (float R, float G, float B) HsbToRgb(float h, float s, float b)
    {
        if (s <= 0.0001f) { float v = b; return (v, v, v); }
        float c = b * s;
        float hh = h / 60f;
        float x = c * (1 - MathF.Abs(hh % 2 - 1));
        float m = b - c;
        float r = 0, g = 0, bl = 0;
        int sector = (int)hh;
        switch (sector)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; bl = x; break;
            case 3: g = x; bl = c; break;
            case 4: r = x; bl = c; break;
            default: r = c; bl = x; break;
        }
        return (r + m, g + m, bl + m);
    }

    private static (float H, float S, float B) RgbToHsb(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;
        float h = 0;
        if (delta > 0.0001f)
        {
            if (max == r) h = 60 * ((g - b) / delta % 6);
            else if (max == g) h = 60 * ((b - r) / delta + 2);
            else h = 60 * ((r - g) / delta + 4);
        }
        if (h < 0) h += 360;
        float s = max < 0.0001f ? 0 : delta / max;
        float bVal = max;
        return (h, s, bVal);
    }

    // ==================== 渲染 ====================

    /// <summary>渲染色域面板：固定当前 H，绘制 S×B 渐变。</summary>
    private void RenderField()
    {
        int w = FieldW, h = FieldH;
        var pixels = new int[w * h];
        float hue = _h;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float s = (float)x / w; // 0→1 左→右
            float b = 1f - (float)y / h; // 1→0 上→下（PS 上方更亮）
            var (r, g, bl) = HsbToRgb(hue, s, b);
            pixels[y * w + x] = (255 << 24) | ((byte)(r * 255) << 16) | ((byte)(g * 255) << 8) | (byte)(bl * 255);
        }
        _fieldBmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    /// <summary>渲染色相滑块：彩虹渐变（S=1, B=1）。</summary>
    private void RenderHueSlider()
    {
        int w = HueW, h = HueH;
        var pixels = new int[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float hue = (float)y / h * 360f;
            var (r, g, bl) = HsbToRgb(hue, 1f, 1f);
            pixels[y * w + x] = (255 << 24) | ((byte)(r * 255) << 16) | ((byte)(g * 255) << 8) | (byte)(bl * 255);
        }
        _hueBmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    // ==================== 光标更新 ====================

    private void UpdateFieldCursor()
    {
        double x = _s * FieldW - 5;
        double y = (1 - _b) * FieldH - 5;
        Canvas.SetLeft(FieldCursor, x);
        Canvas.SetTop(FieldCursor, y);
    }

    private void UpdateSliderCursor()
    {
        double y = _h / 360.0 * HueH - 1.5;
        Canvas.SetLeft(HueCursorL, 0);
        Canvas.SetTop(HueCursorL, y);
        Canvas.SetLeft(HueCursorR, HueW - 4);
        Canvas.SetTop(HueCursorR, y);
    }

    // ==================== 同步 ====================

    private void SetCurrent(string hex)
    {
        if (!TryParseHex(hex, out var c)) return;
        var (h, s, bv) = RgbToHsb(c.R / 255f, c.G / 255f, c.B / 255f);
        _h = h;
        _s = s;
        _b = bv;
        RenderField();
        RenderHueSlider();
        UpdateFieldCursor();
        UpdateSliderCursor();
        SyncAll();
    }

    /// <summary>把当前 HSB 同步到所有数值框、Hex 框、新色预览块（防递归）。</summary>
    private void SyncAll()
    {
        _syncing = true;
        try
        {
            var (r, g, bl) = HsbToRgb(_h, _s, _b);
            var color = Color.FromRgb(
                (byte)Math.Round(r * 255),
                (byte)Math.Round(g * 255),
                (byte)Math.Round(bl * 255));

            HBox.Text = ((int)Math.Round(_h)).ToString();
            SBox.Text = ((int)Math.Round(_s * 100)).ToString();
            BBox.Text = ((int)Math.Round(_b * 100)).ToString();
            RBox.Text = ((int)Math.Round(r * 255)).ToString();
            GBox.Text = ((int)Math.Round(g * 255)).ToString();
            BlueBox.Text = ((int)Math.Round(bl * 255)).ToString();

            HexBox.Text = ToHex(color);
            HexBox.Tag = null;

            NewPreview.Background = new SolidColorBrush(color);

            HBox.Tag = null;
            SBox.Tag = null;
            BBox.Tag = null;
            RBox.Tag = null;
            GBox.Tag = null;
            BlueBox.Tag = null;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ==================== 色域面板鼠标交互 ====================

    private void Field_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        UpdateFieldFromMouse(e.GetPosition((IInputElement)sender));
    }

    private void Field_MouseMove(object sender, MouseEventArgs e)
    {
        if (((UIElement)sender).IsMouseCaptured)
            UpdateFieldFromMouse(e.GetPosition((IInputElement)sender));
    }

    private void Field_MouseUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateFieldFromMouse(Point pos)
    {
        _s = Math.Clamp((float)(pos.X / FieldW), 0f, 1f);
        _b = Math.Clamp((float)(1.0 - pos.Y / FieldH), 0f, 1f);
        UpdateFieldCursor();
        SyncAll();
    }

    // ==================== 色相滑块鼠标交互 ====================

    private void Slider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        UpdateSliderFromMouse(e.GetPosition((IInputElement)sender));
    }

    private void Slider_MouseMove(object sender, MouseEventArgs e)
    {
        if (((UIElement)sender).IsMouseCaptured)
            UpdateSliderFromMouse(e.GetPosition((IInputElement)sender));
    }

    private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateSliderFromMouse(Point pos)
    {
        _h = Math.Clamp((float)(pos.Y / HueH * 360.0), 0f, 359.999f);
        RenderField(); // H 变了，重绘色域
        UpdateSliderCursor();
        SyncAll();
    }

    // ==================== 数值框输入 ====================

    private void HBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(HBox.Text, out int val) && val >= 0 && val <= 360)
        {
            HBox.Tag = null;
            _h = val;
            RenderField(); // H 变了，重绘色域
            UpdateSliderCursor();
            SyncAll();
        }
        else { HBox.Tag = "error"; }
    }

    private void SBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(SBox.Text, out int val) && val >= 0 && val <= 100)
        {
            SBox.Tag = null;
            _s = val / 100f;
            UpdateFieldCursor();
            SyncAll();
        }
        else { SBox.Tag = "error"; }
    }

    private void BBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(BBox.Text, out int val) && val >= 0 && val <= 100)
        {
            BBox.Tag = null;
            _b = val / 100f;
            UpdateFieldCursor();
            SyncAll();
        }
        else { BBox.Tag = "error"; }
    }

    private void RBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(RBox.Text, out int val) && val >= 0 && val <= 255)
        {
            RBox.Tag = null;
            var (_, g, bl) = HsbToRgb(_h, _s, _b);
            var (h, s, bv) = RgbToHsb(val / 255f, g, bl);
            _h = h; _s = s; _b = bv;
            RenderField();
            UpdateFieldCursor();
            UpdateSliderCursor();
            SyncAll();
        }
        else { RBox.Tag = "error"; }
    }

    private void GBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(GBox.Text, out int val) && val >= 0 && val <= 255)
        {
            GBox.Tag = null;
            var (r, _, bl) = HsbToRgb(_h, _s, _b);
            var (h, s, bv) = RgbToHsb(r, val / 255f, bl);
            _h = h; _s = s; _b = bv;
            RenderField();
            UpdateFieldCursor();
            UpdateSliderCursor();
            SyncAll();
        }
        else { GBox.Tag = "error"; }
    }

    private void BlueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (int.TryParse(BlueBox.Text, out int val) && val >= 0 && val <= 255)
        {
            BlueBox.Tag = null;
            var (r, g, _) = HsbToRgb(_h, _s, _b);
            var (h, s, bv) = RgbToHsb(r, g, val / 255f);
            _h = h; _s = s; _b = bv;
            RenderField();
            UpdateFieldCursor();
            UpdateSliderCursor();
            SyncAll();
        }
        else { BlueBox.Tag = "error"; }
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint();
        if (_syncing) return;
        if (TryParseHex(HexBox.Text, out var c))
        {
            HexBox.Tag = null;
            var (h, s, bv) = RgbToHsb(c.R / 255f, c.G / 255f, c.B / 255f);
            _h = h; _s = s; _b = bv;
            RenderField();
            UpdateFieldCursor();
            UpdateSliderCursor();
            SyncAll();
        }
        else { HexBox.Tag = "error"; }
    }

    // ==================== 按钮 ====================

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var (r, g, bl) = HsbToRgb(_h, _s, _b);
        var color = Color.FromRgb(
            (byte)Math.Round(r * 255),
            (byte)Math.Round(g * 255),
            (byte)Math.Round(bl * 255));
        _result = ToHex(color);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _result = null;
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                Confirm_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    // ==================== 标题栏拖动 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
        catch (InvalidOperationException ex)
        {
            App.LogWarn(ex);
        }
    }

    // ==================== 颜色转换工具 ====================

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim();
        if (!HexPattern.IsMatch(s)) return false;
        color = Color.FromRgb(
            Convert.ToByte(s.Substring(1, 2), 16),
            Convert.ToByte(s.Substring(3, 2), 16),
            Convert.ToByte(s.Substring(5, 2), 16));
        return true;
    }

    private void UpdateHint() =>
        HexHint.Visibility = HexBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}