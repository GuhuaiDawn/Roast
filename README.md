# Desktop Narrator

Desktop Narrator is an original Windows background prototype for a low-noise AI system narrator. The current implementation is deliberately limited to Milestones 1–7:

- .NET 8 / WPF project structure.
- A system tray icon with Developer Log and clean exit.
- A WPF-independent foreground monitor using `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`.
- Process-level foreground transition events with duplicate same-process transitions removed.
- A WPF-independent idle monitor using `GetLastInputInfo`, emitting `UserIdle` and `UserReturned` with idle duration.
- A pure `AppSessionBuilder` that turns foreground events into continuous application sessions, with a current-session summary and completed-session logs.
- Local SQLite storage for completed sessions and transition events, with date-based application totals and paginated history that remain available after restart.
- Low-frequency process-group monitoring and five deterministic pattern rules, with a candidate/evidence view and local persistence. Candidates do not yet trigger narration.
- Explainable candidate scoring and filtering, including helper-process noise, foreground association, idle/busy context, repetition and candidate-selection intervals. No model or notification is invoked.

## Privacy boundary

This prototype does not capture screenshots, OCR, browser or chat contents, files, clipboard data, passwords, or keyboard text. Foreground monitoring reads process identity and an optional executable file description; idle monitoring reads the last-input timestamp without keyboard/mouse hooks. Process monitoring enumerates process names for the current Windows session, not command lines, paths, window contents or process memory. Completed sessions, transition events and minimal pattern evidence are stored in local SQLite; nothing is uploaded and no LLM is connected yet. The database is not encrypted by this prototype; it uses the current user's local application-data folder and its Windows permissions.

The future Settings screen must make any new sensitive data source explicitly opt-in. Until then, the boundary remains process-level behavior and input timing only.

## Run

Install the .NET 8 SDK and build:

```powershell
dotnet build .\DesktopNarrator.sln
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj
dotnet run --project .\src\DesktopNarrator.App\DesktopNarrator.App.csproj
```

In Visual Studio 2022 17.14 or later, prefer opening `DesktopNarrator.slnx`. The older `DesktopNarrator.sln` remains available for tooling that does not yet support the XML solution format.

The app starts minimized to the system tray. Right-click the tray icon to open Developer Log or exit. Double-clicking the icon also opens the log.

## Milestone 3: idle detection

- Default: 300 seconds without input, sampled once per second on a background thread. Only transitions are logged, not each sample.
- `UserIdle.IdleStartedAt` estimates the last input time, not the time the threshold was crossed. `DetectedAt` records when the monitor noticed the transition.
- `UserReturned.IdleDuration` includes the entire no-input period (including the threshold), excluding the sampled delay after the latest input. `ReturnedAt` estimates that input time; `DetectedAt` is the observation time.
- Durations use 64-bit uptime, independently of wall-clock adjustments. The 32-bit Windows input tick is unwrapped and retained while unchanged. At first startup, idle history beyond one full 32-bit tick cycle (about 49.7 days) is inherently ambiguous.
- Changed, non-incremental input timestamps are treated as activity, with anomalous times conservatively assigned to the observation time. Input counts and contents are never recorded.
- API read failures retain the last known state and retry next second. Repeated failures are logged once per failure streak. A failing subscriber does not stop other subscribers or the monitor.
- Stop/dispose wakes the sampling thread immediately and joins it; restart creates a fresh tracker and makes no claims about time while monitoring was stopped.
- Idle means **no input**, not proof that the person left: watching a video can count as idle. Only the calling Windows session is observed. Lock/sleep are not separate event types; after a sampling gap, only the latest input timestamp is available. Multiple activities within that gap cannot be reconstructed, so return time may be later than the first actual input.

API reference: [Microsoft GetLastInputInfo documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo).

### Quick manual acceptance

Exit any existing Desktop Narrator instance, then launch with a temporary 10-second threshold:

