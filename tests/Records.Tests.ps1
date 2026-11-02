# Unit tests for src/Records.ps1: made-up messages.jsonl lines in, turns out. Kiro is not needed.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Records.Tests.ps1
# ASCII only.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'src\Records.ps1')
$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host ('FAIL ' + $what) } }
function L([string]$type, [string]$rest) { return '{"id":"x","timestamp":"t","payload":{"type":"' + $type + '"' + $rest + '}}' }
$u1 = L 'user' ',"content":"first task\nline two","documents":[],"images":[]'
$start = L 'turn_start' ',"executionId":"a"'
$think = L 'assistant' ',"content":"thinking it over","operationType":"Think"'
$tool = L 'tool_call' ',"name":"readFile"'
$say1 = L 'assistant' ',"content":"before the tool","operationType":"Say"'
$say2 = L 'assistant' ',"content":"all done\n\u3010x\u3011","operationType":"Say"'
$usage = L 'usage_summary' ',"promptTurnSummaries":[{"unit":"credit","usage":0.5},{"unit":"credit","usage":0.25}],"elapsedTime":8286,"status":"success"'
$end = L 'turn_end' ',"stopReason":"end_turn"'

$t = Get-KacRecordTurn @($u1, $start, $say1, $think, $tool, $say2, $usage, $end)
Check 'normal turn: ended, credits, not cancelled' ($t.Ended -and $t.Credits -and -not $t.Cancelled)
Check 'final is what it said after the last tool call' ($t.Final -eq ("all done`n" + [char]0x3010 + 'x' + [char]0x3011))
Check 'one tool call' ($t.ToolCalls -eq 1)
Check 'user message with its line break' ($t.Users.Count -eq 1 -and $t.Users[0] -eq "first task`nline two")
Check ('footer: ' + $t.Footer) ($t.Footer -like 'end_turn, success, 0*75 credits, 8s')

