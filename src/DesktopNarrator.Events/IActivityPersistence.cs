namespace DesktopNarrator.Events;

/// <summary>非阻塞持久化入口；监测与会话聚合只提交已确认的数据，不依赖数据库实现。</summary>
public interface IActivityPersistence
{
    /// <summary>将事件和由它结束的会话一起排队；false 表示未接受，不能当成保存成功。</summary>
    bool TryRecord(IMonitorEvent monitorEvent, AppSession? completedSession = null);

    /// <summary>排队保存正常退出时的最后会话；只接受已结束会话。</summary>
    bool TrySaveSession(AppSession session);
}
