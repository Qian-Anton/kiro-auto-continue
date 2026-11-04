# How it works (notes for contributors)

Measured against Kiro IDE 1.4.62 to 1.4.86 on Windows 11, November 2026. Kiro documents none of this, so expect it to move.

The program is `KiroAutoContinue.exe`: C# on .NET Framework 4.8, which Windows 10 and 11 come with, with a WPF window and a WinForms tray icon. Its sources are in `app/`. Up to v0.2 it was a PowerShell script; that version is kept in the `v0.2` tag.

## What it does with each turn

It reads each session's record (`messages.jsonl`, see "Kiro's session records" below) and decides from its last turn. It never switches tabs just to look.

| The turn ended with | What it does |
|---|---|
| `error` (cut off; the footer shows no credits) | Sends the continue message, which quotes your last task: "the last turn was interrupted; if you remember what you were doing, pick up from there and do not start over; otherwise here is my message". Waits 1, 5, then 15 minutes before each try. After 3 cut-offs in a row it leaves the session and logs `NEEDS YOU`. A normal finish resets the count. |
| `error` because the credits ran out (`UsageLimitReachedError`, `OverageLimitReachedError`) | Logs `NEEDS YOU - out of credits` and does not retry. It continues once another session finishes a turn normally, or once Kiro has restarted. |
| Cut short by a Kiro restart | Once Kiro has been up for a minute, sends the continue message, which also says terminals and background processes may be gone. See "What a Kiro restart leaves in the records" below. |
| `cancelled` (you pressed stop) | Nothing. Also ends until-done for that session. |
| `end_turn`, until-done off | Nothing, unless a message from your phone waits for this session. |
| `end_turn`, until-done on, no mark | Pushes it on. The first push restates the rules: decide for yourself and record each decision in the final report, do not ask whether to do the next step, stop only with `[DONE]` or with `[STUCK]` plus the reason. Every fifth push repeats the goal. |
| `end_turn`, `[DONE]` on the last lines | The first time, asks it to check the goal item by item. After that, stops and logs `DONE`. |
| `end_turn`, `[STUCK] reason` | Stops and logs `NEEDS YOU` with the reason. |

Until-done also stops after `untilDone.maxPushes` pushes, after 3 pushes in a row that did no work (no tool calls and a short reply), or when you send a new message without the mark.

## Typing into Kiro

Only when there is something to send, and only after `idleSeconds` without keyboard or mouse input (`remote.idleSeconds` for commands from your phone; a click on Continue or Stop in the window does not wait, since you are in the window and not typing into Kiro):

1. Find the session's tab by its title (from `session.json`). Two tabs with the same title are skipped.
2. Show that tab, and check that the panel shows the same last message as the record.
3. Put the text into the chat box through UI Automation and MSAA, without moving the mouse or pressing keys. A draft in the box is left alone, unless the draft is exactly this text (left there when a send was interrupted).
4. Press send, wait until the tab says Working, and show the tab you had open again.

Right before it types, presses send or presses stop, it checks once more that the session's tab is still the one shown. Kiro has a stop button only on the session it shows, so if you switched tabs in the meantime it does nothing and tries again later (11-04: a stop meant for a test session hit the session the user had just switched to). It switches back to your tab only if you did not switch yourself.

## Kiro's session records

