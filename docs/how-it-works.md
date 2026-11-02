# How it works (notes for contributors)

Measured against Kiro IDE 1.4.62 on Windows 11, November 2026. Kiro documents none of this, so expect it to move.

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

Only when there is something to send, and only after `idleSeconds` without keyboard or mouse input (`remote.idleSeconds` for commands from your phone):

1. Find the session's tab by its title (from `session.json`). Two tabs with the same title are skipped.
2. Show that tab, and check that the panel shows the same last message as the record.
3. Put the text into the chat box through UI Automation and MSAA, without moving the mouse or pressing keys. A draft in the box is left alone.
4. Press send, wait until the tab says Working, and show the tab you had open again.

## Kiro's session records

`%USERPROFILE%\.kiro\sessions\<workspace hash>\sess_<uuid>\`

- `session.json`: `id`, `title` (what the chat tab shows), `workspacePaths`, `status` (`in_progress`, `idle`, `waiting_on_user`), `createdAt`, `lastModifiedAt`.
- `messages.jsonl`: one JSON object per line, appended while a turn runs. The kinds used here, under `payload.type`:
  - `user`: `content` is the message as sent. Line breaks arrive as Markdown hard breaks, two spaces and a newline.
  - `turn_start`, then for the agent `assistant` (the visible text has `operationType: "Say"`) and `tool_call` / `tool_result`.
  - `usage_summary`: `status` (`success`, `aborted`, `failed`), `elapsedTime` in ms, and credits in `promptTurnSummaries[].usage`.
  - `turn_end`: `stopReason` is `end_turn` (finished), `cancelled` (the user pressed stop) or `error` (cut off).
- In one workspace's history: 399 `end_turn`, 111 `cancelled`, 70 `error`. A turn stopped early by the user can have zero credits, so the chat footer looks just like a cut-off. The record still says `cancelled`.
- Lines can be megabytes (images, tool output). `src/Records.ps1` pulls fields out with regexes instead of parsing whole lines, and reads only the tail of the file.
- Every `error` turn has a `session_metadata` line with `key: "displayError"` before its `turn_end`: the error the chat showed, as `value.errorType` and `value.message`. Across all workspaces (197 errors): `ConnectionResetError` 65, `ModelThrottleError` 51, `UsageLimitReachedError` 30, `ClientNetworkError` 12, `ClientThrottleError` 7, `OverageLimitReachedError` 4, and a long tail (bad images, content filter, proxy, sign-in). The two limit errors mean the account is out of credits; retrying on it cannot work.
- Kiro writes a few `user` lines itself, mid-turn, marked `_meta.kiro.syntheticUserMessageReason` (`max-tokens-recovery`, `fallback-continuation`; 19 of 8969). They are not the user's task. Whether the chat panel shows them has not been checked.

## What a Kiro restart leaves in the records

From two restarts after the credits ran out (11-01 07:28 UTC after `UsageLimitReachedError`, and 18:56 after `OverageLimitReachedError`) and the other restarts in the history:

- The turns that were waiting on the model when the credits ran out ended with `error` before the restart (four, from 18:54:33 to 18:55:00; Kiro was back at 18:56:11).
- A turn busy with a tool when Kiro went down gets nothing and stays open in the record. Kiro does not close it when it starts. It writes a `turn_end` with `"boundarySource": "recovered"` and `stopReason: "cancelled"` only when that session is used again: all seven such lines came 2 to 20 seconds before someone typed "continue" into that session, apparently when its tab was opened.
- `session.json` keeps `status: "in_progress"` for those sessions, so the status cannot tell them from running ones.
- While a sub-agent runs, the parent session's record can go 20 minutes or more without a new line. A quiet record does not mean a dead turn.

So the tool counts a turn as cut short by a restart when it is still open and its last line is older than the start of Kiro's oldest `Kiro.exe`, when it ended with `error` at most `restartWindowMinutes` before that start (any time before, for a limit error), or when it ended with the recovered `turn_end`. It notices a restart when that start time changes, then reads every record written since the previous start (20 records of up to 53 MB took 12 s). Replayed on the records of both switches (each record cut at the moment after Kiro came back), it picks every session the user then continued by hand, and leaves the finished ones alone. Not seen yet: the tool typing after a real restart, and what such a session's tab shows then. It ignores a `Working` state on those tabs and relies on the record and the idle check.

## Out of credits, and the morning status

- Every `error` turn has a `displayError` (see above). For the two limit errors, retrying cannot work until there are credits again. Such sessions wait; when another session finishes a turn normally, the credits are back, and the waiting ones are continued (the turn gets `CreditsBack`, and the decision sends the plain continue message). A Kiro restart continues them too. When the watcher starts, the sessions whose last turn hit a limit error in the last `resume.creditWaitHours` wait the same way.
- The morning status is the status list over `report.hours`, with counts taken from the watcher's own log lines (`; sent resume`, `; sent push` and the other until-done kinds, `; sent queued`, `NEEDS YOU - `). The log has no year: a date after now belongs to the year before. It goes out once a day between `report.time` and `report.windowHours` later; the day it went out is kept in `logs\report-state.json`.

## The chat panel through UI Automation

- The chat panel is a webview. Chromium exposes it to UI Automation, but `FindAll`/`FindFirst` search the control view by default, which hides plain divs. A pushed `CacheRequest` with `RawViewCondition` makes them search the raw view.
- Tabs are elements of class `tab-bar-item`. The state is the name of the `tab-bar-item-indicator` child (`Working`, `Updated`, `Needs your input`). `GetRuntimeId()` stays the same when a tab is renamed and changes when Kiro restarts.
- A session's messages form one flat list. In a long session the list is `session-view-content`. In a short one it is the `session-view-timeline` element itself. Each turn appears in this order: `kiro-checkpoint-bar`, then the user message (one or more items, since Kiro renders it as Markdown), then a `kiro-face` image, then `agent-message-role` ("Kiro"), then `collapsible-toggle` buttons, then the answer, then the footer texts.
- Markdown in a user message is rendered. A line of only `=` or `-` turns the line above it into a heading.
- The chat box (`chat-input-content`, TipTap) has no ValuePattern. The text is set through MSAA instead: walk the render window's MSAA tree to the node with the chat box's bounds, then call `set_accValue`. Chromium turns that into setInnerText, and the editor picks it up. The send button (`codicon-arrow-up`) is pressed with `InvokePattern`.

## Timing

- When Kiro is minimized or covered by other windows, what UIA reads lags behind by seconds. After a tab switch it can show the old session for a while, typed text can take 8 s to appear, and a new session's tab took over 30 s. Pressing again opened a second session. The tool therefore polls instead of sleeping for a fixed time. After switching it waits until the panel's contents change and settle. Before typing it checks that the panel shows the record's last user message. After setting the text it polls for up to 15 s and sets it again once.
- A send pressed right after a stop is sometimes dropped. The tool presses once more if the text is still in the box after 30 s.

## Notices and staying awake

- A notice is a plain text message posted to a group bot's webhook: WeCom `{"msgtype":"text","text":{"content":...}}` (at most 2048 bytes; it answers `errcode` 0), Feishu `{"msg_type":"text","content":{"text":...}}` (with signature verification also `timestamp` and `sign`; it answers `code` 0). Windows PowerShell's `Invoke-RestMethod` would send a string body as ISO-8859-1, so the body goes out as UTF-8 bytes. A failed post is logged as `notice not sent` and nothing else happens.
- Staying awake is `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` on the watcher's thread while any record, or a sub-agent's `sess_<id>\sub-executions\*.jsonl`, was written in the last `keepAwake.minutes`. A sub-agent leaves the parent's `messages.jsonl` untouched while it runs (20 minutes and more seen), hence the second place to look. On the test machine `CallNtPowerInformation(SystemExecutionState)` read 0x0 before the watcher started and 0x1 after.

## Commands from your phone

- A WeCom smart bot in API mode with a long connection: the client connects to `wss://openws.work.weixin.qq.com` and talks in JSON text frames `{ cmd, headers: { req_id }, body }`, as WeCom's own aibot SDKs (Python, Node) do. Right after connecting it sends `aibot_subscribe` with `body: { bot_id, secret }`; the answer carries the same `req_id` and `errcode` 0. Then `ping` every 30 s, answered the same way. A message to the bot (in a single chat, or an @mention in a group) arrives as `aibot_msg_callback`, with `body.msgid`, `body.from.userid`, `body.msgtype` and `body.text.content` (`body.voice.content` holds the transcript of a voice message). A reply is `aibot_respond_msg` with the callback's `req_id` and a `stream` body; a frame with `finish: false` is replaced by the next one with the same stream id, which is how "typing into #2..." turns into "sent".
- WeCom keeps one connection per bot: a new subscription takes over from the old one. Others have reported that after reconnecting, group messages sometimes stopped arriving; single chats are the safer place for commands.
- The connection runs on its own runspace, so heartbeats go on while the watcher spends a minute typing into Kiro. It talks to the watcher's loop through three concurrent queues (frames in, frames out, log lines). .NET Framework's `ClientWebSocket` worked on the test machine (11-02), through the system proxy; when connecting fails, every other try goes direct.
- Only paired accounts are obeyed. Until one has paired, a random six-digit code is shown in the watcher's window and log; whoever sends it is saved in `remote-allow.json`. Five wrong codes turn pairing off until the watcher starts again.
- Session numbers are kept in `logs\session-numbers.json`, so "continue 3" means the same session after a restart of the watcher.
- "stop N" selects the session's tab, waits until the panel shows the record's last message (a running session's panel keeps changing, so it waits for the switch and that message rather than for the panel to settle), and presses the button named `Cancel`, which the panel has only while a turn runs.
- Text for a running session waits in `logs\queue.json` (read and written on every use, so a restart of the watcher keeps it) and goes in when that session's turn ends normally: a cut-off is continued first, and after a stop the text keeps waiting. The bot then says so on its own with `aibot_send_msg` (body `{ chatid, msgtype: "markdown", markdown: { content } }`; in a single chat the chat id is the user id).
- "new" presses the chat panel's `New session` button once (a second press would open a second session when the first tab shows up late), waits for the new tab and its empty panel, types, and finds the new record by its first message to give it a number.
- A message with no text (an image, a file) gets a short "text only" answer; a mixed message gives its text parts.

## Windows PowerShell 5.1 pitfalls

- Scripts without a BOM are read in the system code page (GBK on Chinese Windows), so every `.ps1` here is ASCII. All text the agent sees lives in UTF-8 JSON.
- Variable names are case-insensitive: `$ts = Get-Date` replaces a global `$TS`. Globals here start with `Kac`.
- Locking the screen does not change the input desktop's name on Windows 11. `LogonUI.exe` running is the reliable sign of a locked screen.
- With the screen locked, UI Automation and MSAA still reach Kiro's window: in the 11-02 test (a session that waited for the lock, then got until-done pushes) it switched tabs, set the chat box text and pressed send twice while locked, 3 and 6 minutes after locking. With the screen unlocked, driving Kiro this way does not count as keyboard or mouse input (`GetLastInputInfo` kept counting up through a tab switch, `set_accValue` and a send) and does not move the foreground window. In the lock test the idle time did restart around the first send (the second waited 3 more minutes); the cause is not known.

## Command-line options

| Option | What it does |
|---|---|
| `-SelfTest` | Opens a new session, sends a short prompt, checks the reply and closes it (about 0.2 credits), then exits. |
| `-Inspect` | Prints the recent sessions and what it would do with each, then exits. Types nothing. |
| `-DryRun` | Decides and logs as usual, but never types. |
| `-Only <text>` | Watches only the sessions whose title contains the text. |
| `-OnlyNew` | Watches only the sessions created after it started. |
| `-Config <file>` | Reads this settings file on top of `config.default.json` and `config.json`. |
| `-TestNotify` | Sends a test notice to `notify.webhook`, then exits. |
| `-TestRemote` | Connects to WeCom as the bot in `remote.botId` / `remote.secret` and checks a heartbeat, then exits. Stop the watcher first: WeCom keeps one connection per bot. |
| `-Report` | Makes the morning summary now, prints it and sends it to `notify.webhook`, then exits. |

`start.cmd` passes its arguments on, so `start.cmd -DryRun` works too.

## All settings

Defaults live in `config.default.json`; `config.json` and the `-Config` file are read on top of it, key by key.

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
| `report.time`, `report.hours`, `report.windowHours` | 08:00, 12, 3 | When the morning summary goes out (`""`: never), how many hours it covers, and how late after that time it may still go out. |
| `restartWindowMinutes` | 10 | An `error` at most this long before Kiro restarted counts as cut short by the restart; also how soon after Kiro it has to be started to pick up what a restart cut short. |
| `kiroStartupSeconds` | 60 | After Kiro starts, wait this long before typing into it. |
| `keepAwake.enabled`, `keepAwake.minutes` | true, 60 | Keep Windows from sleeping on idle while some record was written in the last this many minutes. |
| `notify.webhook`, `notify.secret` | | A WeCom or Feishu group bot's webhook URL; the Feishu signing secret, if it has one. Empty: no notices. |
| `notify.events` | `needsYou`, `done` | Which notices go out. |
| `remote.botId`, `remote.secret` | | The WeCom smart bot's Bot ID and Secret (long connection). Empty: no commands from your phone. |
| `remote.allowFrom` | | WeCom user ids obeyed besides the paired ones. |
| `remote.idleSeconds`, `remote.waitSeconds` | 15, 60 | Before typing a command from your phone, wait for this many seconds without keyboard or mouse input, but at most this long. |
| `remote.listHours`, `remote.maxList` | 24, 10 | Which sessions `status` lists. |
| `untilDone.enabled` | true | Turn until-done off entirely. |
| `untilDone.maxPushes` | 30 | Pushes per until-done run. |
| `untilDone.goalEvery` | 5 | Repeat the goal every this many pushes. |
| `untilDone.verifyOnDone` | true | The first `[DONE]` gets a request to check the goal item by item. |
| `untilDone.maxIdlePushes`, `untilDone.idleChars` | 3, 300 | Stop after this many pushes in a row with no tool calls and a reply shorter than this. |
| `language` | auto | `zh` or `en` for the messages; `auto` picks Chinese when your task has Chinese in it. |
| `words.zh`, `words.en` | | The marks and every message it sends. Lines of only `=` or `-` would make Kiro render the line above as a heading, so avoid them. |
| `autoPrefixes`, `bareContinue` | | How its own messages start (never quoted as your task), and messages such as "continue" that are not a task (it quotes the one before). |

`recordTailBytes`, `notify.timeoutSeconds`, `remote.url`, `placeholders` and `testing` are for development.

## Tests

The unit tests run without Kiro, and without sending or connecting anything:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Policy.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Records.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Notify.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Remote.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Report.Tests.ps1
```

`tests\fast.json` is a settings file for trying it on a throwaway session: short waits, and a stop you press yourself counts as a cut-off. Use it with `-Only` or `-OnlyNew`, so it cannot touch your real sessions:

```
powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -Config tests\fast.json -OnlyNew
```

Then open a new session and ask for something slow (for example "run Start-Sleep -Seconds 300"), press stop while it runs, and watch the log: it should send the continue message, then wait 10 s, then 15 s, and give up on the fourth stop. For until-done, start a new session with `[until-done]` and a task that stops to ask after its first step.
