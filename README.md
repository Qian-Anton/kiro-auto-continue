# kiro-auto-continue

[中文说明](README.zh-CN.md)

Keeps Kiro IDE chat sessions going while you are away. Windows only, a PowerShell script, no Kiro restart and no changes to Kiro itself.

- **Cut-off turns get continued.** Sometimes a turn just stops: the chat footer shows `Elapsed time` but no `Est. Credits Used`. When that happens, it sends a continue message that quotes your last task. If the agent never saw your message (for example "the model is experiencing a high volume of traffic"), it still knows what to do.
- **Until-done mode (opt-in, per message).** Put `[until-done]` (or `【做到完】`) in your message. Whenever the agent stops early to ask a question or ask whether to go on, it is told to decide for itself and keep going, until it writes `[DONE]` or `[STUCK] <reason>`.
- **Turns you stopped yourself are left alone.**
- **Notices on your phone (optional).** When a session needs you, or until-done finishes, it posts a short notice to a WeCom or Feishu group.
- **Windows stays awake while a session works.** It does not go to sleep on idle in the middle of the night.
- **Commands from your phone (optional).** Through a WeCom smart bot: ask what each session is doing, have one carry on, or type a message into it.

## How it decides

It reads Kiro's own session records (`%USERPROFILE%\.kiro\sessions\<workspace>\sess_<id>\messages.jsonl`). Every turn ends with a `turn_end` whose `stopReason` says how it ended, and an `error` comes with the message the chat showed. It never switches tabs just to look.

| The turn ended with | What it does |
|---|---|
| `error` (cut off; footer shows no credits) | Sends the continue message: "the last turn was interrupted; if you remember what you were doing, pick up from there and do not start over; otherwise here is my message". Waits 1, 5, then 15 minutes before each try; after 3 cut-offs in a row it leaves the session and logs `NEEDS YOU`. A normal finish resets the count. |
| `error` because the credits ran out (`UsageLimitReachedError`, `OverageLimitReachedError`) | Nothing for now: a retry cannot work until there are credits again. Logs `NEEDS YOU - out of credits`. It continues the turn, however long ago it stopped, once another session finishes a turn normally (so the credits are back) or once Kiro has restarted (after you signed in again, for example). |
| Cut short by a Kiro restart | Sends the continue message, which also says terminals and background processes may be gone, once Kiro has been up for a minute. A turn that was running when Kiro went down has no `turn_end` at all; Kiro closes it (with a `turn_end` that says `cancelled` although you did not stop it) only when you next type into that session. So it counts a turn still open although Kiro started after its last line, and a turn that ended with `error` shortly before Kiro restarted. |
| `cancelled` (you pressed stop) | Nothing. Also ends until-done for that session. |
| `end_turn`, until-done off | Nothing. |
| `end_turn`, until-done on, no mark | Pushes it on. The first push restates the rules: decide for yourself and record each decision in the final report, do not ask whether to do the next step, stop only with `[DONE]` or with `[STUCK]` plus the reason. Every fifth push repeats the goal. |
| `end_turn`, `[DONE]` on the last lines | The first time, asks it to check the goal item by item. After that, stops and logs it. |
| `end_turn`, `[STUCK] reason` | Stops and logs `NEEDS YOU` with the reason. |

Until-done also stops after 30 pushes, after 3 pushes in a row that did no work (no tool calls and a short reply), or when you send a new message without the mark.

## How it types

Only when there is something to send. It waits until the keyboard and mouse have been idle for 3 minutes, then:

1. Finds the session's tab by its title (from `session.json`).
2. Shows that tab, and checks that the panel shows the same last message as the record.
3. Puts the text into the chat box through Windows UI Automation and MSAA. It never moves the mouse or presses keys.
4. Presses send, waits until the tab says Working, and goes back to the tab you had open.

It never types over a draft in the chat box.

## Use it

Requirements: Windows 10 or 11 (Windows PowerShell 5.1 comes with it), Kiro IDE with the chat panel open. Tested with Kiro 1.4.62.

