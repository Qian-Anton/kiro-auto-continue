# How it works (notes for contributors)

Measured against Kiro IDE 1.4.62 on Windows 11, November 2026. Kiro documents none of this, so expect it to move.

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

## Out of credits, and the morning status

- Every `error` turn has a `displayError` (see above). For the two limit errors, retrying cannot work until there are credits again. Such sessions wait; when another session finishes a turn normally, the credits are back, and the waiting ones are continued (the turn gets `CreditsBack`, and the decision sends the plain continue message). A Kiro restart continues them too. When the watcher starts, the sessions whose last turn hit a limit error in the last `resume.creditWaitHours` wait the same way.
- The morning status is the status list over `report.hours`, with counts taken from the watcher's own log lines (`; sent resume`, `; sent push` and the other until-done kinds, `; sent queued`, `NEEDS YOU - `). The log has no year: a date after now belongs to the year before. It goes out once a day between `report.time` and `report.windowHours` later; the day it went out is kept in `logs\report-state.json`.

## Windows PowerShell 5.1 pitfalls

- Scripts without a BOM are read in the system code page (GBK on Chinese Windows), so every `.ps1` here is ASCII. All text the agent sees lives in UTF-8 JSON.
- Variable names are case-insensitive: `$ts = Get-Date` replaces a global `$TS`. Globals here start with `Kac`.
- Locking the screen does not change the input desktop's name on Windows 11. `LogonUI.exe` running is the reliable sign of a locked screen.
- With the screen locked, UI Automation and MSAA still reach Kiro's window: in the 11-02 test (a session that waited for the lock, then got until-done pushes) it switched tabs, set the chat box text and pressed send twice while locked, 3 and 6 minutes after locking. With the screen unlocked, driving Kiro this way does not count as keyboard or mouse input (`GetLastInputInfo` kept counting up through a tab switch, `set_accValue` and a send) and does not move the foreground window. In the lock test the idle time did restart around the first send (the second waited 3 more minutes); the cause is not known.
