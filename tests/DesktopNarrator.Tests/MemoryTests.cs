using System.Diagnostics;
using System.IO;
using DesktopNarrator.Events;
using DesktopNarrator.Analysis;
using DesktopNarrator.Memory;
using Microsoft.Data.Sqlite;

/// <summary>在独立临时数据库中验证真正的 SQLite 持久化、日期统计和故障恢复，不碰用户数据。</summary>
internal static class MemoryTests
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8));
    internal static readonly DateOnly Day = new(2026, 10, 28);
    internal static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Tests+08", TimeSpan.FromHours(8), "Tests+08", "Tests+08");

    /// <summary>重新创建仓储模拟重启，保留原始偏移、亚秒精度、中文和包含引号的应用名称。</summary>
    public static void RoundTrip()
    {
        using var files = new TestDatabase();
        var session = Session("Code'; DROP TABLE Events;--", 90.125);
        files.Store.Initialize();
        files.Store.WriteBatch([new(null, session)]);
        var reopened = new SqliteActivityStore(files.Path);
        reopened.Initialize();
        var history = reopened.ReadDay(Day, Zone);
        Check(history.Sessions.Single() == session, "Session changed after reopening");
        Check(history.Totals.Single().TotalForegroundSeconds == 90.125, "Duration precision was lost");
        Check(reopened.ReadEvents(Day, Zone).Count == 0, "Parameter text was interpreted as SQL");
    }

    /// <summary>相同 ID 重试不重复计入事件和会话。</summary>
    public static void Idempotency()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var record = StoredMonitorEvent.From(new UserIdleEvent(Start.AddMinutes(-5), Start));
        var batch = new[] { new ActivityWrite(record, Session("Code", 12)) };
        files.Store.WriteBatch(batch);
        files.Store.WriteBatch(batch);
        Check(files.Store.ReadDay(Day, Zone).SessionCount == 1, "Duplicate session inserted");
        Check(files.Store.ReadEvents(Day, Zone).Count == 1, "Duplicate event inserted");
    }

    /// <summary>批次后半段失败时，前半段事件和会话也不能部分提交。</summary>
    public static void AtomicRollback()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var first = new ActivityWrite(StoredMonitorEvent.From(new UserIdleEvent(Start, Start)), Session("Code", 10));
        var invalid = Session("Chrome", 20) with { EndTime = null, Duration = null };
        Throws<ArgumentException>(() => files.Store.WriteBatch([first, new(null, invalid)]));
        Check(files.Store.ReadDay(Day, Zone).SessionCount == 0, "Partial session commit");
        Check(files.Store.ReadEvents(Day, Zone).Count == 0, "Partial event commit");
    }

    /// <summary>一段跨午夜会话必须分配到两天，不能把全部时长算给开始日。</summary>
    public static void CrossMidnight()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var start = new DateTimeOffset(2026, 10, 28, 23, 59, 30, TimeSpan.FromHours(8));
        files.Store.WriteBatch([new(null, Session("Code", 90, start))]);
        Check(files.Store.ReadDay(Day, Zone).Totals.Single().TotalForegroundSeconds == 30, "First-day overlap wrong");
        Check(files.Store.ReadDay(Day.AddDays(1), Zone).Totals.Single().TotalForegroundSeconds == 60, "Next-day overlap wrong");
        Check(files.Store.ReadDay(Day.AddDays(2), Zone).SessionCount == 0, "Session leaked beyond its interval");
    }

    /// <summary>秋季回拨的本地日期有 25 小时，春季跳时有 23 小时；日期边界不能固定加 24 小时。</summary>
    public static void DaylightSaving()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        files.Store.WriteBatch([
            new(null, Session("Code", 25 * 3600, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)))),
            new(null, Session("Code", 23 * 3600, new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5))))
        ]);
        Check(files.Store.ReadDay(new(2026, 11, 1), zone).Totals.Single().TotalForegroundSeconds == 90000, "25-hour day wrong");
        Check(files.Store.ReadDay(new(2026, 3, 8), zone).Totals.Single().TotalForegroundSeconds == 82800, "23-hour day wrong");
    }

    /// <summary>系统调时后原始结束时间可能倒退，但持久化总计仍使用单调时长。</summary>
    public static void ClockAdjustment()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var session = Session("Code", 30) with { EndTime = Start.AddHours(-2) };
        files.Store.WriteBatch([new(null, session)]);
        var result = files.Store.ReadDay(Day, Zone);
        Check(result.Totals.Single().TotalForegroundSeconds == 30, "Clock adjustment changed total");
        Check(result.Sessions.Single().EndTime == session.EndTime, "Original timestamp was overwritten");
    }

    /// <summary>分页只限制历史列表，总计仍覆盖全部记录；大小写不同的进程归为同一应用。</summary>
    public static void Pagination()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var writes = Enumerable.Range(0, 205).Select(i => new ActivityWrite(null,
            Session(i % 2 == 0 ? "Code" : "CODE", 1, Start.AddSeconds(i)))).ToArray();
        files.Store.WriteBatch(writes);
        var first = files.Store.ReadDay(Day, Zone);
        var second = files.Store.ReadDay(Day, Zone, 100);
        var third = files.Store.ReadDay(Day, Zone, 200);
        Check(first.Sessions.Count == 100 && second.Sessions.Count == 100 && third.Sessions.Count == 5, "Paging counts wrong");
        Check(first.Totals.Single().TotalForegroundSeconds == 205 && first.SessionCount == 205, "Totals were limited to a page");
        Check(first.Sessions.Concat(second.Sessions).Concat(third.Sessions).Select(s => s.Id).Distinct().Count() == 205, "Paging repeated rows");
    }

    /// <summary>未来模式版本和损坏数据库均须报错，不能自动删除用户数据来“修复”。</summary>
    public static void PreserveUnknownDatabase()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        files.Store.WriteBatch([new(null, Session("Code", 10))]);
        using (var connection = files.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }
        Throws<InvalidOperationException>(files.Store.Initialize);
        Check(files.Store.ReadDay(Day, Zone).SessionCount == 1, "Future database was overwritten");
        var corruptPath = System.IO.Path.Combine(files.DirectoryPath, "corrupt.db");
        var contents = Enumerable.Repeat((byte)42, 256).ToArray();
        File.WriteAllBytes(corruptPath, contents);
        Throws<SqliteException>(() => new SqliteActivityStore(corruptPath).Initialize());
        Check(File.ReadAllBytes(corruptPath).SequenceEqual(contents), "Corrupt database was overwritten");
    }

    /// <summary>数据库被其他连接锁住时提交仍快速返回；释放锁后同一项重试成功且没有重复。</summary>
    public static void LockedDatabaseRecovery()
    {
        using var files = new TestDatabase();
        using var writer = new ActivityPersistence(files.Store);
        using var failure = new ManualResetEventSlim();
        writer.StatusChanged += (_, status) => { if (!status.Healthy) failure.Set(); };
        writer.Start();
        Check(writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "Initialization failed");
        using (var connection = files.Open())
        using (var transaction = connection.BeginTransaction())
        {
            var watch = Stopwatch.StartNew();
            Check(writer.TrySaveSession(Session("Code", 30)), "Write not accepted");
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Producer blocked on database lock");
            Check(failure.Wait(TimeSpan.FromSeconds(4)), "Lock failure not reported");
            Check(writer.PendingCount == 1, "Failed write was discarded");
            transaction.Rollback();
        }
        Check(writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(), "Retry did not finish");
        Check(files.Store.ReadDay(Day, Zone).SessionCount == 1, "Retry lost/duplicated data");
        Check(writer.Shutdown(TimeSpan.FromSeconds(3)), "Shutdown did not drain");
    }

    /// <summary>目录暂不可用时仍启动工作线程并保留队列，恢复后自动完成建库和写入。</summary>
    public static void InitializationRecovery()
    {
        using var files = new TestDatabase();
        var blocker = System.IO.Path.Combine(files.DirectoryPath, "temporarily-file");
        using (File.Create(blocker)) { }
        var store = new SqliteActivityStore(System.IO.Path.Combine(blocker, "narrator.db"));
        using var failure = new ManualResetEventSlim();
        using var writer = new ActivityPersistence(store);
        writer.StatusChanged += (_, status) => { if (!status.Healthy) failure.Set(); };
        writer.Start();
        Check(writer.TrySaveSession(Session("Code", 20)), "Unavailable path rejected a valid queue item");
        Check(failure.Wait(TimeSpan.FromSeconds(3)), "Initialization failure was hidden");
        File.Delete(blocker); // 只删除本测试刚创建的单个阻挡文件，不处理用户目录。
        Check(writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(), "Initialization did not recover");
        Check(store.ReadDay(Day, Zone).SessionCount == 1, "Recovery lost queued data");
    }

    /// <summary>队列满时显式拒绝；无法排空时退出必须限时返回并报告尚未确认的写入。</summary>
    public static void QueueAndShutdownLimits()
    {
        using var files = new TestDatabase();
        using (var full = new ActivityPersistence(files.Store, capacity: 1))
        {
            Check(full.TrySaveSession(Session("Code", 1)), "First queue item rejected");
            Check(!full.TrySaveSession(Session("Code", 2)) && full.RejectedCount == 1, "Full queue silently lost data");
        }
        using var writer = new ActivityPersistence(files.Store);
        writer.Start();
        Check(writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "Writer did not start");
        using var connection = files.Open();
        using var transaction = connection.BeginTransaction();
        Check(writer.TrySaveSession(Session("Code", 3)), "Pending item rejected");
        var watch = Stopwatch.StartNew();
        Check(!writer.Shutdown(TimeSpan.FromMilliseconds(50)), "Locked shutdown reported success");
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Shutdown ignored deadline");
        Check(!writer.TrySaveSession(Session("Code", 4)), "Stopped writer accepted more data");
        transaction.Rollback();
    }

    /// <summary>事件记录仅包含受允许的时间和进程身份；未声明的新事件类型不能直接进入数据库。</summary>
    public static void EventPrivacy()
    {
        var app = new ForegroundApplication(1, "Code", "not-for-event-metadata", Start);
        var focused = StoredMonitorEvent.From(new ForegroundChangedEvent(null, app, Start));
        Check(focused.EventType == "AppFocused" && !focused.MetadataJson.Contains("not-for-event-metadata"), "Unexpected event fields persisted");
        var returned = StoredMonitorEvent.From(new UserReturnedEvent(Start, TimeSpan.FromSeconds(300), Start));
        Check(returned.MetadataJson.Contains("300"), "Idle duration missing");
        Throws<ArgumentException>(() => StoredMonitorEvent.From(new UnsupportedEvent()));
    }

    /// <summary>真实 v1 表升级为当前模式；既有会话/事件原样保留，重复初始化不会丢数据。</summary>
    public static void PatternSchemaMigration()
    {
        using var files = new TestDatabase();
        using (var connection = files.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE AppSessions (
                    Id TEXT PRIMARY KEY, ProcessName TEXT NOT NULL COLLATE NOCASE, FriendlyName TEXT NOT NULL,
                    StartTime TEXT NOT NULL, EndTime TEXT NOT NULL, StartUtcTicks INTEGER NOT NULL,
                    EffectiveEndUtcTicks INTEGER NOT NULL, DurationTicks INTEGER NOT NULL CHECK(DurationTicks >= 0));
                CREATE TABLE Events (Id TEXT PRIMARY KEY, EventType TEXT NOT NULL, Timestamp TEXT NOT NULL,
                    UtcTicks INTEGER NOT NULL, ProcessName TEXT, MetadataJson TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        var session = Session("Code", 480);
        var record = StoredMonitorEvent.From(new UserIdleEvent(Start, Start));
        files.Store.WriteBatch([new(record, session)]);
        files.Store.Initialize();
        files.Store.Initialize();
        Check(files.Store.ReadDay(Day, Zone).Sessions.Single() == session, "Migration changed old sessions");
        Check(files.Store.ReadEvents(Day, Zone).Single() == record, "Migration changed old events");
        Check(files.Store.ReadPatterns(Day, Zone).Count == 0, "Migration invented candidates");
        using var verification = files.Open();
        using var version = verification.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Check((long)version.ExecuteScalar()! == 3, "Version not upgraded");
    }

    /// <summary>候选、进程事件共用真实写入队列；重启和相同 ID 重试后证据及原始时间仍不变。</summary>
    public static void PatternPersistence()
    {
        using var files = new TestDatabase();
        var pattern = new DetectedPattern(Guid.NewGuid(), PatternType.LongSession, Start.AddMinutes(90), Start,
            "Code'测试", "前台驻留，包含无输入", "{\"DurationSeconds\":5400,\"IncludesIdle\":true}");
        using (var writer = new ActivityPersistence(files.Store))
        {
            writer.Start();
            Check(writer.TrySavePattern(pattern) && writer.TrySavePattern(pattern), "Pattern rejected");
            writer.TryRecord(new AppStartedEvent("Code", Start));
            writer.TryRecord(new AppClosedEvent("Code", Start.AddSeconds(10)));
            writer.TryRecord(new ProcessObservationResetEvent(Start.AddSeconds(11)));
            Check(writer.Shutdown(TimeSpan.FromSeconds(5)), "Pattern queue did not drain");
        }
        var reopened = new SqliteActivityStore(files.Path);
        Check(reopened.ReadPatterns(Day, Zone).Single() == pattern, "Pattern changed or duplicated");
        Check(reopened.ReadPatterns(Day.AddDays(1), Zone).Count == 0, "Pattern date filter wrong");
        Check(reopened.ReadEvents(Day, Zone).Select(x => x.EventType).Order().SequenceEqual(
            new[] { "AppClosed", "AppStarted", "ProcessObservationReset" }), "Lifecycle event whitelist missing");
        var writes = Enumerable.Range(0, 205).Select(i => new ActivityWrite(null, null,
            pattern with { Id = Guid.NewGuid(), EndTime = Start.AddSeconds(i + 1) })).ToArray();
        reopened.WriteBatch(writes);
        var limited = reopened.ReadPatterns(Day, Zone, 20);
        Check(limited.Count == 20 && limited[0].EndTime == Start.AddSeconds(205), "Pattern ordering/limit wrong");
    }

    /// <summary>候选写入与同一批中的其他数据一起回滚；不接受无效 ID 或非对象的证据。</summary>
    public static void PatternAtomicRollback()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var valid = new DetectedPattern(Guid.NewGuid(), PatternType.LongIdleReturn, Start, Start, null, "返回", "{}");
        Throws<ArgumentException>(() => files.Store.WriteBatch([
            new(null, Session("Code", 10), valid), new(null, null, valid with { Id = Guid.Empty })]));
        Check(files.Store.ReadDay(Day, Zone).SessionCount == 0 && files.Store.ReadPatterns(Day, Zone).Count == 0, "Pattern batch partially committed");
        Throws<ArgumentException>(() => SqliteActivityStore.ValidatePattern(valid with { EvidenceJson = "[]" }));
    }

    /// <summary>构造本测试专属 v2 数据库，升级后保留原始候选且不为旧候选伪造判定。</summary>
    public static void EvaluationSchemaMigration()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var old = InterestingnessTests.Candidate();
        var session = Session("Code", 10);
        files.Store.WriteBatch([new(null, session, old)]);
        using (var connection = files.Open())
        using (var command = connection.CreateCommand())
        {
            // 只移除独立夹具里刚创建、没有任何记录的 v3 表，得到与上一版一致的 v2 模式。
            command.CommandText = "DROP TABLE PatternEvaluations; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        files.Store.Initialize();
        files.Store.Initialize();
        Check(files.Store.ReadPatterns(Day, Zone).Single() == old, "Migration modified old candidate");
        Check(files.Store.ReadDay(Day, Zone).Sessions.Single() == session, "Migration modified session");
        Check(files.Store.ReadEvaluations(Day, Zone).Count == 0, "Migration invented a historical score");
        using var verification = files.Open();
        using var version = verification.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Check((long)version.ExecuteScalar()! == 3, "v2 was not upgraded");
    }

    /// <summary>通过与过滤结果都保存；重开后逐字段保留分数、分量、原因、策略版本和上下文。</summary>
    public static void EvaluationRoundTrip()
    {
        using var files = new TestDatabase();
        var accepted = InterestingnessTests.Candidate();
        var rejected = InterestingnessTests.Candidate(PatternType.ClosedThenReopened, "git", 55);
        var engine = new InterestingnessEngine();
        var evaluations = engine.EvaluateBatch([accepted, rejected], Start, TimeSpan.Zero).ToDictionary(x => x.PatternId);
        using (var writer = new ActivityPersistence(files.Store))
        {
            writer.Start();
            foreach (var pattern in new[] { accepted, rejected })
            {
                Check(writer.TrySavePattern(pattern, evaluations[pattern.Id]), "Assessment rejected by writer");
                Check(writer.TrySavePattern(pattern, evaluations[pattern.Id]), "Idempotent assessment retry rejected");
            }
            Check(writer.Shutdown(TimeSpan.FromSeconds(5)), "Assessment queue did not drain");
        }
        var reopened = new SqliteActivityStore(files.Path);
        var actual = reopened.ReadEvaluations(Day, Zone);
        Check(actual.Count == 2 && actual.Count(x => x.IsSelected) == 1, "Assessments lost or duplicated");
        foreach (var value in actual)
            Check(System.Text.Json.JsonSerializer.Serialize(value) == System.Text.Json.JsonSerializer.Serialize(evaluations[value.PatternId]), "Assessment snapshot changed");
        Check(reopened.ReadPatterns(Day, Zone).Count == 2, "Filtered facts were discarded");
        Check(reopened.ReadEvaluations(Day.AddDays(1), Zone).Count == 0 && reopened.ReadEvaluations(Day, Zone, 1).Count == 1, "Assessment date/limit wrong");
    }

    /// <summary>不匹配 ID、非有限数和自相矛盾的通过状态均拒绝；批次内事实与评分一起回滚。</summary>
    public static void EvaluationAtomicity()
    {
        using var files = new TestDatabase();
        files.Store.Initialize();
        var first = InterestingnessTests.Candidate();
        var second = InterestingnessTests.Candidate(process: "Chrome");
        var value = new InterestingnessEngine().EvaluateBatch([first], Start, TimeSpan.Zero).Single();
        Throws<ArgumentException>(() => files.Store.WriteBatch([new(null, null, first, value), new(null, null, second, value)]));
        Check(files.Store.ReadPatterns(Day, Zone).Count == 0 && files.Store.ReadEvaluations(Day, Zone).Count == 0, "Fact/assessment partially committed");
        Throws<ArgumentException>(() => files.Store.WriteBatch([new(null, null, null, value)]));
        Throws<ArgumentException>(() => SqliteActivityStore.ValidateEvaluation(first, value with { Score = double.NaN }));
        Throws<ArgumentException>(() => SqliteActivityStore.ValidateEvaluation(first, value with { Score = 10 }));
        Throws<ArgumentException>(() => SqliteActivityStore.ValidateEvaluation(first, value with { GlobalCooldownRemainingSeconds = 1 }));
    }

    internal static AppSession Session(string name, double seconds, DateTimeOffset? start = null)
    {
        var time = start ?? Start;
        return new(Guid.NewGuid(), name, "测试应用", time, time.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    private sealed record UnsupportedEvent : IMonitorEvent { public DateTimeOffset Timestamp => Start; }

    internal sealed class TestDatabase : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("DesktopNarrator.Tests-");
        public string DirectoryPath => _directory.FullName;
        public string Path => System.IO.Path.Combine(DirectoryPath, "narrator.db");
        public SqliteActivityStore Store => new(Path);
        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            return connection;
        }
        public void Dispose()
        {
            // 仅清理由本夹具创建的临时目录，防止测试配置错误导致误删其他文件。
            var full = System.IO.Path.GetFullPath(DirectoryPath);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !_directory.Name.StartsWith("DesktopNarrator.Tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            // WPF 取消的是查询结果，不会强制中断已经进入 SQLite 的原生读取。
            // 给这些短任务一个有界释放窗口；仍被占用则让测试失败，不能无限等待或吞掉错误。
            var cleanup = Stopwatch.StartNew();
            while (true)
            {
                try { _directory.Delete(recursive: true); return; }
                catch (IOException) when (cleanup.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(50); }
            }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