```powershell
dotnet run --project .\src\DesktopNarrator.App\DesktopNarrator.App.csproj -- --idle-seconds 10
```

1. Open Developer Log from the tray and leave the keyboard/mouse untouched for at least 11 seconds. Expect one `UserIdle` entry.
2. Wait longer: no duplicate `UserIdle` entries should appear.
3. Move the mouse or press a key: expect one `UserReturned` entry with `IdleDuration` within about one sampling interval of the no-input period.
4. Repeat; foreground switching should still work. Exit via the tray and check that the process exits.

`--idle-seconds` accepts integers from 1 through 86400 for this launch only; invalid values log a diagnostic and fall back to 300 seconds. No persistent settings UI is implemented yet. The existing narration toggle does not pause monitoring (the narration pipeline is not implemented).

### Automated verification

The console test runner includes deterministic fake-input tests for threshold boundaries, duplicate suppression, repeated idle/return cycles, clock changes, tick rollover, polling gaps, transient failures, subscriber isolation, and lifecycle. Native smoke tests read the current foreground process and last-input time and require an interactive Windows session; a restricted sandbox may prevent these reads. No automated test generates keyboard or mouse input. The UI acceptance steps above remain a separate manual check.

## Milestone 4: foreground sessions

`DesktopNarrator.Events` contains `AppSession` and `AppSessionBuilder`. No new solution project is required. `DesktopNarrator.App/SessionLogRecorder` connects foreground events to the builder and the existing Developer Log.

- The first observed application starts a session; switching to another process name completes it and starts the next. No history is inferred from `PreviousApplication` before monitoring began.
- Process names are compared case-insensitively. Different PIDs belonging to the same application do not reset the session start. Returning to an earlier app starts a new session with a new ID.
- Each session contains `Id`, `ProcessName`, `FriendlyName`, `StartTime`, `EndTime`, `Duration` and derived `DurationSeconds`. Active sessions have no end/final duration yet.
- Start/end dates come from event observation times. Duration uses independent monotonic elapsed time supplied to the pure builder; changing the system clock does not change elapsed duration. After a clock adjustment, the displayed end date can be earlier than the start date: use `Duration`, not date subtraction, for statistics.
- The builder rejects negative or decreasing elapsed-time observations without changing the active session. Equal elapsed times are allowed (a zero-duration rapid switch). Replay callers must use one consistent elapsed-time origin.
- Session duration means **observed foreground residence**, including no-input periods. Idle/return events remain independent and do not split sessions or subtract time. This is not active work time; unavailable foreground observations and sleep/lock gaps are not reconstructed.
- The adapter serializes consumption and finalization, and aggregates on the monitor callback rather than waiting for the UI queue. It rejects callbacks after finalization. Normal application exit completes the final session once. Since the log dispatcher may already be shutting down, its final summary is also sent to diagnostic output (visible in VS Output while debugging).
- Completed sessions now persist through Milestone 5. The live Developer Log and current unfinished session remain in memory. A forced process kill or crash cannot guarantee finalization or save work still waiting in the write queue.

### Quick session acceptance

1. Stop the old instance, rebuild, and run `DesktopNarrator.App`. Open Developer Log; its heading should show **Milestone 1–7**, with a current application and start time below it.
2. Focus an application such as Visual Studio for about 10 seconds, then switch to another application. Expect `SessionEnded` for the previous app (start/end dates and duration) and `SessionStarted` for the new one. Opening the log itself is also a foreground switch and is intentionally recorded.
3. Close only the log window, switch applications, and reopen it. The current-session summary should reflect the latest foreground event; closing this window does not end monitoring.
4. Exit through the tray. Under VS debugging, look in Output for the final `SessionEnded` with `监测结束`. Closing the log window, stopping the debugger, and tray exit are different operations.

Automated session checks cover an 8-minute Code → 40-minute Chrome timeline, duplicate process names, same-time transitions, subsecond precision, multi-day sessions, clock changes, invalid input, idempotent finalization, UI queue delay, closed-window updates and a native foreground-monitor pipeline. They do not replace manually verifying the tray exit interaction.