1. Download this folder.
2. Check that it works with your Kiro (opens one short session, costs about 0.2 credits, closes it again):
   `powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -SelfTest`
3. Double-click `start.cmd`. Leave its window open; close it to stop.

Everything it does goes to `logs\kiro-auto-continue.log`. To see what it would do with your recent sessions without touching anything, run it with `-Inspect`. To watch decisions without typing, run it with `-DryRun`. `-Only <text>` limits it to sessions whose title contains the text.

While any session's record (or a sub-agent's) was written in the last 60 minutes, it keeps Windows from going to sleep on idle. The display can still turn off, and closing the lid, the power button or Start > Sleep still put the PC to sleep. On battery that costs charge; `keepAwake.enabled` turns it off.

## Notices on your phone

It can tell you when a session needs you (cut off three times in a row, out of credits, until-done stuck or stopped, or it could not send its message) and when until-done finishes, through a group bot's webhook:

- **WeCom**: in the group, `…` at the top right > Message push > Add; name it, save, and copy the webhook URL (`https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…`).
- **Feishu / Lark**: group settings > Bots > Add bot > Custom bot; copy the webhook URL. If you turn on signature verification, copy the secret too.

Anyone with the URL can post to that group, so put it in `config.json`, which stays out of git:

```json
{ "notify": { "webhook": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=...", "secret": "" } }
```

`kiro-auto-continue.ps1 -TestNotify` sends one test notice. `notify.events` picks what goes out: `needsYou`, `done`.

## Commands from your phone (WeCom)

With a WeCom smart bot you can ask the PC from your phone what each session is doing, have a session carry on, or type a message into it. The watcher keeps one outgoing WebSocket open to WeCom (`wss://openws.work.weixin.qq.com`), so no public IP or domain is needed.

1. In the WeCom admin console, open 智能专区 (smart zone) > 智能机器人 (smart bots) and create a bot in API mode with a long connection (长连接). Let only yourself see it. Copy its Bot ID and Secret.
2. Put them in `config.json` (kept out of git): `{ "remote": { "botId": "...", "secret": "..." } }`. `kiro-auto-continue.ps1 -TestRemote` checks that WeCom accepts them; stop the watcher first, since WeCom keeps one connection per bot.
3. Start the watcher. Until an account has paired, its window and log show a pairing code: send `配对 <code>` (or `pair <code>`) to the bot. Your account is saved in `remote-allow.json` (kept out of git), and from then on only that account is obeyed.

| You send | It does |
|---|---|
| `状态` / `status` | Lists the sessions whose records were written in the last 24 hours, newest first, each with a number and how its last turn ended. |
| `续 2` / `continue 2` | Has session 2 carry on: after a cut-off, the continue message quoting your task; otherwise a plain "continue". |
| `停 2` / `stop 2` | Stops session 2, as its stop button does. |
| `2 <text>` | Types the text into session 2, as if you typed it at the PC. While session 2 is running, the text waits and goes in once that turn finishes normally (a cut-off is continued first; after a stop the text keeps waiting), and the bot tells you when it went in. |
| `清 2` / `clear 2` | Drops what is waiting for session 2. |
| `新 my_project <text>` / `new my_project <text>` | Opens a new session in the Kiro window of my_project and sends the text. With one Kiro window open, the workspace can be left out. |
| `帮助` / `help` | Lists these. |

It reads text only; a voice message works too, since WeCom turns it into text. A session keeps its number across runs, and the notices name sessions by number too. Before typing it waits until the keyboard and mouse have been idle for 15 seconds. Anyone who can command the bot can make your agents run commands on this PC: keep the Secret to yourself and the bot visible only to you.

## Morning status

