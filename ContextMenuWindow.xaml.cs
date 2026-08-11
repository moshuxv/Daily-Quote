using System.Windows;
using System.Windows.Input;

namespace 每日一句;

/// <summary>右键菜单弹出窗口：独立顶层窗口，不被主窗口（挂在 WorkerW）的边界裁切。</summary>
public partial class ContextMenuWindow : Window
{
    private readonly WidgetWindow _owner;
    private bool _closing;

    public ContextMenuWindow(WidgetWindow owner)
    {
        InitializeComponent();
        _owner = owner;

        // 锁定 / 取消锁定 互斥显示：已锁定只显示「取消锁定」，未锁定只显示「锁定位置」
        if (App.CurrentSettings.LockPosition)
        {
            BtnLock.Visibility = Visibility.Collapsed;
            BtnUnlock.Visibility = Visibility.Visible;
        }
        else
        {
            BtnLock.Visibility = Visibility.Visible;
            BtnUnlock.Visibility = Visibility.Collapsed;
        }

        // 失焦（点击别处）即关闭
        Deactivated += (_, _) => SafeClose();
        // Esc 关闭
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SafeClose(); e.Handled = true; }
        };
    }

    /// <summary>
    /// 安全关闭：防止重复 Close 抛 InvalidOperationException。
    /// 点菜单项时按钮 Click 与窗口 Deactivated 会先后触发关闭；Close() 本身又会引发
    /// WM_ACTIVATE 再次进入 Deactivated，导致关闭过程中二次 Close 崩溃。
    /// </summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) { _owner.ActionRefresh(); SafeClose(); }
    private void BtnCopy_Click(object sender, RoutedEventArgs e) { _owner.ActionCopy(); SafeClose(); }
    private void BtnSettings_Click(object sender, RoutedEventArgs e) { _owner.ActionSettings(); SafeClose(); }
    private void BtnLock_Click(object sender, RoutedEventArgs e) { _owner.ActionLock(true); SafeClose(); }
    private void BtnUnlock_Click(object sender, RoutedEventArgs e) { _owner.ActionLock(false); SafeClose(); }
    private void BtnQuit_Click(object sender, RoutedEventArgs e) { _owner.ActionQuit(); SafeClose(); }
}