## Milestone 5: SQLite and saved history

`DesktopNarrator.Memory` uses `Microsoft.Data.Sqlite` 8.0.33. With Milestone 6's Analysis project, both solution formats now contain **six projects**. Reload the solution in Visual Studio if a project is missing, restore NuGet packages, and keep `DesktopNarrator.App` as the startup project.

### 保存位置与使用方式

- 数据库位于 `%LOCALAPPDATA%\DesktopNarrator\narrator.db`，不在源码或 `bin` 目录中。当前实际路径也显示在日志和历史页。
- 托盘 → **打开 Developer Log** → **已保存历史**：选择日期，查看该日各程序的前台总时长和会话明细；每页 100 段，会话分页不会截断上方的全天总计。新记录写入后点击“刷新”查看。
- 总时长仅来自**已结束、已提交**的会话，包含无输入时段，不代表有效工作时间。当前会话在切换到其他程序或通过托盘正常退出时结束；打开日志本身也会造成前台切换。
- `Events` 保存 `AppFocused`、`UserIdle`、`UserReturned`，以及 Milestone 6 新增的 `AppStarted`、`AppClosed`、`ProcessObservationReset`；`AppSessions` 保存完整会话。没有保存每秒采样、完整进程列表、窗口标题、用户输入或整段调试日志。
- 日统计从会话按需计算，暂不维护重复的 `DailyStats` 缓存表。Milestone 6 新增 `DetectedPatterns`，Milestone 7 新增 `PatternEvaluations` 初筛快照；旁白和叙事记忆尚未实现。

### 时间与可靠性边界

- 会话保留原始起止时间及 UTC 偏移；时长以整数 tick 保存，避免丢失亚秒精度。跨午夜会话只将与所选日期相交的部分计入当天，明细则仍显示**完整会话**的起止和时长；同一跨日会话可以出现在两天中。
- 日期边界采用查询时的本机时区，考虑夏令时，不能假定每天都是 24 小时。系统时钟调整后，按“开始时间 + 单调时长”估计跨日分配，不宣称能还原精确的真实日期归属；更改时区也会改变日统计分组。
- 数据库初始化和写入由专用后台线程负责；每次操作独占连接，使用 WAL、参数化 SQL 和事务。事件及对应结束会话同批提交，稳定 ID 保证重试不会重复累计。未知的新版本或损坏数据库会报错保留，不自动删库重建。
- 待处理队列最多 4096 项，另有最多 64 项在当前批次中；提交成功只表示**已入队**。锁冲突等待约 1 秒后报告故障，并保留批次、间隔约 1 秒重试。队列已满/已停止时明确拒绝新项，状态中保留累计拒绝数，不承诺无限缓冲。
- 历史查询先等待此前已接受的写入，最多 2 秒；未跟上时仍显示已提交历史并标明。数据库不可读会显示“历史暂不可用”，不会伪装成没有记录；切换日期或关窗使旧查询结果作废。
- 正常退出先停止监测、结束最后会话，再等待写入队列最多 5 秒。失败或超时会输出未确认数量；**停止调试、任务管理器结束进程、崩溃或断电，可能丢失当前会话和尚未提交的数据**。本阶段没有未完成会话的崩溃恢复。
- 请只运行一个实例：本阶段尚未加入单实例限制，多开会重复观察并影响总计。数据库没有自动保留期限或清理策略；运行期间可能同时存在 `narrator.db-wal` 和 `narrator.db-shm`，不要单独删除这些文件或仅复制主库作为运行中备份。

### 重启验收

1. 停止旧程序，重新生成并运行；打开日志，确认显示 **Milestone 1–7** 和“数据库可用”。
2. 在 Visual Studio、浏览器等程序之间切换，每个停留几秒；进入“已保存历史”，选择今天并刷新，确认应用总计和会话明细。
3. 从**托盘菜单退出**，再重新启动；今天的历史应仍在，实时日志则重新开始。关闭日志窗口不等于退出程序。
4. 选择没有记录的日期，应显示 0 个应用、0 段会话；有超过 100 段时可翻页，总计不随页码改变。