`%USERPROFILE%\.kiro\sessions\<workspace hash>\sess_<uuid>\`

- `session.json`: `id`, `title` (what the chat tab shows), `workspacePaths`, `status` (`in_progress`, `idle`, `waiting_on_user`), `createdAt`, `lastModifiedAt`.
- `messages.jsonl`: one JSON object per line, appended while a turn runs. The kinds used here, under `payload.type`:
  - `user`: `content` is the message as sent. Line breaks arrive as Markdown hard breaks, two spaces and a newline.
  - `turn_start`, then for the agent `assistant` (the visible text has `operationType: "Say"`) and `tool_call` / `tool_result`.
  - `usage_summary`: `status` (`success`, `aborted`, `failed`), `elapsedTime` in ms, and credits in `promptTurnSummaries[].usage`.
  - `turn_end`: `stopReason` is `end_turn` (finished), `cancelled` (the user pressed stop) or `error` (cut off).
- In one workspace's history: 399 `end_turn`, 111 `cancelled`, 70 `error`. A turn stopped early by the user can have zero credits, so the chat footer looks just like a cut-off. The record still says `cancelled`.
- Lines can be megabytes (images, tool output). `app/Records.cs` pulls fields out with regexes instead of parsing whole lines, and reads only the tail of the file.
- Every `error` turn has a `session_metadata` line with `key: "displayError"` before its `turn_end`: the error the chat showed, as `value.errorType` and `value.message`. Across all workspaces (197 errors): `ConnectionResetError` 65, `ModelThrottleError` 51, `UsageLimitReachedError` 30, `ClientNetworkError` 12, `ClientThrottleError` 7, `OverageLimitReachedError` 4, and a long tail (bad images, content filter, proxy, sign-in). The two limit errors mean the account is out of credits; retrying on it cannot work.
- Kiro writes a few `user` lines itself, mid-turn, marked `_meta.kiro.syntheticUserMessageReason` (`max-tokens-recovery`, `fallback-continuation`; 19 of 8969). They are not the user's task. Whether the chat panel shows them has not been checked.

## What a Kiro restart leaves in the records

From two restarts after the credits ran out (11-01 07:28 UTC after `UsageLimitReachedError`, and 18:56 after `OverageLimitReachedError`) and the other restarts in the history:

- The turns that were waiting on the model when the credits ran out ended with `error` before the restart (four, from 18:54:33 to 18:55:00; Kiro was back at 18:56:11).
- A turn busy with a tool when Kiro went down gets nothing and stays open in the record. Kiro does not close it when it starts. It writes a `turn_end` with `"boundarySource": "recovered"` and `stopReason: "cancelled"` only when that session is used again: all seven such lines came 2 to 20 seconds before someone typed "continue" into that session, apparently when its tab was opened.
- `session.json` keeps `status: "in_progress"` for those sessions, so the status cannot tell them from running ones.
- While a sub-agent runs, the parent session's record can go 20 minutes or more without a new line. A quiet record does not mean a dead turn.

So the program counts a turn as cut short by a restart when it is still open and its last line is older than the start of Kiro's oldest `Kiro.exe`, when it ended with `error` at most `restartWindowMinutes` before that start (any time before, for a limit error), or when it ended with the recovered `turn_end`. It notices a restart when that start time changes, then reads every record written since the previous start. It ignores a `Working` state on those tabs and relies on the record and the idle check.

## Out of credits, and the morning status

- For the two limit errors, retrying cannot work until there are credits again. Such sessions wait; when another session finishes a turn normally, the credits are back, and the waiting ones are continued with the plain continue message. A Kiro restart continues them too. When the program starts, the sessions whose last turn hit a limit error in the last `resume.creditWaitHours` wait the same way.
- The morning status is the status list over `report.hours`, with counts taken from the program's own log lines (`; sent resume`, `; sent push` and the other until-done kinds, `; sent queued`, `NEEDS YOU - `). The log has no year: a date after now belongs to the year before. It goes out once a day between `report.time` and `report.windowHours` later; the day it went out is kept in `report-state.json`. When no session wrote its record in that time, it is not sent.

## The credits line

- Kiro asks its service for the account's usage now and then. On 11-04 it asked when a window started or reloaded, every few minutes while the plan's credits were being used up, and every one to two hours after that. It logs each answer in `%APPDATA%\Kiro\logs\<start time>\window<N>\exthost\kiro.kiroAgent\q-client.log`, as a line `yyyy-MM-dd HH:mm:ss.fff [info] {json}` (local time) with `"commandName":"GetUsageLimitsCommand"`. Kiro keeps the file open, so it is read with every kind of sharing allowed.
- From the answer's `output`: `subscriptionInfo.subscriptionTitle` (`KIRO FREE`, `KIRO PRO`, ...), and from the `CREDIT` entry of `usageBreakdownList`: `currentUsageWithPrecision` and `usageLimitWithPrecision` (this month's plan credits), `currentOveragesWithPrecision`, and `bonuses[]` (`displayName`, `status` such as `ACTIVE` or `EXHAUSTED`, `currentUsage`, `usageLimit`; a name or description with "trial" in it is shown as trial credits, an `EXPIRED` one not at all). A failed request (an `error`, no `output`) and an empty answer (`"output":{}`) are skipped, so the last good answer stays.
- The newest answer across all those logs is shown, with the time Kiro asked: the same numbers as Kiro's status bar. Kiro also keeps its latest answer in `%APPDATA%\Kiro\User\globalStorage\state.vscdb` (SQLite; key `kiro.kiroAgent`, entry `kiro.resourceNotifications.usageState`); the log is used instead because it is plain text and carries the time.
- There is no estimate on top of Kiro's numbers: they do not always agree with the credits the session records show.
- It never reads Kiro's login token and never asks the service itself. A log is read again only when its size or time changed.

## The chat panel through UI Automation

- The chat panel is a webview. Chromium exposes it to UI Automation, but `FindAll`/`FindFirst` search the control view by default, which hides plain divs. A pushed `CacheRequest` with `RawViewCondition` makes them search the raw view. All UI Automation runs on the watcher's own STA thread.
- Tabs are elements of class `tab-bar-item`. The state is the name of the `tab-bar-item-indicator` child (`Working`, `Updated`, `Needs your input`). `GetRuntimeId()` stays the same when a tab is renamed and changes when Kiro restarts.
- A session's messages form one flat list. In a long session the list is `session-view-content`. In a short one it is the `session-view-timeline` element itself. Each turn appears in this order: `kiro-checkpoint-bar`, then the user message (one or more items, since Kiro renders it as Markdown), then a `kiro-face` image, then `agent-message-role` ("Kiro"), then `collapsible-toggle` buttons, then the answer, then the footer texts.
- A turn that ended before Kiro wrote anything (cut off within seconds, or stopped at once), and a turn that has only just begun, have no "Kiro" label yet: then the newest checkpoint bar comes after the newest label, and the user message is what follows that bar (`app/Panel.cs`).
- Markdown in a user message is rendered. A line of only `=` or `-` turns the line above it into a heading.
- The chat box (`chat-input-content`, TipTap) has no ValuePattern. The text is set through MSAA instead: walk the render window's MSAA tree to the node with the chat box's bounds, then call `set_accValue`. Chromium turns that into setInnerText, and the editor picks it up. The send button (`codicon-arrow-up`) is pressed with `InvokePattern`, and so is the stop button, which is named `Cancel` and exists only while a turn runs.

## Timing

- When Kiro is minimized or covered by other windows, what UIA reads lags behind by seconds. After a tab switch it can show the old session for a while, typed text can take 8 s to appear, and a new session's tab took over 30 s. Pressing again opened a second session. The program therefore polls instead of sleeping for a fixed time. After switching it waits until the panel's contents change and settle. Before typing it checks that the panel shows the record's last user message. After setting the text it polls for up to 15 s and sets it again once.
- A send pressed right after a stop is sometimes dropped. The program presses once more if the text is still in the box after 30 s.
- After Stop is pressed, the record shows the turn ended at once, while the tab can say Working for 20 s more. A stop counts as done once the record says so.

## The locked screen

Measured with the script version (11-02); the exe drives Kiro the same way.

- Locking the screen does not change the input desktop's name on Windows 11. `LogonUI.exe` running is the reliable sign of a locked screen.
- With the screen locked, UI Automation and MSAA still reach Kiro's window: a session that waited for the lock, then got until-done pushes, had its tab switched, its chat box set and send pressed twice while locked, 3 and 6 minutes after locking.
- With the screen unlocked, driving Kiro this way does not count as keyboard or mouse input (`GetLastInputInfo` kept counting up through a tab switch, `set_accValue` and a send) and does not move the foreground window. In the lock test the idle time did restart around the first send (the second waited 3 more minutes); the cause is not known.

## Notices and staying awake

- Notices go where `Notify.Route` says: to the accounts paired with the bot, in their own chat with it (`aibot_send_msg`), or, when nobody has paired or WeCom refuses the bot's id and secret, to the WeCom group webhook in `notify.webhook`: a POST of `{"msgtype":"text","text":{"content":...}}` as UTF-8, cut to 2000 bytes (WeCom allows 2048), answered with `errcode` 0. A failed post is logged as `notice not sent` and nothing else happens. The morning status goes the same way. Feishu was dropped with the script version.
- Staying awake is `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` on the watcher's thread while any record, or a sub-agent's `sess_<id>\sub-executions\*.jsonl`, was written in the last `keepAwake.minutes`. A sub-agent leaves the parent's `messages.jsonl` untouched while it runs (20 minutes and more seen), hence the second place to look. On the test machine `CallNtPowerInformation(SystemExecutionState)` read 0x0 before the watcher started and 0x1 after. Switching it off in the window lets Windows sleep again at once.

## Commands from your phone

- A WeCom smart bot in API mode with a long connection: the client connects to `wss://openws.work.weixin.qq.com` and talks in JSON text frames `{ cmd, headers: { req_id }, body }`, as WeCom's own aibot SDKs (Python, Node) do. Right after connecting it sends `aibot_subscribe` with `body: { bot_id, secret }`; the answer carries the same `req_id` and `errcode` 0. Then `ping` every 30 s, answered the same way. A message to the bot (in a single chat, or an @mention in a group) arrives as `aibot_msg_callback`, with `body.msgid`, `body.from.userid`, `body.msgtype` and `body.text.content` (`body.voice.content` holds the transcript of a voice message). A reply is `aibot_respond_msg` with the callback's `req_id` and a `stream` body; a frame with `finish: false` is replaced by the next one with the same stream id, which is how "typing into #2..." turns into "sent".
- WeCom keeps one connection per bot: a new subscription takes over from the old one. Others have reported that after reconnecting, group messages sometimes stopped arriving; single chats are the safer place for commands.
- The connection (`ClientWebSocket`) runs on its own background thread, so heartbeats go on while the watcher spends a minute typing into Kiro. It talks to the watcher's loop through three concurrent queues (frames in, frames out, log lines). It goes through the system proxy; when connecting fails, every other try goes direct. The secret is never logged.
- Only paired accounts are obeyed. Until one has paired, a random six-digit code is shown on the window's Phone page and in the log; whoever sends it is saved in `remote-allow.json`. Five wrong codes turn pairing off until the program starts again.
- Session numbers are kept in `session-numbers.json`, so "continue 3" means the same session after a restart of the program.
- "stop N" selects the session's tab, waits until the panel shows the record's last message (a running session's panel keeps changing, so it waits for the switch and that message rather than for the panel to settle), and presses `Cancel`.
- Text for a running session waits in `queue.json` (read and written on every use, so a restart of the program keeps it) and goes in when that session's turn ends normally: a cut-off is continued first, and after a stop the text keeps waiting. The bot then says so on its own with `aibot_send_msg` (body `{ chatid, msgtype: "markdown", markdown: { content } }`; in a single chat the chat id is the user id).
- "new" presses the chat panel's `New session` button once (a second press would open a second session when the first tab shows up late), waits for the new tab and its empty panel, types, and finds the new record by its first message to give it a number.
- A message with no text (an image, a file) gets a short "text only" answer; a mixed message gives its text parts.

## The window and the tray

- One copy runs at a time (a named mutex). Starting the exe again brings up the running copy's window; `--exit` asks the running copy to quit, as the tray menu's Exit does (both through named events), so an update can close it cleanly.
- Closing the window only hides it (the first time, a balloon says so). The tray icon's menu opens the window, pauses and exits. A session that needs you, or an until-done task that finished, also pops a balloon.
- The session list is the records written in the last `remote.listHours`, at most `remote.maxList`, the same list as the phone's `status`. "Recent" is kept in `activity.jsonl` and is worded in the window's language when shown.
- "Start with Windows" writes `"<exe>" --tray` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. If the exe was moved, the entry is pointed at the new place the next time it starts.
- The window's language is the `language` setting, or Windows' own language with `auto`. The messages it types into Kiro follow `language` too, but `auto` there means the language of your task.

## Where it keeps things

`%APPDATA%\KiroAutoContinue\`:

| File | What |
|---|---|
| `config.json` | Your settings: only the keys that differ from the defaults. The window writes the ones it owns. |
| `kiro-auto-continue.log` | Everything it does. "Full log" in the window opens it. |
| `activity.jsonl` | The window's "Recent" list. |
| `session-numbers.json` | The number of each session, for the phone. |
| `queue.json` | Text from your phone waiting for a running session. |
| `remote-allow.json` | The accounts paired with the bot. |
| `report-state.json` | The day the morning status last went out. |

The environment variable `KAC_DATA_DIR` puts all of this somewhere else. A copy started that way also gets its own single-instance lock, so a test copy can run next to the real one.

## Command-line options

| Option | What it does |
|---|---|
| `--tray` | Starts hidden in the tray (what "Start with Windows" uses). |
| `--exit` | Asks the running copy to quit. Exit code 0 if one was running. |
| `--preview` | Shows the window with no watcher: nothing is typed, sent or saved, and it runs next to a real copy. Add `--sample` for made-up states, so every style shows; `--page=0`, `1` or `2`, `--lang=zh` or `en`, and `--size=720x680` pick what is shown. |
| `--shot=<png>` | With `--preview`: draws the window off screen at the screen's DPI, writes the picture and `<png>.txt` (every text that is cut off, overlaps another, or sticks out of the window), and exits with 1 if there was any. |
| `--texts=<file>` | Shows no window; writes what the phone's `status` and the morning status would say now. |

## All settings

The defaults are built into the exe (`app/config.default.json`). `%APPDATA%\KiroAutoContinue\config.json` is read on top of them, key by key; it is read when the program starts.

| Key | Default | Meaning |
|---|---|---|
| `pollSeconds` | 5 | How often it reads the session records. |
| `idleSeconds` | 180 | Type only after this many seconds without keyboard or mouse input. |
| `settleSeconds` | 20 | Wait this long after a turn ends before deciding. |
| `resume.delaysSeconds` | 60, 300, 900 | Wait before the 1st, 2nd, 3rd continue after a cut-off. |
| `resume.maxTries` | 3 | Cut-offs in a row before it gives up on a session. |
| `resume.maxPerRun` | 200 | Messages it sends at most in one run. |
| `resume.catchUpMinutes` | 60 | When it starts, it also continues the turns cut off this many minutes before, so restarting it drops nothing. |
| `resume.limitErrors` | `UsageLimitReachedError`, `OverageLimitReachedError` | Error types that mean the credits ran out: wait for them to come back (or for a Kiro restart) instead of trying again. |
| `resume.creditWaitHours` | 24 | When it starts, sessions that ran out of credits in this many hours before wait for the credits to come back. |
| `report.time`, `report.hours`, `report.windowHours` | 08:00, 12, 3 | When the morning status goes out (`""`: never), how many hours it covers, and how late after that time it may still go out. |
| `restartWindowMinutes` | 10 | An `error` at most this long before Kiro restarted counts as cut short by the restart; also how soon after Kiro it has to be started to pick up what a restart cut short. |
| `kiroStartupSeconds` | 60 | After Kiro starts, wait this long before typing into it. |
| `keepAwake.enabled`, `keepAwake.minutes` | true, 60 | Keep Windows from sleeping on idle while some record was written in the last this many minutes. |
| `notify.webhook` | | A WeCom group bot's webhook URL. Empty: notices go only through the paired bot, if any. |
| `notify.events` | `needsYou`, `done` | Which notices go out. |
| `remote.botId`, `remote.secret` | | The WeCom smart bot's Bot ID and Secret (long connection). Empty: no commands from your phone. |
| `remote.allowFrom` | | WeCom user ids obeyed besides the paired ones. |
| `remote.idleSeconds`, `remote.waitSeconds` | 15, 60 | Before typing a command from your phone, wait for this many seconds without keyboard or mouse input, but at most this long. |
| `remote.listHours`, `remote.maxList` | 24, 10 | Which sessions the window and `status` list. |
| `untilDone.enabled` | true | Turn until-done off entirely. |
| `untilDone.maxPushes` | 30 | Pushes per until-done run. |
| `untilDone.goalEvery` | 5 | Repeat the goal every this many pushes. |
| `untilDone.verifyOnDone` | true | The first `[DONE]` gets a request to check the goal item by item. |
| `untilDone.maxIdlePushes`, `untilDone.idleChars` | 3, 300 | Stop after this many pushes in a row with no tool calls and a reply shorter than this. |
| `language` | auto | `zh` or `en`. With `auto`, the window and the notices follow Windows' language, and the messages it types follow the language of your task. |
| `words.zh`, `words.en` | | The marks, every message it sends, and every word of the window. Lines of only `=` or `-` would make Kiro render the line above as a heading, so avoid them. |
| `autoPrefixes`, `bareContinue` | | How its own messages start (never quoted as your task), and messages such as "continue" that are not a task (it quotes the one before). |

`startWithWindows` and `trayHinted` are written by the program itself. `recordTailBytes`, `notify.timeoutSeconds`, `remote.url`, `placeholders` and `testing` (`cancelIsInterrupt`: a stop you press counts as a cut-off; `startPaused`: start paused, so a test copy never types) are for development.

## Building and testing

- Building needs the .NET SDK (8 was used): `dotnet build app\KiroAutoContinue.csproj -c Release` makes `app\bin\Release\net48\KiroAutoContinue.exe`. The `Microsoft.NETFramework.ReferenceAssemblies` package lets it target .NET Framework 4.8 without the developer pack. The exe needs nothing next to it: the defaults and the icon are inside, and the `.exe.config` beside it only names .NET Framework 4.8.
- The C# and XAML sources are ASCII only. Every text the program shows or sends lives in `app/config.default.json`.
- The unit tests need neither Kiro nor the network, and use a scratch data folder of their own:

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File tests\cs\run.ps1
  ```

  `tests\cs\bin\Debug\net48\KiroAutoContinue.Tests.exe --replay <file>` runs no tests: for every session record on the PC it writes what it reads and would do, one JSON line each (long texts as hashes). That was used to compare the exe with the script version.
- The window, the watcher's loop and the UI Automation code are not in the unit tests; they were tried on the real Kiro. For that, start a test copy with `KAC_DATA_DIR` set to a folder of its own, and use `testing.cancelIsInterrupt` there on a throwaway session.