$failed = L 'usage_summary' ',"promptTurnSummaries":[],"elapsedTime":4123,"status":"failed"'
$t = Get-KacRecordTurn @($u1, $start, $tool, $failed, (L 'turn_end' ',"stopReason":"error"'))
Check 'error turn: ended, no credits' ($t.Ended -and -not $t.Credits -and -not $t.Cancelled -and $t.Stop -eq 'error')
$aborted = L 'usage_summary' ',"promptTurnSummaries":[],"elapsedTime":6240,"status":"aborted"'
$t = Get-KacRecordTurn @($u1, $start, $aborted, (L 'turn_end' ',"stopReason":"cancelled"'))
Check 'cancelled turn: ended, stopped by you' ($t.Ended -and $t.Cancelled)
$t = Get-KacRecordTurn @($u1, $start, $tool)
Check 'running turn: not ended' (-not $t.Ended)
$u2 = L 'user' (',"content":"second","images":["' + ('A' * 300000) + '"]')
$t = Get-KacRecordTurn @($u1, $start, $say1, $usage, $end, $u2, $start, $say2, $usage, $end)
Check 'two turns: users newest first, the huge line read' ($t.Users.Count -eq 2 -and $t.Users[0] -eq 'second' -and $t.Users[1] -like 'first task*')
Check 'two turns: the last turn only' ($t.ToolCalls -eq 0 -and $t.Final -like 'all done*')
$synth = L 'user' ',"content":"Your previous response used the entire output budget on internal reasoning.","_meta":{"kiro":{"syntheticUserMessageReason":"max-tokens-recovery"}}'
$t = Get-KacRecordTurn @($u1, $start, $say1, $synth, $tool, $usage, $end)
Check 'a user line Kiro wrote itself is not yours' ($t.Users.Count -eq 1 -and $t.Users[0] -like 'first task*')
# A Kiro restart (11-02, after the credits ran out): Kiro came back at 18:56:11 UTC (02:56:11 local, UTC+8).
function At([string]$line, [string]$when) { return $line.Replace('"timestamp":"t"', ('"timestamp":"' + $when + '"')) }
function Utc([string]$s) { return [DateTime]::SpecifyKind([DateTime]$s, [DateTimeKind]::Utc) }
$up = Utc '2026-11-01T18:56:11'
# The turn_end Kiro writes for a lost turn, when that session is next used: "recovered" and "cancelled".
$lost = '{"id":"s-e-turn-end-synthetic","timestamp":"2026-11-01T18:57:41.550Z","payload":{"type":"turn_end","boundarySource":"recovered","stopReason":"cancelled","executionId":"e"}}'
$t = Get-KacRecordTurn @($u1, $start, $tool, $lost)
Check 'recovered: ended, not stopped by you, no credits, marked' ($t.Ended -and -not $t.Cancelled -and -not $t.Credits -and $t.Recovered -and $t.Restart)
Check ('recovered: footer ' + $t.Footer) ($t.Footer -eq 'cancelled, recovered')
Check 'recovered: counts whatever Kiro does' ((Set-KacRestartCut $t 10 $null) -and (Set-KacRestartCut $t 10 $up))
Check 'turn end time read' ($t.EndedAt -eq '2026-11-01T18:57:41.550Z')
$when = ConvertFrom-KacTime $t.EndedAt
Check 'time parsed as UTC' ($when -and $when.Kind -eq [DateTimeKind]::Utc -and $when.Hour -eq 18 -and $when.Minute -eq 57)
# An error shortly before Kiro restarted counts; one long before, or after Kiro started, does not.
$cut = @($u1, (At $start '2026-11-01T18:41:07.752Z'), (At $tool '2026-11-01T18:50:00.000Z'), (At (L 'turn_end' ',"stopReason":"error"') '2026-11-01T18:54:33.268Z'))
$t = Get-KacRecordTurn $cut
Check 'error, Kiro up 98 s later: cut by the restart, marked' ((Set-KacRestartCut $t 10 $up) -and $t.Restart -and $t.Footer -eq 'error')
Check 'error, Kiro started 2 h later: not the restart' (-not (Set-KacRestartCut (Get-KacRecordTurn $cut) 10 (Utc '2026-11-01T20:56:11')))
Check 'error, Kiro started before it: not the restart' (-not (Set-KacRestartCut (Get-KacRecordTurn $cut) 10 (Utc '2026-11-01T18:00:00')))
Check 'error, Kiro not running: not the restart' (-not (Set-KacRestartCut (Get-KacRecordTurn $cut) 10 $null))
Check 'error, Kiro start unreadable: not the restart' (-not (Set-KacRestartCut (Get-KacRecordTurn $cut) 10 ([DateTime]::MinValue)))
Check 'error, Kiro start in local time: the same' (Set-KacRestartCut (Get-KacRecordTurn $cut) 10 $up.ToLocalTime())
# Left open by the restart (no turn_end): counts once Kiro started after its last line, and is then over.
$open = @($u1, (At $start '2026-11-01T18:41:07.752Z'), (At $say1 '2026-11-01T18:50:00.000Z'), (At $tool '2026-11-01T18:54:20.000Z'))
$t = Get-KacRecordTurn $open
Check 'open: not ended, last line time read' (-not $t.Ended -and $t.LastAt -eq '2026-11-01T18:54:20.000Z' -and $t.StartedAt -eq '2026-11-01T18:41:07.752Z')
Check 'open, Kiro started after its last line: cut by the restart' (Set-KacRestartCut $t 10 $up)
Check ('open: now ended, no credits, not stopped by you, footer ' + $t.Footer) ($t.Ended -and $t.Restart -and -not $t.Credits -and -not $t.Cancelled -and $t.Footer -eq 'no turn_end')
$t = Get-KacRecordTurn $open
Check 'open, Kiro started before its last line: still running' (-not (Set-KacRestartCut $t 10 (Utc '2026-11-01T18:54:00')) -and -not $t.Ended -and -not $t.Restart)
Check 'open, Kiro not running: not decided' (-not (Set-KacRestartCut (Get-KacRecordTurn $open) 10 $null))
Check 'open for hours (a long command, a sub-agent): still counts' (Set-KacRestartCut (Get-KacRecordTurn $open) 10 (Utc '2026-11-02T01:00:00'))
Check 'open with no turn at all: not decided' (-not (Set-KacRestartCut (Get-KacRecordTurn @($u1)) 10 $up))
# What the chat showed for a failed turn. Those 11-02 errors were the account running out of credits, which is
# why it was switched: such an error counts however long before the restart.
$shown = '{"id":"c","timestamp":"2026-11-01T18:54:48.655Z","payload":{"type":"session_metadata","key":"displayError","value":{"message":"You''ve reached your overage limit. (Request ID: d)","errorType":"OverageLimitReachedError","retryErrorType":"CLIENT_ERROR"},"executionId":"e"}}'
$errAt = At (L 'turn_end' ',"stopReason":"error"') '2026-11-01T18:54:48.656Z'
$limitCut = @($u1, (At $start '2026-11-01T18:41:07.752Z'), $shown, $errAt)
$t = Get-KacRecordTurn $limitCut
Check ('error shown: ' + $t.ErrorType + ' / ' + $t.ErrorText) ($t.ErrorType -eq 'OverageLimitReachedError' -and $t.ErrorText -eq 'You''ve reached your overage limit. (Request ID: d)')
Check ('error shown: footer ' + $t.Footer) ($t.Footer -eq 'error (OverageLimitReachedError)')
$limits = @('UsageLimitReachedError', 'OverageLimitReachedError')
Check 'out of credits, accounts switched 9 h later: continue it' (Set-KacRestartCut (Get-KacRecordTurn $limitCut) 10 (Utc '2026-11-02T03:56:11') $limits)
Check 'out of credits, no list given: only within the window' (-not (Set-KacRestartCut (Get-KacRecordTurn $limitCut) 10 (Utc '2026-11-02T03:56:11')))
Check 'out of credits, Kiro not restarted since: no' (-not (Set-KacRestartCut (Get-KacRecordTurn $limitCut) 10 (Utc '2026-11-01T18:00:00') $limits))
$dropped = @($u1, (At $start '2026-11-01T18:41:07.752Z'), $shown.Replace('OverageLimitReachedError', 'ConnectionResetError'), $errAt)
Check 'a dropped connection 9 h before a restart: no' (-not (Set-KacRestartCut (Get-KacRecordTurn $dropped) 10 (Utc '2026-11-02T03:56:11') $limits))
$k = Get-KacKiroStart
Check 'Kiro start: none, or a time' ($null -eq $k -or $k -is [DateTime])
$t = Get-KacRecordTurn @($u1, $start, $tool, $usage, $end)
Check 'a normal finish is never a restart' (-not (Set-KacRestartCut $t 10 $up) -and -not $t.Restart)
$quoted = L 'user' ',"content":"say \"hi\" and \\\"ok\\\"","documents":[]'
$t = Get-KacRecordTurn @($quoted, $start, $end)
Check ('escaped quotes: ' + $t.Users[0]) ($t.Users[0] -eq 'say "hi" and \"ok\"')

