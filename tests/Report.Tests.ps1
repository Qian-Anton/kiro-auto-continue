# Unit tests for src/Report.ps1: counting what the watcher did from its log, and when the morning status is due.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Report.Tests.ps1
# ASCII only.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'src\Report.ps1')
$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host ('FAIL ' + $what) } }

$now = [DateTime]'2026-11-03T08:00:00'
$log = @(
  '11-02 19:00:00  ws | old : interrupted [error]; sent resume',
  '11-02 21:10:05  ws | a : interrupted [error, failed]; sent resume',
  '11-02 21:40:00  ws | a : cut short by a Kiro restart [no turn_end]; sent resume (screen locked)',
  '11-02 22:00:00  ws | b : until-done: stopped without a mark, push 2/30; sent push',
  '11-02 22:30:00  ws | b : a message from your phone was waiting [end_turn, success]; sent queued',
  '11-03 01:00:00  ws | c : NEEDS YOU - out of credits [error (OverageLimitReachedError)]; it continues once the credits are back or Kiro restarts',
  '11-03 02:00:00  ws | b : DONE - until-done: done after 3 pushes',
  '11-03 03:00:00  ws | a : sent from your phone: [x]',
  '11-03 03:00:01  remote: zhangsan sent status',
  'not a log line'
)
$c = Get-KacLogCounts $log $now.AddHours(-12) $now
Check ('continues in the window: ' + $c.Resumes['ws | a']) ($c.Resumes['ws | a'] -eq 2 -and -not $c.Resumes.ContainsKey('ws | old'))
Check 'pushes: until-done and waiting messages' ($c.Pushes['ws | b'] -eq 2)
Check 'needs you' ($c.Needs.ContainsKey('ws | c') -and $c.Needs.Count -eq 1)
Check 'until-done finished' ($c.Done.ContainsKey('ws | b'))
Check 'totals' ((Get-KacTotal $c.Resumes) -eq 2 -and (Get-KacTotal $c.Pushes) -eq 2)
$c = Get-KacLogCounts @('12-31 23:30:00  ws | a : x; sent resume') ([DateTime]'2026-12-31T20:00:00') ([DateTime]'2027-01-01T08:00:00')
Check 'a line from the last day of the year before' ($c.Resumes['ws | a'] -eq 1)
$c = Get-KacLogCounts @() $now.AddHours(-12) $now
Check 'no lines: nothing' ((Get-KacTotal $c.Resumes) -eq 0 -and $c.Needs.Count -eq 0)

Check '08:00 today' ((Get-KacReportDue '08:00' $now) -eq [DateTime]'2026-11-03T08:00:00')
Check '7:30' ((Get-KacReportDue '7:30' $now) -eq [DateTime]'2026-11-03T07:30:00')
Check 'empty: off' ($null -eq (Get-KacReportDue '' $now))
Check 'not a time: off' ($null -eq (Get-KacReportDue '25:00' $now) -and $null -eq (Get-KacReportDue 'eight' $now))

"{0} passed, {1} failed" -f $script:pass, $script:fail
exit $script:fail
