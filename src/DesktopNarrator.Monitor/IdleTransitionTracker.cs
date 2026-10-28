using DesktopNarrator.Events;

namespace DesktopNarrator.Monitor;

/// <summary>
/// 单线程空闲状态机；用系统运行计时计算时长，用日期时间生成展示用的事件时间点。
/// 只在进入空闲和从空闲返回时发事件，不为持续空闲重复输出日志。
/// </summary>
public sealed class IdleTransitionTracker
{
    private readonly TimeSpan _threshold;
    private IdleInputSample? _previous;
    private long _lastInputUptime;

    /// <summary>设置无输入阈值；必须大于零，达到阈值即进入空闲。</summary>
    public IdleTransitionTracker(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    /// <summary>上一次有效观察后是否已经达到空闲阈值。</summary>
    public bool IsIdle { get; private set; }
    /// <summary>本次观察距离最后输入的时长，尚未达到阈值时也会更新。</summary>
    public TimeSpan CurrentIdleTime { get; private set; }

    /// <summary>
    /// 输入一次采样，返回按发生顺序排列的零至两个事件。
    /// 如果采样间隔很长，可能同时发现“曾经返回”和“随后再次空闲”。
    /// </summary>
    public IReadOnlyList<IMonitorEvent> Observe(IdleInputSample sample)
    {
        if (sample.UptimeMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(sample));
        if (_previous is { } previous && sample.UptimeMilliseconds < previous.UptimeMilliseconds)
            throw new ArgumentException("Uptime must not move backwards.", nameof(sample));

        var events = new List<IMonitorEvent>(2);
        if (_previous is null || sample.LastInputTick != _previous.Value.LastInputTick)
        {
            // 无符号差值处理 32 位计数回绕；首次采样无法还原超过一个完整周期的历史。
            var age = (long)unchecked((uint)sample.UptimeMilliseconds - sample.LastInputTick);
            // 接近一个完整周期的差值可能是最多约 60 秒的未来输入时间，保守视为刚输入。
            // 这是异常值处理启发式，并不代表能够准确重建任意合成输入的时间。
            if (age > uint.MaxValue - 60_000L)
                age = 0;
            var newInputUptime = Math.Max(0, sample.UptimeMilliseconds - age);
            if (_previous is not null)
            {
                // 输入计数可能非递增；若变化后的时间早于上次观察，按本次观察时间处理，
                // 避免产生负的返回时长，或把新输入误算成更长的空闲。
                if (newInputUptime < _previous.Value.UptimeMilliseconds)
                    newInputUptime = sample.UptimeMilliseconds;
                if (IsIdle)
                {
                    events.Add(new UserReturnedEvent(
                        sample.Timestamp - TimeSpan.FromMilliseconds(sample.UptimeMilliseconds - newInputUptime),
                        TimeSpan.FromMilliseconds(newInputUptime - _lastInputUptime), sample.Timestamp));
                    IsIdle = false;
                }
            }
            _lastInputUptime = newInputUptime;
        }

        // 输入计数未变化时保留 64 位基准，允许持续监测跨越多个 32 位回绕周期。
        CurrentIdleTime = TimeSpan.FromMilliseconds(sample.UptimeMilliseconds - _lastInputUptime);
        if (!IsIdle && CurrentIdleTime >= _threshold)
        {
            IsIdle = true;
            events.Add(new UserIdleEvent(sample.Timestamp - CurrentIdleTime, sample.Timestamp));
        }

        _previous = sample;
        return events;
    }
}
