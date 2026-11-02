@echo off
rem kiro-auto-continue: double-click to start, close this window to stop.
rem The log is logs\kiro-auto-continue.log next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0kiro-auto-continue.ps1" %*
pause