# Reading the file: from the start, from where the last read ended, and with a line still being written.
$tmp = [System.IO.Path]::GetTempFileName()
try {
  [System.IO.File]::WriteAllText($tmp, ($u1 + "`n" + $start + "`n" + $say1 + "`n"), (New-Object System.Text.UTF8Encoding $false))
  $r = Read-KacLines $tmp 0
  Check 'read three lines' ($r.Lines.Count -eq 3 -and $r.End -eq (Get-Item $tmp).Length)
  [System.IO.File]::AppendAllText($tmp, ($tool + "`n" + '{"id":"half'), (New-Object System.Text.UTF8Encoding $false))
  $r2 = Read-KacLines $tmp $r.End
  Check 'next read: the new full line, not the half one' ($r2.Lines.Count -eq 1 -and $r2.Lines[0] -eq $tool)
  $r3 = Read-KacLines $tmp $r2.End
  Check 'half a line waits for the rest' ($r3.Lines.Count -eq 0 -and $r3.End -eq $r2.End)
  $r4 = Read-KacLines $tmp 0 40
  Check 'a tail read drops the cut first line' ($r4.Lines.Count -le 1 -and -not ($r4.Lines -contains $u1))
} finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

"{0} passed, {1} failed" -f $script:pass, $script:fail
exit $script:fail
