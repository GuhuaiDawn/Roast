namespace DesktopNarrator.Memory;

/// <summary>查询用后台适配器；先短暂等待此前写入，再查询已提交快照，不阻塞 WPF 线程。</summary>
public sealed class HistoryQueryService(SqliteActivityStore store, ActivityPersistence? persistence = null)
{
    public string DatabasePath => store.DatabasePath;

    /// <summary>队列未及时排空时仍允许读已保存历史，并明确告知页面可能缺少最新数据。</summary>
    public async Task<(DayHistory History, bool WritesCaughtUp)> ReadDayAsync(
        DateOnly date, TimeZoneInfo zone, int offset = 0, CancellationToken cancellationToken = default)
    {
        var caughtUp = true;
        if (persistence is not null)
        {
            try { caughtUp = await persistence.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { caughtUp = false; }
        }
        var result = await Task.Run(() => store.ReadDay(date, zone, offset), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (result, caughtUp);
    }
}
