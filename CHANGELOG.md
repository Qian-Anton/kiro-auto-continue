# Changelog

## v0.2 (2026-11-02)

- **Commands from your phone** through a WeCom smart bot over its long connection (no public IP or domain needed): `status`, `continue N`, `stop N`, `N <text>`, `clear N`, `new <workspace> <text>` and `help`, in Chinese or English. An account pairs once with a code from the watcher's window; only paired accounts are obeyed. Text for a running session waits in a queue and goes in after that turn finishes normally, and the bot tells you when it did. Session numbers stay the same across runs.
- **Notices** to a WeCom or Feishu group when a session needs you (cut off three times in a row, out of credits, until-done stuck, a message that could not be sent) and when until-done finishes. `-TestNotify` sends a test notice.
- **Morning status** at 08:00: the sessions that worked in the last 12 hours, how each last turn ended, and how often the watcher continued or pushed them. `-Report` sends one now.
- **Kiro restarts:** turns cut short by a restart are continued once Kiro has been up for a minute, including turns that were busy with a tool and never got a `turn_end`.
- **Out of credits** (`UsageLimitReachedError`, `OverageLimitReachedError`): no retries. The session waits until another session finishes a turn normally, or Kiro restarts, and is then continued.
- **Catching up on start:** turns cut off in the hour before the watcher started, and sessions that ran out of credits in the last 24 hours.
- **Windows stays awake** while a session works.
- Works with the screen locked (tested: tab switches, typing and sending while locked).
- The user messages Kiro writes itself mid-turn are no longer quoted as your task.

## v0.1 (2026-11-02)

- Continues cut-off turns (`stopReason: error`) with a message that quotes your last task. Waits 1, 5 and 15 minutes; after 3 cut-offs in a row it leaves the session and logs `NEEDS YOU`.
- Until-done mode, per message (`[until-done]` or `【做到完】`): the agent is pushed on until it writes `[DONE]` or `[STUCK] <reason>`.
- Leaves turns you stopped yourself alone.
- Types only after 3 minutes without keyboard or mouse input, through UI Automation and MSAA, and never over a draft.
- `-SelfTest`, `-Inspect`, `-DryRun`, `-Only`, `-OnlyNew`.