自动化测试覆盖重开数据库、会话管线正常收尾、事务回滚、幂等重试、跨午夜、夏令时、调时、分页、锁与初始化故障恢复、有界队列/退出超时、隐私字段白名单，以及真实 WPF 历史页的日期切换和分页。测试均使用独立临时数据库，不改动用户历史。原生监测和 WPF 测试需要交互式 Windows 环境；托盘菜单的完整手工重启验收仍需按上面步骤执行。

```powershell
# 仅运行 SQLite 存储与队列测试
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --memory-only
```

实现依据：[Microsoft SQLite 异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)、[数据库错误与超时](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)、[事务](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)。

## Milestone 6: Pattern Detector

新增 `DesktopNarrator.Analysis`，只依赖事件模型，不依赖 WPF、Windows API、SQLite 或真实时钟。规则消费有序事件和同一基点的单调计时，便于立即重放长时间线。`PatternLogRecorder` 将多来源回调串行化，每 5 秒检查长会话；没有候选时不写心跳日志。

### 默认规则

| 类型 | 触发条件 | 去重与事实范围 |
| --- | --- | --- |
| `LongSession` | 同一程序连续前台驻留 ≥90 分钟 | 每段会话一次，包含无输入时段；不等于高强度工作。切换前和低频心跳均可检测。 |
| `RapidSwitching` | 最近 5 分钟内至少 25 次应用切换 | 首次快照、同名进程不算切换；窗口为 `(当前时刻−5分钟, 当前时刻]`。仅在从低于阈值变为达到阈值时产生候选，跌回阈值下后重新准备检测。 |
| `ClosedThenReopened` | 观察到某同名进程组全部消失后，≤10 分钟再次出现 | 不是切走再切回，也不是关闭单个窗口；间隔为快照发现时刻之间的估计。 |
| `LongIdleReturn` | 完整无输入时长 ≥30 分钟后返回 | 不把无输入当作确实离开；同一个返回事件不重复产生候选。 |
| `RepeatedOpen` | 本次连续监测在同一当地日期观察到某程序启动 6 次 | 每程序达到阈值时一次；同名子进程不单独计数。程序重启、进程观察缺口会清零，不宣称是跨重启的全天总次数。 |

`PatternOptions` 集中管理阈值，目前没有设置 UI。`--idle-seconds` 只改变空闲监测器的阈值，不改变这里的 30 分钟候选规则。快速切换只保留阈值所需的最近样本，证据明确使用“至少 N 次”和“样本涉及的应用数”，不是完整窗口内的精确全量统计。

### 进程事件的来源与限制

- `ProcessMonitor` 默认每 5 秒通过 `Process.GetProcesses` 获取当前 Windows 会话的名称快照。同名进程大小写不敏感，只有进程组 `0 → 存在` / `存在 → 0` 才产生启动/关闭事件；不同路径的同名程序也会被合并。
- 第一次成功采样只建立基线，已有程序不会被误报为刚启动。读取失败时不使用部分列表；重新成功时重建基线。采样间隔超过允许缺口时也重新基线，默认缺口为 15 秒。
- `ProcessObservationReset` 会清除关闭/重开及当日反复启动规则的旧证据；不会清除数据库历史，也不会重置独立的前台会话监测。
- 时间戳表示**发现时间**。两个快照之间完整启动又退出的短命进程、同名组在间隙内全部替换、始终驻留后台的浏览器重新开窗，可能没有相应生命周期事件。只观察当前 Windows 会话，不覆盖其他登录会话，也不能证明某次启动是用户主动操作。
- 不通过前台事件推断程序启动，不读取 PID 之外的进程内存、命令行或窗口内容。前台规则沿用既有观察边界：睡眠、锁屏和遗漏的前台变化不被还原，长驻留不能解释为持续有效操作。

