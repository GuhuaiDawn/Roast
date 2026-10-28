namespace DesktopNarrator.Monitor;

/// <summary>空闲状态计算的输入；仅包含时间，不包含按键、鼠标坐标或输入内容。</summary>
/// <param name="Timestamp">本次观察的日期时间，用于事件展示，不用于计算持续时长。</param>
/// <param name="UptimeMilliseconds">64 位系统运行毫秒数，提供不受修改日期时间影响的计时基准。</param>
/// <param name="LastInputTick">Windows 返回的 32 位最后输入计数，可能回绕或非递增。</param>
public readonly record struct IdleInputSample(DateTimeOffset Timestamp, long UptimeMilliseconds, uint LastInputTick);

/// <summary>隔离系统 API；测试可注入模拟采样源，不必实际等待用户离开。</summary>
public interface IIdleInputSource
{
    /// <summary>同步读取一次时间快照；实现应及时返回，失败时抛异常交给监测器重试。</summary>
    IdleInputSample Read();
}
