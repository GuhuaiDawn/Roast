# Desktop Narrator

Desktop Narrator is an original Windows background prototype for a low-noise AI system narrator. The current implementation is deliberately limited to Milestones 1–4:

- .NET 8 / WPF project structure.
- A system tray icon with Developer Log and clean exit.
- A WPF-independent foreground monitor using `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`.
- Process-level foreground transition events with duplicate same-process transitions removed.
- A WPF-independent idle monitor using `GetLastInputInfo`, emitting `UserIdle` and `UserReturned` with idle duration.
- A pure `AppSessionBuilder` that turns foreground events into continuous application sessions, with a current-session summary and completed-session logs.

## Privacy boundary

This prototype does not capture screenshots, OCR, browser or chat contents, files, clipboard data, passwords, or keyboard text. The monitors only read the current foreground window's process ID, process name, an optional executable file description, and the last-input timestamp for the current Windows session. Idle monitoring does not install keyboard or mouse hooks or read input contents. No raw event log is uploaded anywhere, and no LLM or database is connected yet.

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
- Sessions and logs are currently in memory only; exiting loses history. A forced process kill or crash cannot guarantee finalization. Persistence belongs to Milestone 5.

### Quick session acceptance

1. Stop the old instance, rebuild, and run `DesktopNarrator.App`. Open Developer Log; its heading should show **Milestone 1–4**, with a current application and start time below it.
2. Focus an application such as Visual Studio for about 10 seconds, then switch to another application. Expect `SessionEnded` for the previous app (start/end dates and duration) and `SessionStarted` for the new one. Opening the log itself is also a foreground switch and is intentionally recorded.
3. Close only the log window, switch applications, and reopen it. The current-session summary should reflect the latest foreground event; closing this window does not end monitoring.
4. Exit through the tray. Under VS debugging, look in Output for the final `SessionEnded` with `监测结束`. Closing the log window, stopping the debugger, and tray exit are different operations.

Automated session checks cover an 8-minute Code → 40-minute Chrome timeline, duplicate process names, same-time transitions, subsecond precision, multi-day sessions, clock changes, invalid input, idempotent finalization, UI queue delay, closed-window updates and a native foreground-monitor pipeline. They do not replace manually verifying the tray exit interaction.

## Developer Log stability

Log entries are queued onto the store's owning UI dispatcher, including calls made from the UI thread. The public collection is read-only; producers use `Append`. This prevents background collection mutations and nested writes during collection notifications. Entries become visible asynchronously.

Auto-scroll runs once per burst at `ContextIdle` priority, after collection notifications, data binding and layout. It uses the ListBox's current view, not a potentially ahead-of-view source index. Closing/unloading a log window detaches its collection handler and cancels any pending scroll. Unexpected unhandled UI errors go to diagnostic output rather than being written back into the potentially failing log view, and are not globally swallowed.

The WPF regression tests create invisible, non-activating windows and exercise burst appends, scrolling, repeated close/reopen, closing with a pending scroll, background producers, and nested diagnostics. Run only these with:

```powershell
dotnet run --project .\tests\DesktopNarrator.Tests\DesktopNarrator.Tests.csproj -- --log-ui-only
```

The burst/reopen test reproduced the original “ItemsControl is inconsistent with its items source” exception before the fix. See also Microsoft's [WPF threading model](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model).

## Architecture

- `src/DesktopNarrator.Events`: event/session models and pure application-session aggregation; no WPF or Win32 dependency.
- `src/DesktopNarrator.Monitor`: Win32 monitoring and transition state logic; no WPF dependency.
- `src/DesktopNarrator.App`: WPF lifecycle, tray UI, Developer Log, and the serialized session-to-log adapter.
- `tests/DesktopNarrator.Tests`: dependency-free executable tests for transition logic.

The next appropriate milestone is Milestone 5: SQLite persistence so sessions and per-app totals remain queryable after restart. Pattern detection, LLM calls and overlays are still out of scope.