API 依据：[Microsoft Process.GetProcesses](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.getprocesses)。

### 候选界面与持久化

进入 **Developer Log → 模式候选（本次运行）**，查看最近 200 个候选，点击一行查看 JSON 证据；实时日志也会出现 `[Pattern]`。关闭日志再打开不会清空候选，重启程序后此页重新开始。正常使用时可以长时间为空：没有达到阈值就保持沉默。

每个候选保存类型、起止时间、可选进程名、事实摘要与最小证据。Milestone 6 的 v2 模式新增了 `DetectedPatterns`，当前 Milestone 7 已增量升级为 **v3**（见下节），不重算或删除已有事件/会话。候选共用有界写入队列和稳定 ID 去重；界面显示候选不等于已经提交，故障/拒绝数量请看数据库状态。候选与来源事件分别入队，不承诺二者跨工作项的原子提交。

`SqliteActivityStore.ReadPatterns(date, zone, limit)` 可按发现日期查询已保存候选，默认最多 200 条；当前未提供跨运行候选历史 UI，也没有从旧候选恢复检测器计数。旧版程序不支持高于自身版本的数据库，请不要用旧生成文件回退运行。

Milestone 6 负责事实，Milestone 7 在下游做初筛；当前仍**没有反馈调度器、LLM、旁白通知或叙事记忆**。候选不包含“摸鱼”等意图判断，通过初筛也不是最终打扰决定。

### 验证方式

1. 停止旧实例，在 VS 重新加载解决方案（现在 6 个项目），重新生成并启动 `DesktopNarrator.App`。
2. 确认标题为 **Milestone 1–7**，日志包含规则阈值与 `ProcessObservationReset` 基线标记。
3. 在 5 分钟内切换不同程序至少 25 次，可看到 `RapidSwitching`；同一程序多个窗口不累计为应用切换。
4. 对可真正退出的普通应用，在其同名进程全部退出后等待至少两个采样周期（约 10 秒），10 分钟内重开，检查 `AppClosed` → `AppStarted` 和 `ClosedThenReopened`。不要为测试强制结束系统或其他人的进程。
5. 点击候选查看证据，关闭日志后重开检查列表。90 分钟长会话、30 分钟空闲及其边界可直接用下列确定性测试验证，无需真实等待。

```powershell
# 纯规则边界及快速重放，不访问系统监测或真实数据库
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --analysis-only
# 进程组状态、故障恢复和真实隐藏子进程生命周期
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --process-only
```

截至 Milestone 7，自动化回归共 77 项，涵盖原有功能、五类规则、评分/过滤重放、旧库升级、候选与评分事务/去重、真实进程监测及不可见 WPF 候选页。测试创建的隐藏子进程及临时库会清理，不操作用户应用或历史。完整托盘交互和长时间后台运行仍需手工验收；上述重放是测试接口，不是已实现的 JSON 文件导入 UI。

## Milestone 7: Interestingness Engine

在现有 `DesktopNarrator.Analysis` 中新增 `InterestingnessEngine` 和 `InterestingnessOptions`，项目总数仍为 **6**。引擎不做系统采样、磁盘 I/O 或模型请求；只消费本次运行的事件、结构化事实和单调计时。被过滤的候选仍保留，以便判断是不是过滤过度。

### 评分含义与默认策略

规则版本 `rules-v1`，分数范围 0–100，默认通过阈值 **75**。分数是可调整的启发值，**不是概率、真实心理判断或统计显著性**。

`分数 = clamp(100 × (0.30A + 0.25P + 0.20N + 0.15C + 0.10H) − 近期扣分 − 重复扣分, 0, 100)`

