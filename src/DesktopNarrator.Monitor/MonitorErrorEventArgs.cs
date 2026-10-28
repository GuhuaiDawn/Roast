namespace DesktopNarrator.Monitor;

/// <summary>传递失败操作和原始异常，供应用层记录诊断；不包含用户输入内容。</summary>
public sealed class MonitorErrorEventArgs : EventArgs
{
    public MonitorErrorEventArgs(string operation, Exception exception)
    {
        Operation = operation;
        Exception = exception;
    }

    /// <summary>失败的 API、监测步骤或订阅回调名称。</summary>
    public string Operation { get; }

    /// <summary>保留原始异常，方便定位权限、进程退出等边界情况。</summary>
    public Exception Exception { get; }
}