At 08:00 it sends one more notice: the sessions that worked in the last 12 hours, each with its number, how its last turn ended and how often it was continued, and in one line how many times it continued or pushed and how many sessions need you. It goes where the notices go (`notify.webhook`), or, with no webhook, to the accounts paired with the bot. It goes out once a day, only between 08:00 and 11:00, and not at all when no session worked. `kiro-auto-continue.ps1 -Report` makes one now and sends it to the webhook. `report.time` changes the hour; `""` turns it off.

## Settings

Defaults live in `config.default.json`. To change one, create `config.json` next to it with just the keys you want; it is read on top of the defaults (and stays out of git). For example:

```json
{ "idleSeconds": 120, "untilDone": { "maxPushes": 50 }, "language": "en" }
```

| Key | Default | Meaning |
|---|---|---|
| `idleSeconds` | 180 | Type only after this many seconds without keyboard or mouse input. |
| `settleSeconds` | 20 | Wait this long after a turn ends before deciding. |
| `resume.delaysSeconds` | 60, 300, 900 | Wait before the 1st, 2nd, 3rd continue after a cut-off. |
| `resume.maxTries` | 3 | Cut-offs in a row before it gives up on a session. |
| `resume.catchUpMinutes` | 60 | When it starts, it also continues the turns cut off this many minutes before, so restarting it drops nothing. |
| `resume.limitErrors` | `UsageLimitReachedError`, `OverageLimitReachedError` | Error types that mean the credits ran out: wait for them to come back (or for a Kiro restart) instead of trying again. |
| `resume.creditWaitHours` | 24 | When it starts, sessions that ran out of credits in this many hours before wait for the credits to come back. |
| `report.time`, `report.hours`, `report.windowHours` | 08:00, 12, 3 | When the morning status goes out (`""`: never), how many hours it covers, and how late after that time it may still go out. |
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
| `language` | auto | `zh` or `en` for the messages; `auto` picks Chinese when your task has Chinese in it. |
| `words.zh`, `words.en` | | The marks and every message it sends. Lines of only `=` or `-` would make Kiro render the line above as a heading, so avoid them. |

## Limits

- Windows only: it drives Kiro's chat panel through Windows UI Automation.
- It relies on how Kiro's chat panel and session records look today. A Kiro update can break it; `-SelfTest` and `-Inspect` show where.
- It handles turns that end, or that a Kiro restart cuts short, while it is running. If it was not running when Kiro restarted, start it within 10 minutes after Kiro: it then also picks up the cut-short turns whose record was written in the 10 minutes before Kiro started.
- The session's tab has to be open in a chat panel. Two tabs with the same title are skipped.
- Only the text of your message is quoted, not its images or attachments.
- With Kiro minimized or behind other windows, Kiro updates its panel slowly (seconds, sometimes a minute); it waits and checks before typing. A locked screen is fine: it switches tabs, types and sends while locked.
- In until-done mode the agent makes the calls you would normally make. Read its final report.
- Commands from your phone reach the PC only while the watcher runs and the PC is awake. With no session working for an hour, Windows may go to sleep on idle again.

Not affiliated with Kiro or Amazon Web Services.

## Tests

`tests\Policy.Tests.ps1`, `tests\Records.Tests.ps1`, `tests\Notify.Tests.ps1`, `tests\Remote.Tests.ps1` and `tests\Report.Tests.ps1` run without Kiro (and without sending or connecting anything):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Policy.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Records.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Notify.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Remote.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Report.Tests.ps1
```

`tests\fast.json` is a settings file for trying it on a throwaway session: short waits, and a stop you press yourself counts as a cut-off. Use it with `-Only` (sessions whose title contains a text) or `-OnlyNew` (sessions created after it started), so it cannot touch your real sessions:

```
powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -Config tests\fast.json -OnlyNew
```

Then open a new session and ask for something slow (for example "run Start-Sleep -Seconds 300"), press stop while it runs, and watch the log: it should send the continue message, then wait 10 s, then 15 s, and give up on the fourth stop. For until-done, start a new session with `[until-done]` and a task that stops to ask after its first step.

## License

MIT, see [LICENSE](LICENSE).