| 分量 | 当前实现 | 未声称实现的部分 |
| --- | --- | --- |
| A · 规则强度 | 时长/次数相对于固定参考值；较短重开间隔的强度较高 | 没有个人分布、历史百分位或异常概率 |
| P · 前台关联 | 长会话直接来自前台观察；生命周期候选检查近期同名程序是否出现过前台 | 没有学习用户偏好，不能证明启动是主动行为 |
| N · 新颖性 | 之前 3 小时没有同主题候选取 1，否则取 0.35 | 比较的是候选，不是“之前说过什么” |
| C · 当前状态 | 无输入或近期快速切换时为 0，其余为 1 | 未检查全屏游戏、视频或通知适宜性 |
| H · 表达潜力 | 按模式类型使用固定启发系数 | 不是模型判断，也不保证幽默质量 |

具体强度：长会话 `min(1, 时长秒/5400×0.65)`，长空闲 `min(1, 时长秒/1800×0.65)`，重复启动 `min(1, 次数/6×0.65)`，快速切换 `min(1, 次数/25×0.65)`，重开间隔 0–600 秒映射为 `1−间隔秒/600×0.6`。`PatternOptions` 是检测阈值，评分参考值与它分开；缩短检测阈值用于测试不会自动获得高分。

- 默认精确过滤辅助进程名：`git`、`git-remote-http`、`git-remote-https`、`git-lfs`、`conhost`、`msbuild`、`vctip`，以及本程序自身。比较忽略大小写和末尾 `.exe`，不使用前缀匹配；例如 `GitKraken` 不会因名称包含 git 被过滤。名单是可改的策略，不代表确认这些进程是恶意软件。
- `ClosedThenReopened` 和 `RepeatedOpen` 还要求本次运行最近 24 小时有同名程序的前台观察；没有关联时保守过滤，而不是把后台任务启动当成用户行为。
- 进入空闲后不通过候选；返回事件先恢复状态，再评估新候选。观察到 5 分钟内 25 次切换后，最近一次确认快速切换的 30 秒内直接过滤，**没有延后重试或待发队列**。因此快速切换事实可被成功检测，但同时因当前繁忙而被过滤。
- 两次“候选通过初筛”至少相隔 **20 分钟**；同类型、同进程至少相隔 **3 小时**。都是本次运行的单调时间，不受墙上时间回拨影响。被过滤的候选不延长通过间隔，进程监测重建基线也不取消这些间隔。
- 近期扣分最多 20 分，按全局通过间隔的剩余比例计算；重复扣分按前序批次中最近 3 小时的同主题候选每次 5 分、最多 20 分。同批候选先按基础分排名，再依次应用通过间隔，避免单纯依赖事件列表顺序抢占名额。
- 分数达到阈值仍可能因硬性条件而被过滤。例如 `git` 可以有很强的重开事实，但不会因此越过辅助进程策略。每一条阻止原因都显示出来。
- 上下文有界：最多保留 256 个近期前台名称、1024 个近期主题、每主题最多 16 个出现时刻和最近 4096 个已评估 ID。大量噪声不会挤掉仍在冷却中的已通过主题。相同候选 ID 在去重窗口内不会重复评估或重复消费间隔。

### 界面与保存

**Developer Log → 模式候选（本次运行）** 新增：

- “分数 / 阈值”“初筛结果”“判定原因”列，以及本次运行累计通过/过滤计数。
- 下方“结构化事实”和“评分与过滤原因（评估时快照）”两页，分别展示原始证据、分量、策略版本、原因与剩余间隔。
- 状态明确使用 **通过初筛（未发送）** / **已过滤**。剩余间隔是评估当时的快照，不是实时倒计时；即使分数高也不能理解成即将弹窗。
- 未到模式阈值时显示空列表提示。视图最多 200 行，累计计数不受这项视图上限影响；计数也不是数据库提交确认。

SQLite 模式升级为 **v3**，从 v1/v2 增量增加缺失的表和索引，保留历史。新增 `PatternEvaluations`，存储分数、是否通过、分量、原因、上下文、评估时间和策略版本。一个候选及其评分作为**同一工作项、同一事务**保存，重复 ID 不覆盖原决策；通过和过滤结果都保存。旧候选没有评分就保持未评分，不追溯伪造决定。

