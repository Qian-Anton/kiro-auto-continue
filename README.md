# kiro-auto-continue

[中文说明](README.zh-CN.md)

Kiro sessions sometimes stop halfway through a task: the connection drops, the model is overloaded, the credits run out, or Kiro restarts. If you are asleep or away from the PC, the task waits there until you come back.

kiro-auto-continue watches your Kiro sessions in the background. When one stops like that, it asks the agent to continue and attaches your original request, so the agent knows what it was doing. It is a small Windows program. You don't install anything into Kiro or restart it.

## What it does

- If a turn gets cut off (a network error, an overloaded model), it asks the agent to continue. It tries up to three times in a row, waiting 1, 5 and 15 minutes.
- If you stopped a turn yourself, it leaves it alone.
- After Kiro restarts, it continues the sessions that were running.
- If the credits ran out, it does not retry. It continues after Kiro restarts, or once another session gets through a turn again, which means the credits are back.
- Until-done mode, if you want it: the agent keeps working instead of stopping to ask whether to go on.
- Its window lists your recent sessions and what each one is doing, with Continue and Stop buttons, and below them what it did lately.
- It shows the credits as Kiro last reported them, the same numbers as Kiro's status bar, with the time Kiro checked.
- Notices on your phone, and commands from your phone, through WeCom, if you set them up.
- While a session is working, the PC does not go to sleep on its own.

It types only after the keyboard and mouse have been idle for 3 minutes, then switches back to the tab you had open. It never touches a message you have started typing. It also works with the screen locked.

## Get started

You need Windows 10 or 11 and Kiro IDE (tested with 1.4.86).

1. Download `KiroAutoContinue.exe` from [Releases](https://github.com/Qian-Anton/kiro-auto-continue/releases) and put it anywhere.
2. Double-click it. The first time, Windows may say "Windows protected your PC", because the program is not signed: click "More info", then "Run anyway".
3. While the window is open, it is watching Kiro. Close the window and it keeps watching from the tray at the bottom right. To quit, right-click the tray icon and choose Exit.

To have it running whenever you log in, turn on "Start with Windows" on the Settings page.

Everything it does goes into its log; "Full log" in the window opens it. Its settings and log live in `%APPDATA%\KiroAutoContinue` and stay on this PC.

## Until-done mode

Put `[until-done]` (or `【做到完】`) anywhere in your message. When the agent stops to ask a question, or to ask whether it should go on, it is told to decide for itself and keep going. The first time the agent says it is done, it is asked to check its work against your request once more.

It stops when the agent writes `[DONE]` or `[STUCK] <reason>`, after 30 pushes, or when the agent does nothing three times in a row. Sending a message without the mark ends it too.

Read the agent's final report afterwards: it made decisions you would normally make yourself.

## Notices on your phone

The easiest way is the WeCom bot in the next section: once you have paired with it, notices come from the bot in your chat with it, and that is where you send commands too.

For notices only, without a bot, use a WeCom group's message push: in the group, tap `…` at the top right, then Message push > Add, copy the webhook URL, paste it under "Notices" on the window's Phone page, and click "Send a test".

You get a message when a session needs you (cut off three times in a row, out of credits, stuck in until-done) and when an until-done task is finished. At 8:00 every morning you also get a short summary of the last 12 hours. Each of the three can be switched off on the Phone page.

Keep the webhook URL to yourself, since anyone who has it can post to the group.

## Commands from your phone (WeCom)

1. In the WeCom admin console (web), open 智能专区 (smart zone) > 智能机器人 (smart bots). Create a bot in API mode with a long connection (长连接), visible only to you, and copy its Bot ID and Secret.
2. Enter them under "Commands from your phone" on the window's Phone page and click Connect.
3. Once it is connected, a pairing code shows up there. Send `pair <code>` to the bot. From then on it takes commands only from you.

Then send the bot:

| Message | What happens |
|---|---|
| `status` | A line with the credits as Kiro reported them, then your recent sessions, each with a number. |
| `continue 2` | Session 2 carries on. |
| `stop 2` | Stops session 2. |
| `2 <text>` | Sends the text to session 2. If session 2 is busy, the text waits until the turn is done, and the bot tells you when it went in. |
| `clear 2` | Drops the text waiting for session 2. |
| `new <project> <text>` | Opens a new session in that project's Kiro window and sends the text. With one Kiro window open, you can leave out the project. |
| `help` | Shows this list. |

The Chinese commands work too (`状态`, `续 2`, `停 2`, `清 2`, `新`, `帮助`), and so do voice messages. Before a command types into Kiro, it waits until the keyboard and mouse have been idle for 15 seconds.

Keep the Secret private and the bot visible only to you: whoever can command the bot can make the agents on your PC run commands.

## Settings

The Settings page has: start with Windows, keep the PC from sleeping or hibernating while a session works, how long the keyboard and mouse must be idle before it types, and the language.

Any other setting goes into `%APPDATA%\KiroAutoContinue\config.json`. Add just the key you want to change:

```json
{ "untilDone": { "maxPushes": 50 } }
```

Then quit the program and start it again. All settings are listed in [docs/how-it-works.md](docs/how-it-works.md#all-settings).

## Coming from the script (v0.2)

First stop the script (close the `start.cmd` window). With both running, they would compete to continue the same sessions, and WeCom keeps only one connection per bot.

Then, before you start the new program, copy `config.json` and `remote-allow.json` from the script's folder into `%APPDATA%\KiroAutoContinue`: that carries over the webhook, the bot and the pairing. Or enter them again on the Phone page and pair again. Feishu groups are no longer supported.

The script version stays available as [v0.2](https://github.com/Qian-Anton/kiro-auto-continue/releases/tag/v0.2).

## Limits

- Windows only.
- It relies on how Kiro's chat panel looks today, so a Kiro update can break it.
- The session's tab has to be open in Kiro.
- Phone commands arrive only while kiro-auto-continue is running and the PC is awake.
- The credits are copied from what Kiro reports. Kiro decides how often it checks, and its numbers do not always match what you can still use.

## More

How it works, the command-line options, and how to build it and run the tests: [docs/how-it-works.md](docs/how-it-works.md).

Not affiliated with Kiro or Amazon Web Services. MIT license, see [LICENSE](LICENSE).
