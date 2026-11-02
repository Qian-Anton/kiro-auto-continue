# Unit tests for src/Remote.ps1 and the phone's "continue" in src/Policy.ps1: commands, frames, the status words.
# Connects to nothing.   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Remote.Tests.ps1
# ASCII only: the Chinese comes from config.default.json or from code points.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
foreach ($part in 'Config', 'Records', 'Policy', 'Remote') { . (Join-Path $repo "src\$part.ps1") }
$cfg = Read-KacJson (Join-Path $repo 'config.default.json')
$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host ('FAIL ' + $what) } }
function U([int[]]$cp) { return -join ($cp | ForEach-Object { [char]$_ }) }
function Cmd([string]$t) { return (ConvertTo-KacRemoteCommand $t $cfg) }
$zc = $cfg.words.zh.remote.cmd
$ST = [string]@($zc.status)[0]; $HE = [string]@($zc.help)[0]; $SE = [string]@($zc.send)[0]; $PA = [string]@($zc.pair)[0]
$HAO = [string]@($zc.number)[0]
$CO = [string]($zc.continue | Sort-Object Length | Select-Object -First 1)        # the short one
$COL = [string]($zc.continue | Sort-Object Length -Descending | Select-Object -First 1)
$WORK = U 0x5199, 0x5B8C, 0x6D4B, 0x8BD5
$QF = U 0xFF1F

# Commands, Chinese and English, with the forms a phone keyboard gives.
$c = Cmd $ST; Check 'status' ($c.Kind -eq 'status')
$c = Cmd ($ST + $QF); Check 'status with a full-width question mark' ($c.Kind -eq 'status')
$c = Cmd 'STATUS'; Check 'status in English, any case' ($c.Kind -eq 'status')
$c = Cmd '?'; Check 'a question mark alone' ($c.Kind -eq 'status')
$c = Cmd $HE; Check 'help' ($c.Kind -eq 'help')
$c = Cmd ($CO + ' 2'); Check 'continue 2' ($c.Kind -eq 'continue' -and $c.N -eq 2)
$c = Cmd ($CO + '2' + $HAO); Check 'continue with the number word, no space' ($c.Kind -eq 'continue' -and $c.N -eq 2)
$c = Cmd ($COL + ' 12'); Check 'the long word for continue' ($c.Kind -eq 'continue' -and $c.N -eq 12)
$c = Cmd $COL; Check 'continue without a number' ($c.Kind -eq 'continue' -and $c.N -eq 0)
$c = Cmd 'c 3'; Check 'c 3' ($c.Kind -eq 'continue' -and $c.N -eq 3)
$c = Cmd 'continue 4'; Check 'continue 4' ($c.Kind -eq 'continue' -and $c.N -eq 4)
$c = Cmd ('2 ' + $WORK); Check ('2 and a task: ' + $c.Kind + ' ' + $c.N + ' [' + $c.Text + ']') ($c.Kind -eq 'send' -and $c.N -eq 2 -and $c.Text -eq $WORK)
$c = Cmd ('2' + $HAO + $WORK); Check 'number word, then the text without a space' ($c.Kind -eq 'send' -and $c.N -eq 2 -and $c.Text -eq $WORK)
$c = Cmd ('2' + $HAO + ' ' + $WORK); Check 'number word and a space' ($c.Kind -eq 'send' -and $c.N -eq 2 -and $c.Text -eq $WORK)
$c = Cmd ('3' + (U 0xFF1A) + $WORK); Check 'a full-width colon' ($c.Kind -eq 'send' -and $c.N -eq 3 -and $c.Text -eq $WORK)
$c = Cmd ($SE + ' 5 ' + $WORK); Check 'the send word first' ($c.Kind -eq 'send' -and $c.N -eq 5 -and $c.Text -eq $WORK)
$c = Cmd 'send 6 run the tests'; Check 'send 6 in English' ($c.Kind -eq 'send' -and $c.N -eq 6 -and $c.Text -eq 'run the tests')
$c = Cmd '12: fix it'; Check '12: fix it' ($c.Kind -eq 'send' -and $c.N -eq 12 -and $c.Text -eq 'fix it')
$c = Cmd ('2' + $WORK); Check 'a digit glued to text is not a command' ($c.Kind -eq 'unknown')
$c = Cmd '23'; Check 'a number alone is not a command' ($c.Kind -eq 'unknown')
$c = Cmd ($PA + ' 123456'); Check 'pair' ($c.Kind -eq 'pair' -and $c.Code -eq '123456')
$c = Cmd 'pair 0042'; Check 'pair in English keeps leading zeros' ($c.Kind -eq 'pair' -and $c.Code -eq '0042')
$c = Cmd 'hello'; Check 'anything else: unknown' ($c.Kind -eq 'unknown')
$c = Cmd ''; Check 'empty: unknown' ($c.Kind -eq 'unknown')