`SqliteActivityStore.ReadEvaluations(date, zone, limit)` 按**评估日期**读取持久化快照。候选/评分历史暂时只有查询 API，界面仍是本次运行列表；重启不恢复新颖性、前台关联或通过间隔。数据库写入失败的风险与 Milestone 5 相同，不能把“显示了通过”当成“已经提交”。

**边界：**本阶段没有个人历史基线、旁白历史、全屏检测或 FeedbackScheduler。即使通过初筛，未来反馈层仍需依据真正的已发送记录和实时场景决定是否发送。当前没有模型请求，也没有通知。托盘里的旁白开关仍不停止监测和候选初筛。

### 验收与测试

1. 正常退出旧实例，重新生成后启动。确认标题为 **Milestone 1–7**，解决方案仍是 6 个项目。
2. 新产生的 `git` 重开候选应保留事实，但显示“已过滤”和“后台辅助进程”原因；不要为测试启动不可信程序。
3. 5 分钟内频繁切换应用，`RapidSwitching` 应被检测并可因当前繁忙而过滤，不是检测失效。
4. 点击候选，核对下方事实和评分快照；关闭/重开日志后，本次运行的结果应保留。新启动不会自动给上次截图中的旧候选补评分。
5. 用确定性测试验收 90 分钟长会话、20 分钟通过间隔、3 小时同主题窗口和调时，不必真实等待。

```powershell
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --scoring-only
```

专项测试覆盖辅助进程精确匹配、前台关联过期、空闲/快速切换上下文、阈值边界、乱序时间、去重、同批排名、噪声压力、旧库升级、事务回滚和 WPF 评分显示。混合重放样例中 100 个候选仅 1 个通过，**只证明该样例的过滤行为，不承诺实际用户固定通过率**。仍需真实使用中调阈值，不为满足次数强行通过候选。

## Developer Log stability

Log entries are queued onto the store's owning UI dispatcher, including calls made from the UI thread. The public collection is read-only; producers use `Append`. This prevents background collection mutations and nested writes during collection notifications. Entries become visible asynchronously.

Auto-scroll runs once per burst at `ContextIdle` priority, after collection notifications, data binding and layout. It uses the ListBox's current view, not a potentially ahead-of-view source index. Closing/unloading a log window detaches its collection handler and cancels any pending scroll. Unexpected unhandled UI errors go to diagnostic output rather than being written back into the potentially failing log view, and are not globally swallowed.

The WPF regression tests create invisible, non-activating windows and exercise burst appends, scrolling, repeated close/reopen, closing with a pending scroll, background producers, and nested diagnostics. Run only these with:

```powershell
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --log-ui-only
```

The burst/reopen test reproduced the original “ItemsControl is inconsistent with its items source” exception before the fix. See also Microsoft's [WPF threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).

## Architecture

- `src/DesktopNarrator.Events`: event/session/pattern/evaluation models and pure application-session aggregation; no WPF or Win32 dependency.
- `src/DesktopNarrator.Monitor`: foreground, idle and process-group monitoring and transition logic; no WPF dependency.
- `src/DesktopNarrator.Analysis`: deterministic pattern rules, explainable initial scoring and filtering; no WPF, Win32 or database dependency.
- `src/DesktopNarrator.Memory`: SQLite schema, transactional repository, bounded background writer and asynchronous history-query adapter; no WPF or Win32 dependency.
- `src/DesktopNarrator.App`: WPF lifecycle, tray UI, Developer Log, history/candidate views, and serialized session/pattern adapters.
- `tests/DesktopNarrator.Tests`: executable test runner for transitions, sessions, pattern replay, SQLite persistence and WPF regressions; no external test framework.

The next appropriate milestone is Milestone 8: the LLM interface and prompt pipeline, with explicit configuration and minimal factual context. Feedback scheduling and real-message safeguards must be connected before any automatic notifications; the overlay remains a later milestone.
