# Changelog

## v0.3 (2026-11-05)

A program with a window instead of a PowerShell script.

- One file, `KiroAutoContinue.exe`: download it and double-click it. It runs on the .NET Framework 4.8 that Windows 10 and 11 come with, so there is nothing to install.
- The window lists your recent sessions and what each one is doing, with Continue and Stop buttons, and what it did lately in plain words. Closing the window keeps it running in the tray. It can start with Windows.
- Notices and the phone bot are set up in the window. Once you have paired with the bot, notices come from it in your chat with it; a WeCom group webhook still works without a bot. Feishu is no longer supported.
- The credits as Kiro last reported them: in a box of their own on the Sessions page, one bar for this month's and one for the trial credits, with when Kiro checked and how long ago that was. The phone's `status` and the morning summary carry the same numbers in one line.
- Settings, the log and the pairing are kept in `%APPDATA%\KiroAutoContinue`.
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