# What arrives: text with an @mention in front, full-width digits, a voice transcript, an image.
function Body([string]$json) { return (ConvertFrom-Json $json) }
$b = Body ('{"msgtype":"text","text":{"content":"@kiro ' + $ST + '"}}')
Check 'the @mention is dropped' ((Get-KacRemoteText $b) -eq $ST)
$b = Body ('{"msgtype":"text","text":{"content":"' + (U 0xFF12) + ' ' + $WORK + '"}}')
$t = Get-KacRemoteText $b; Check ('full-width digit made plain: ' + $t) ($t -eq ('2 ' + $WORK) -and (Cmd $t).Kind -eq 'send')
$b = Body ('{"msgtype":"voice","voice":{"content":"' + $ST + '"}}')
Check 'a voice message: its transcript' ((Get-KacRemoteText $b) -eq $ST)
$b = Body '{"msgtype":"image","image":{"url":"x"}}'
Check 'an image: no text' ((Get-KacRemoteText $b) -eq '')

# Frames.
$j = New-KacReplyFrame 'req1' 'stream_1' ("line 1`n" + $WORK) $true | ConvertFrom-Json
Check 'reply frame: respond to the callback, a finished stream' ($j.cmd -eq 'aibot_respond_msg' -and $j.headers.req_id -eq 'req1' -and $j.body.msgtype -eq 'stream' -and $j.body.stream.id -eq 'stream_1' -and $j.body.stream.finish -eq $true)
Check 'reply frame: text, line break and Chinese kept' ($j.body.stream.content -eq ("line 1`n" + $WORK))
$j = New-KacReplyFrame 'req1' 'stream_1' 'wait' $false | ConvertFrom-Json
Check 'reply frame: an unfinished stream' ($j.body.stream.finish -eq $false)
Check 'req ids: prefix, milliseconds, 8 hex digits' ((New-KacReqId 'ping') -match '^ping_\d{13}_[0-9a-f]{8}$')

# How a turn stands.
$rw = $cfg.words.zh.remote
function T($ended, [string]$stop, $cancelled, $restart, [string]$at) { return [pscustomobject]@{ Ended = $ended; Stop = $stop; Cancelled = $cancelled; Restart = $restart; EndedAt = $at } }
$now = [DateTime]'2026-11-02T16:00:00'
$today = ([DateTime]'2026-11-02T14:05:00').ToUniversalTime().ToString('o')
$yesterday = ([DateTime]'2026-11-01T23:10:00').ToUniversalTime().ToString('o')
Check 'running' ((Get-KacTurnState (T $false '' $false $false '') $rw $now) -eq [string]$rw.state.running)
$s = Get-KacTurnState (T $true 'end_turn' $false $false $today) $rw $now
Check ('finished today: ' + $s) ($s -eq (Format-KacText $rw.state.done @{ at = '14:05' }))
$s = Get-KacTurnState (T $true 'error' $false $false $yesterday) $rw $now
Check ('cut off yesterday: ' + $s) ($s -eq (Format-KacText $rw.state.cut @{ at = '11-01 23:10' }))
$s = Get-KacTurnState (T $true 'cancelled' $true $false $today) $rw $now
Check 'stopped by you' ($s -eq (Format-KacText $rw.state.stopped @{ at = '14:05' }))
$s = Get-KacTurnState (T $true 'error' $false $true $today) $rw $now
Check 'cut short by a restart' ($s -eq [string]$rw.state.restart)
$s = Format-KacText $rw.line @{ n = 3; name = 'ws | t'; state = 'x' }
Check ('a status line: ' + $s) ($s.StartsWith('3') -and $s.Contains('ws | t') -and $s.EndsWith('x'))

