using System.Globalization;
using System.Text.Json;
using DesktopNarrator.Events;
using Microsoft.Data.Sqlite;

namespace DesktopNarrator.Memory;

/// <summary>
/// 同步仓储，只在后台执行；每次操作独占连接，不跨线程共享 SQLite 对象。
/// 模式不兼容时拒绝写入，不删除、覆盖或降级用户数据库。
/// </summary>
public sealed class SqliteActivityStore
{
    public string DatabasePath { get; }

    /// <summary>默认数据库属于当前用户；测试使用独立临时目录，不污染真实历史。</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopNarrator", "narrator.db");

    public SqliteActivityStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
    }

    /// <summary>以事务升级到 v3，仅增表/索引，保留 v1/v2 历史且不为旧候选补造评分。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is < 0 or > 3) throw new InvalidOperationException($"Database schema {version} is not supported (expected 0–3).");
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppSessions (
                Id TEXT PRIMARY KEY, ProcessName TEXT NOT NULL COLLATE NOCASE, FriendlyName TEXT NOT NULL,
                StartTime TEXT NOT NULL, EndTime TEXT NOT NULL, StartUtcTicks INTEGER NOT NULL,
                EffectiveEndUtcTicks INTEGER NOT NULL, DurationTicks INTEGER NOT NULL CHECK(DurationTicks >= 0));
            CREATE INDEX IF NOT EXISTS IX_Sessions_Time ON AppSessions(StartUtcTicks, EffectiveEndUtcTicks);
            CREATE TABLE IF NOT EXISTS Events (
                Id TEXT PRIMARY KEY, EventType TEXT NOT NULL, Timestamp TEXT NOT NULL,
                UtcTicks INTEGER NOT NULL, ProcessName TEXT, MetadataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Events_Time ON Events(UtcTicks);
            CREATE TABLE IF NOT EXISTS DetectedPatterns (
                Id TEXT PRIMARY KEY, PatternType TEXT NOT NULL, StartTime TEXT NOT NULL, EndTime TEXT NOT NULL,
                DetectedUtcTicks INTEGER NOT NULL, ProcessName TEXT, Summary TEXT NOT NULL, MetadataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Patterns_Time ON DetectedPatterns(DetectedUtcTicks);
            CREATE TABLE IF NOT EXISTS PatternEvaluations (
                PatternId TEXT PRIMARY KEY, EvaluatedAt TEXT NOT NULL, EvaluatedUtcTicks INTEGER NOT NULL,
                PolicyVersion TEXT NOT NULL, Score REAL NOT NULL CHECK(Score >= 0 AND Score <= 100),
                IsSelected INTEGER NOT NULL CHECK(IsSelected IN (0,1)), EvaluationJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Evaluations_Time ON PatternEvaluations(EvaluatedUtcTicks);
            PRAGMA user_version=3;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>批量事务保存；稳定 ID 的幂等插入确保失败后的重试不会把统计翻倍。</summary>
    public void WriteBatch(IReadOnlyList<ActivityWrite> writes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var write in writes)
        {
            // 单个工作项必须携带对应候选，禁止孤立评分；同批失败会整体回滚。
            if (write.Evaluation is { } evaluation)
            {
                if (write.Pattern is null) throw new ArgumentException("Evaluation requires its candidate.", nameof(writes));
                ValidateEvaluation(write.Pattern, evaluation);
            }
            if (write.Pattern is { } pattern)
            {
                ValidatePattern(pattern);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO DetectedPatterns(Id,PatternType,StartTime,EndTime,DetectedUtcTicks,ProcessName,Summary,MetadataJson)
                    VALUES($id,$type,$start,$end,$ticks,$process,$summary,$json) ON CONFLICT(Id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$id", pattern.Id.ToString("D"));
                command.Parameters.AddWithValue("$type", pattern.Type.ToString());
                command.Parameters.AddWithValue("$start", pattern.StartTime.ToString("O"));
                command.Parameters.AddWithValue("$end", pattern.EndTime.ToString("O"));
                command.Parameters.AddWithValue("$ticks", pattern.EndTime.UtcTicks);
                command.Parameters.AddWithValue("$process", (object?)pattern.ProcessName ?? DBNull.Value);
                command.Parameters.AddWithValue("$summary", pattern.Summary);
                command.Parameters.AddWithValue("$json", pattern.EvidenceJson);
                command.ExecuteNonQuery();
            }
            if (write.Evaluation is { } assessment)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO PatternEvaluations(PatternId,EvaluatedAt,EvaluatedUtcTicks,PolicyVersion,Score,IsSelected,EvaluationJson)
                    VALUES($id,$time,$ticks,$version,$score,$selected,$json) ON CONFLICT(PatternId) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$id", assessment.PatternId.ToString("D"));
                command.Parameters.AddWithValue("$time", assessment.EvaluatedAt.ToString("O"));
                command.Parameters.AddWithValue("$ticks", assessment.EvaluatedAt.UtcTicks);
                command.Parameters.AddWithValue("$version", assessment.PolicyVersion);
                command.Parameters.AddWithValue("$score", assessment.Score);
                command.Parameters.AddWithValue("$selected", assessment.IsSelected ? 1 : 0);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(assessment));
                command.ExecuteNonQuery();
            }
            if (write.Event is { } value)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO Events(Id,EventType,Timestamp,UtcTicks,ProcessName,MetadataJson)
                    VALUES($id,$type,$time,$ticks,$process,$json) ON CONFLICT(Id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$id", value.Id.ToString("D"));
                command.Parameters.AddWithValue("$type", value.EventType);
                command.Parameters.AddWithValue("$time", value.Timestamp.ToString("O"));
                command.Parameters.AddWithValue("$ticks", value.Timestamp.UtcTicks);
                command.Parameters.AddWithValue("$process", (object?)value.ProcessName ?? DBNull.Value);
                command.Parameters.AddWithValue("$json", value.MetadataJson);
                command.ExecuteNonQuery();
            }
            if (write.Session is { } session)
            {
                ValidateSession(session);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO AppSessions(Id,ProcessName,FriendlyName,StartTime,EndTime,StartUtcTicks,EffectiveEndUtcTicks,DurationTicks)
                    VALUES($id,$process,$friendly,$start,$end,$startTicks,$effectiveEnd,$duration) ON CONFLICT(Id) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
                command.Parameters.AddWithValue("$process", session.ProcessName);
                command.Parameters.AddWithValue("$friendly", session.FriendlyName);
                command.Parameters.AddWithValue("$start", session.StartTime.ToString("O"));
                command.Parameters.AddWithValue("$end", session.EndTime!.Value.ToString("O"));
                command.Parameters.AddWithValue("$startTicks", session.StartTime.UtcTicks);
                // 调时后按“开始时刻 + 单调时长”估计日期分配，原始 EndTime 仍原样保留。
                command.Parameters.AddWithValue("$effectiveEnd", (session.StartTime + session.Duration!.Value).UtcTicks);
                command.Parameters.AddWithValue("$duration", session.Duration.Value.Ticks);
                command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    /// <summary>指定本地日期的完整总计及一页会话；跨午夜会话仅贡献与该日相交的部分。</summary>
    public DayHistory ReadDay(DateOnly date, TimeZoneInfo zone, int offset = 0, int pageSize = 100)
    {
        if (offset < 0 || pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(offset));
        var start = DayBoundary(date, zone);
        var end = DayBoundary(date.AddDays(1), zone);
        using var connection = Open(readOnly: true);
        // 同一读取事务提供一致快照，避免总计与历史刚好跨越后台的一次提交。
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$from", start);
        command.Parameters.AddWithValue("$to", end);
        const string filter = "StartUtcTicks < $to AND (EffectiveEndUtcTicks > $from OR (DurationTicks=0 AND StartUtcTicks >= $from))";
        command.CommandText = $"""
            SELECT ProcessName,FriendlyName,
                SUM(MAX(0,MIN(EffectiveEndUtcTicks,$to)-MAX(StartUtcTicks,$from))),COUNT(*)
            FROM AppSessions WHERE {filter} GROUP BY ProcessName COLLATE NOCASE ORDER BY 3 DESC, ProcessName;
            """;
        var totals = new List<DailyAppTotal>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) totals.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) / (double)TimeSpan.TicksPerSecond, reader.GetInt64(3)));
        command.CommandText = $"SELECT COUNT(*) FROM AppSessions WHERE {filter};";
        var count = (long)command.ExecuteScalar()!;
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        command.CommandText = $"""
            SELECT Id,ProcessName,FriendlyName,StartTime,EndTime,DurationTicks FROM AppSessions
            WHERE {filter} ORDER BY StartUtcTicks DESC, Id LIMIT $limit OFFSET $offset;
            """;
        var sessions = new List<AppSession>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) sessions.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), TimeSpan.FromTicks(reader.GetInt64(5))));
        transaction.Commit();
        return new(date, totals, sessions, count, offset, pageSize);
    }

    /// <summary>所选日期的事件历史；限定数量，避免把整个数据库装入内存。</summary>
    public IReadOnlyList<StoredMonitorEvent> ReadEvents(DateOnly date, TimeZoneInfo zone, int limit = 1000)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,EventType,Timestamp,ProcessName,MetadataJson FROM Events WHERE UtcTicks >= $from AND UtcTicks < $to ORDER BY UtcTicks DESC,Id LIMIT $limit;";
        command.Parameters.AddWithValue("$from", DayBoundary(date, zone));
        command.Parameters.AddWithValue("$to", DayBoundary(date.AddDays(1), zone));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<StoredMonitorEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        return result;
    }

    /// <summary>拒绝未结束会话；只有完成聚合的数据才可参与持久化统计。</summary>
    public static void ValidateSession(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.ProcessName);
        if (session.Id == Guid.Empty || session.EndTime is null || session.Duration is null || session.Duration < TimeSpan.Zero)
            throw new ArgumentException("Only completed, nonnegative sessions with an ID can be stored.", nameof(session));
        _ = session.StartTime + session.Duration.Value;
    }

    /// <summary>按发现日期查询已保存候选；限制返回量，供后续评分与历史读取使用。</summary>
    public IReadOnlyList<DetectedPattern> ReadPatterns(DateOnly date, TimeZoneInfo zone, int limit = 200)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,PatternType,StartTime,EndTime,ProcessName,Summary,MetadataJson FROM DetectedPatterns
            WHERE DetectedUtcTicks >= $from AND DetectedUtcTicks < $to ORDER BY DetectedUtcTicks DESC,Id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", DayBoundary(date, zone));
        command.Parameters.AddWithValue("$to", DayBoundary(date.AddDays(1), zone));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<DetectedPattern>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Enum.Parse<PatternType>(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5), reader.GetString(6)));
        return result;
    }

    /// <summary>只接受完整候选；开始/结束日期允许因系统调时逆序，不以日期相减验证时长。</summary>
    public static void ValidatePattern(DetectedPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Id == Guid.Empty || !Enum.IsDefined(pattern.Type)) throw new ArgumentException("Invalid pattern identity.", nameof(pattern));
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern.Summary);
        using var document = System.Text.Json.JsonDocument.Parse(pattern.EvidenceJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new ArgumentException("Pattern evidence must be an object.", nameof(pattern));
    }

    /// <summary>按评估日期读取初筛快照，包含被过滤的候选；不将旧版未评分候选伪装为零分。</summary>
    public IReadOnlyList<PatternEvaluation> ReadEvaluations(DateOnly date, TimeZoneInfo zone, int limit = 200)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EvaluationJson FROM PatternEvaluations WHERE EvaluatedUtcTicks >= $from AND EvaluatedUtcTicks < $to
            ORDER BY EvaluatedUtcTicks DESC,PatternId LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", DayBoundary(date, zone));
        command.Parameters.AddWithValue("$to", DayBoundary(date.AddDays(1), zone));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<PatternEvaluation>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<PatternEvaluation>(reader.GetString(0))
            ?? throw new InvalidDataException("Missing evaluation data."));
        return result;
    }

    /// <summary>评分与候选 ID 一致，数字有限且在约定范围内；错误输入在排队前拒绝。</summary>
    public static void ValidateEvaluation(DetectedPattern pattern, PatternEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (evaluation.PatternId != pattern.Id || evaluation.PatternId == Guid.Empty) throw new ArgumentException("Mismatched evaluation identity.");
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluation.PolicyVersion);
        static bool Range(double value, double max) => double.IsFinite(value) && value >= 0 && value <= max;
        var b = evaluation.Breakdown;
        if (b is null || !Range(evaluation.Score, 100) || !Range(evaluation.Threshold, 100) ||
            !Range(b.Abnormality, 1) || !Range(b.PersonalRelevance, 1) || !Range(b.Novelty, 1) ||
            !Range(b.ContextSuitability, 1) || !Range(b.HumorPotential, 1) || !Range(b.RecencyPenalty, 100) || !Range(b.RepetitionPenalty, 100) ||
            !Range(evaluation.GlobalCooldownRemainingSeconds, TimeSpan.MaxValue.TotalSeconds) || !Range(evaluation.TopicCooldownRemainingSeconds, TimeSpan.MaxValue.TotalSeconds))
            throw new ArgumentException("Invalid scoring values.");
        if (evaluation.Reasons is null || evaluation.Reasons.Count == 0 || evaluation.Reasons.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Evaluation must explain the decision.");
        if (evaluation.IsSelected && (evaluation.Score < evaluation.Threshold || evaluation.GlobalCooldownRemainingSeconds > 0 || evaluation.TopicCooldownRemainingSeconds > 0))
            throw new ArgumentException("Selected evaluation contradicts its score or interval.");
        using var json = JsonDocument.Parse(evaluation.ContextJson);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Context must be an object.");
    }

    private SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 1, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>按查询时区计算午夜，不能假设每天恒为 24 小时；无效午夜前进至首个有效分钟。</summary>
    private static long DayBoundary(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).UtcTicks;
    }
}
