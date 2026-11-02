# kiro-auto-continue

[中文说明](README.zh-CN.md)

Kiro sessions sometimes stop halfway through a task: the connection drops, the model is overloaded, the credits run out, or Kiro restarts. If you are asleep or away from the PC, the task waits there until you come back.

kiro-auto-continue watches your Kiro sessions in the background. When one stops like that, it asks the agent to continue and attaches your original request, so the agent knows what it was doing. It is a PowerShell script for Windows. You don't install anything into Kiro or restart it.

## What it does

- If a turn gets cut off (a network error, an overloaded model), it asks the agent to continue. It tries up to three times in a row, waiting 1, 5 and 15 minutes.
- If you stopped a turn yourself, it leaves it alone.
- After Kiro restarts, it continues the sessions that were running.
- If the credits ran out, it does not retry. It continues after Kiro restarts, or once another session gets through a turn again, which means the credits are back.
- Until-done mode, if you want it: the agent keeps working instead of stopping to ask whether to go on.
- Notices on your phone through a WeCom or Feishu group, and commands from your phone through a WeCom bot, if you set them up.
- While a session is working, the PC does not go to sleep on its own.

It types only after the keyboard and mouse have been idle for 3 minutes, then switches back to the tab you had open. It never touches a message you have started typing. It also works with the screen locked.

## Get started

You need Windows 10 or 11 and Kiro IDE (tested with 1.4.62).

1. Download the zip from [Releases](https://github.com/Qian-Anton/kiro-auto-continue/releases) and unzip it.
2. Optional: check that it works with your Kiro. This opens a short test session (about 0.2 credits) and closes it again:
   `powershell -NoProfile -ExecutionPolicy Bypass -File kiro-auto-continue.ps1 -SelfTest`
3. Double-click `start.cmd`. Leave its window open. Close it to stop.

Everything it does goes to `logs\kiro-auto-continue.log`.

## Until-done mode

Put `[until-done]` (or `【做到完】`) anywhere in your message. When the agent stops to ask a question, or to ask whether it should go on, it is told to decide for itself and keep going. The first time the agent says it is done, it is asked to check its work against your request once more.

It stops when the agent writes `[DONE]` or `[STUCK] <reason>`, after 30 pushes, or when the agent does nothing three times in a row. Sending a message without the mark ends it too.

Read the agent's final report afterwards: it made decisions you would normally make yourself.

## Notices on your phone

First create a webhook for a group chat:

- WeCom: in the group, tap `…` at the top right, then Message push > Add, and copy the webhook URL.
- Feishu: group settings > Bots > Add bot > Custom bot, and copy the webhook URL.

Create `config.json` next to the script and put the URL in it:

```json
{ "notify": { "webhook": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=..." } }
```

For a Feishu bot with signature verification, add its secret too: `"secret": "..."` next to `"webhook"`.

`kiro-auto-continue.ps1 -TestNotify` sends a test message. From then on you get a message when a session needs you (cut off three times in a row, out of credits, stuck in until-done) and when an until-done task is finished. At 8:00 every morning you also get a short summary of the last 12 hours.

Keep the URL to yourself, since anyone who has it can post to the group. `config.json` is not tracked by git.

## Commands from your phone (WeCom)

1. In the WeCom admin console, open 智能专区 (smart zone) > 智能机器人 (smart bots). Create a bot in API mode with a long connection (长连接), visible only to you, and copy its Bot ID and Secret.
2. Add them to the same `config.json`: `{ "notify": { ... }, "remote": { "botId": "...", "secret": "..." } }`
3. Start kiro-auto-continue. Its window shows a pairing code: send `pair <code>` to the bot. From then on it takes commands only from you.

Then send the bot:

| Message | What happens |
|---|---|
| `status` | Lists your recent sessions, each with a number. |
| `continue 2` | Session 2 carries on. |
| `stop 2` | Stops session 2. |
| `2 <text>` | Sends the text to session 2. If session 2 is busy, the text waits until the turn is done, and the bot tells you when it went in. |
| `clear 2` | Drops the text waiting for session 2. |
| `new <project> <text>` | Opens a new session in that project's Kiro window and sends the text. With one Kiro window open, you can leave out the project. |
| `help` | Shows this list. |

The Chinese commands work too (`状态`, `续 2`, `停 2`, `清 2`, `新`, `帮助`), and so do voice messages. Before a command types into Kiro, it waits until the keyboard and mouse have been idle for 15 seconds. Without a notice group, the morning summary comes from the bot.

Keep the Secret private and the bot visible only to you: whoever can command the bot can make the agents on your PC run commands.

## Settings

The defaults are in `config.default.json`. To change one, add just that key to `config.json`:

```json
{ "idleSeconds": 120, "untilDone": { "maxPushes": 50 } }
```

The ones you are most likely to change:

| Setting | Default | Meaning |
|---|---|---|
| `idleSeconds` | 180 | Seconds without keyboard or mouse input before it types. |
| `resume.maxTries` | 3 | Cut-offs in a row before it stops trying and tells you. |
| `untilDone.maxPushes` | 30 | The most pushes in one until-done run. |
| `report.time` | `"08:00"` | When the morning summary goes out. `""` turns it off. |
| `keepAwake.enabled` | `true` | Keep the PC awake while a session is working. |
| `language` | `"auto"` | `"zh"` or `"en"` for the messages it sends. `"auto"` follows the language of your request. |

The rest are listed in [docs/how-it-works.md](docs/how-it-works.md#all-settings).

## Limits

- Windows only.
- It relies on how Kiro's chat panel looks today, so a Kiro update can break it. `-SelfTest` tells you whether it still works.
- The session's tab has to be open in Kiro.
- Phone commands arrive only while kiro-auto-continue is running and the PC is awake.

## More

How it works, all command-line options and how to run the tests: [docs/how-it-works.md](docs/how-it-works.md).

Not affiliated with Kiro or Amazon Web Services. MIT license, see [LICENSE](LICENSE).