# "continue N" from the phone.
$TASK = (U 0x5199, 0x4E00, 0x4E2A, 0x5C0F, 0x6E38, 0x620F) + ' A'
function Turn($users, $ended, $credits, $cancelled, $restart) { return [pscustomobject]@{ Users = @($users); Ended = $ended; Credits = $credits; Cancelled = $cancelled; Restart = $restart } }
$x = Get-KacManualContinue (Turn @($TASK) $true $false $false $false) $cfg
Check 'after a cut-off: the continue message quoting the task' ($x.StartsWith(@($cfg.words.zh.resume)[0]) -and $x.Contains($TASK))
$x = Get-KacManualContinue (Turn @($TASK) $true $false $false $true) $cfg
Check 'after a restart: the restart message' ($x.StartsWith(@($cfg.words.zh.restart)[0]) -and $x.Contains($TASK))
$x = Get-KacManualContinue (Turn @($TASK) $true $true $false $false) $cfg
Check 'after a normal finish: a plain continue' ($x -eq [string]$cfg.words.zh.remote.continueText)
$x = Get-KacManualContinue (Turn @($TASK) $true $true $true $false) $cfg
Check 'after your own stop: a plain continue' ($x -eq [string]$cfg.words.zh.remote.continueText)
Check 'while it runs: nothing' ($null -eq (Get-KacManualContinue (Turn @($TASK) $false $false $false $false) $cfg))
$x = Get-KacManualContinue (Turn @('please refactor the parser') $true $true $false $false) $cfg
Check 'an English task: an English continue' ($x -eq [string]$cfg.words.en.remote.continueText)
Check 'a plain continue is not quoted as a task later' (Test-KacBare ([string]$cfg.words.zh.remote.continueText) $cfg)

# Stop, clear, and new sessions.
$STP = [string]($zc.stop | Sort-Object Length | Select-Object -First 1)
$CLR = [string]($zc.clear | Sort-Object Length | Select-Object -First 1)
$NEW = [string]($zc.new | Sort-Object Length | Select-Object -First 1)
$c = Cmd ($STP + ' 2'); Check 'stop 2' ($c.Kind -eq 'stop' -and $c.N -eq 2)
$c = Cmd ($STP + '3' + $HAO); Check 'stop with the number word' ($c.Kind -eq 'stop' -and $c.N -eq 3)
$c = Cmd 'stop 4'; Check 'stop 4 in English' ($c.Kind -eq 'stop' -and $c.N -eq 4)
$c = Cmd ($CLR + ' 2'); Check 'clear 2' ($c.Kind -eq 'clear' -and $c.N -eq 2)
$c = Cmd ($NEW + ' my_project ' + $WORK); Check 'new, with a workspace' ($c.Kind -eq 'new' -and $c.Text -eq ('my_project ' + $WORK))
$c = Cmd ($NEW + (U 0xFF1A) + $WORK); Check 'new, after a full-width colon' ($c.Kind -eq 'new' -and $c.Text -eq $WORK)
$c = Cmd ($NEW + $WORK); Check 'the word glued to text is not a new session' ($c.Kind -eq 'unknown')
$c = Cmd 'new proj fix the build'; Check 'new in English' ($c.Kind -eq 'new' -and $c.Text -eq 'proj fix the build')
$ws2 = @('my_project', 'website')
$s = Split-KacNewCommand ('my_project ' + $WORK) $ws2; Check 'workspace by name' ($s.Workspace -eq 'my_project' -and $s.Text -eq $WORK)
$s = Split-KacNewCommand ('web ' + $WORK) $ws2; Check 'workspace by the start of its name' ($s.Workspace -eq 'website' -and $s.Text -eq $WORK)
$s = Split-KacNewCommand 'MY_PROJECT fix it' $ws2; Check 'workspace in any case' ($s.Workspace -eq 'my_project' -and $s.Text -eq 'fix it')
$s = Split-KacNewCommand $WORK @('my_project'); Check 'one window: the workspace may be left out' ($s.Workspace -eq 'my_project' -and $s.Text -eq $WORK)
$s = Split-KacNewCommand $WORK $ws2; Check 'two windows and no name: cannot tell' ($s.Workspace -eq '')
$s = Split-KacNewCommand 'my_project' $ws2; Check 'a name and no text' ($s.Workspace -eq 'my_project' -and $s.Text -eq '')

