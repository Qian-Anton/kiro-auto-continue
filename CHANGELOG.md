# Changelog

## Unreleased

- Fixed: after a few hours it could take several GB of memory and a third of a CPU core. Every few seconds it read the end of each session written in the last day again, 4 MB a session and up to 84 MB for a long one. It now keeps what it has read and reads only what was added since.
- Fixed: a session that renamed itself while its Kiro window was minimized could not be continued. A minimized window keeps showing the old title on the tab, so the tab was not found. It now has the window bring what it shows up to date before it reads it, which also lets "new session" from the phone work with the window minimized.
- Fixed: a draft in the chat box that only began like the text it was about to type (say, a draft starting with "continue" when it types that one word) was taken for that text and sent. Now a draft is sent only when it is that text in full.
- When it cannot find a session's tab, the log lists the tabs it saw. When the record says a turn ended but its tab still says Working, it looks again a minute later instead of dropping it without a word.

## v0.3 (2026-11-05)

A program with a window instead of a PowerShell script.

- One file, `KiroAutoContinue.exe`: download it and double-click it. It runs on the .NET Framework 4.8 that Windows 10 and 11 come with, so there is nothing to install.
- The window lists your recent sessions and what each one is doing, with Continue and Stop buttons, and what it did lately in plain words. Closing the window keeps it running in the tray. It can start with Windows.
- Notices and the phone bot are set up in the window. Once you have paired with the bot, notices come from it in your chat with it; a WeCom group webhook still works without a bot. Feishu is no longer supported.
- The credits as Kiro last reported them: in a box of their own on the Sessions page, one bar for this month's and one for the trial credits, with when Kiro checked and how long ago that was. The phone's `status` and the morning summary carry the same numbers in one line.
- Settings, the log and the pairing are kept in `%APPDATA%\KiroAutoContinue`.
- A turn that Kiro ends while the agent is still working is continued like a cut-off. Kiro records no error for it: the turn ends right after a tool result with no reply, or with `tool_use` or `max_tokens`, and the chat shows it like a finished turn, credits and all.
- When Kiro stops a turn to ask whether to keep trying after a tool kept failing, it presses Keep Iterating for you, up to three times a turn (`keepIterating.maxPerTurn`, 0 to turn it off). Any other question it leaves for you with a notice.
- The Chinese wording of the window, the phone replies and the notices was gone over.
- Fixed: a session cut off again right after it was continued could not be continued any more. Switching tabs just as it was about to type or press stop could make it act on your tab instead. A stop from the window or the phone could be reported as failed although the turn had ended.
- The PowerShell script is no longer in the repository. It stays available as v0.2.

## v0.2 (2026-11-02)

First public release.

- When a turn gets cut off, it asks the agent to continue, with your original request attached. Turns you stopped yourself are left alone.
- After Kiro restarts, it continues the sessions that were running.
- When the credits run out, it waits instead of retrying, and continues once they are back.
- Until-done mode: the agent keeps working instead of stopping to ask whether to go on.
- Notices in a WeCom or Feishu group when a session needs you, and a summary every morning.
- Commands from your phone through a WeCom bot: see what each session is doing, have one continue or stop, send it a message, or open a new session.
- Keeps the PC awake while a session is working. Works with the screen locked.
