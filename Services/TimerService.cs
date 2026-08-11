using System.Timers;

namespace 每日一句.Services;

/// <summary>
/// 简单分钟级定时器：以 1 分钟为基本 tick，onTick 收到剩余分钟数（倒计时，归零后重置）。
/// 注意：Elapsed 在非 UI 线程触发，UI 更新需自行调度到 Dispatcher。
/// </summary>
public static class TimerService
{
    private static System.Timers.Timer? _timer;
    private static int _intervalMinutes = 1;
    private static int _minutesLeft;

    /// <summary>启动定时器；intervalMinutes &lt;= 0 按 1 分钟处理；重复调用会先 Stop。</summary>
    public static void Start(Action<int> onTick, int intervalMinutes)
    {
        Stop();

        _intervalMinutes = Math.Max(1, intervalMinutes);
        _minutesLeft = _intervalMinutes;

        _timer = new System.Timers.Timer(60_000) { AutoReset = true };
        _timer.Elapsed += (_, _) =>
        {
            _minutesLeft--;
            if (_minutesLeft <= 0) _minutesLeft = _intervalMinutes;
            onTick?.Invoke(_minutesLeft);
        };
        _timer.Start();
    }

    /// <summary>停止并释放定时器。</summary>
    public static void Stop()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Dispose();
        _timer = null;
    }
}