# Mixed messages, and a message the bot sends on its own.
$b = Body ('{"msgtype":"mixed","mixed":{"msg_item":[{"msgtype":"text","text":{"content":"' + $ST + '"}},{"msgtype":"image","image":{"url":"x"}}]}}')
Check 'a mixed message: its text' ((Get-KacRemoteText $b) -eq $ST)
$j = New-KacPushFrame 'zhangsan' ("a`n" + $WORK) | ConvertFrom-Json
Check 'push frame: to the user, as markdown' ($j.cmd -eq 'aibot_send_msg' -and $j.headers.req_id -like 'aibot_send_msg_*' -and $j.body.chatid -eq 'zhangsan' -and $j.body.msgtype -eq 'markdown' -and $j.body.markdown.content -eq ("a`n" + $WORK))

# Every word the watcher uses, in both languages.
$keys = 'help', 'numbered', 'line', 'hint', 'none', 'pending', 'untilDone', 'needFormat', 'continueText', 'noNumber', 'noSuch', 'working', 'sending', 'sent', 'notSent', 'notAllowed', 'pairHint', 'paired', 'pairWrong', 'pairClosed',
  'textOnly', 'queuedAnn', 'queued', 'queueSent', 'cleared', 'nothingQueued', 'stopping', 'stopped', 'notRunning', 'notStopped', 'newOpening', 'newOpened', 'newOpenedNoNumber', 'newWhich', 'newNoText', 'notOpened', 'countAnn'
foreach ($lang in 'zh', 'en') {
  $r = $cfg.words.$lang.remote
  $missing = @($keys | Where-Object { -not $r.$_ })
  foreach ($k in 'running', 'done', 'cut', 'stopped', 'restart') { if (-not $r.state.$k) { $missing += 'state.' + $k } }
  foreach ($k in 'outOfCredits', 'gaveUp', 'cannotSend', 'tooMany', 'stuck', 'idle', 'maxPushes', 'done') { if (-not $r.need.$k) { $missing += 'need.' + $k } }
  foreach ($k in 'busy', 'noTab', 'twoTabs', 'notShown', 'draft', 'working', 'noCancel', 'noWindow', 'noButton', 'newTab', 'notReady') { if (-not $r.why.$k) { $missing += 'why.' + $k } }
  foreach ($k in 'status', 'help', 'continue', 'stop', 'clear', 'new', 'send', 'pair') { if (@($r.cmd.$k).Count -eq 0) { $missing += 'cmd.' + $k } }
  foreach ($k in 'title', 'summary') { if (-not $r.report.$k) { $missing += 'report.' + $k } }
  Check ('remote words ' + $lang + ' complete: ' + ($missing -join ', ')) ($missing.Count -eq 0)
}

"{0} passed, {1} failed" -f $script:pass, $script:fail
exit $script:fail
