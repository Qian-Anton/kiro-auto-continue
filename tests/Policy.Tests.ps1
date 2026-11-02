# Unit tests for src/Policy.ps1: made-up turns in, decisions out. Kiro is not needed.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Policy.Tests.ps1
# ASCII only: the Chinese text is built from code points.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'src\Config.ps1')
. (Join-Path $repo 'src\Policy.ps1')
$cfg = Read-KacJson (Join-Path $repo 'config.default.json')
function U([int[]]$cp) { return -join ($cp | ForEach-Object { [char]$_ }) }
$TRIG = $cfg.words.zh.marks.trigger; $DONE = $cfg.words.zh.marks.done; $STUCK = $cfg.words.zh.marks.stuck
$JIXU = U 0x7EE7, 0x7EED
$TASK = (U 0x5199, 0x4E00, 0x4E2A, 0x5C0F, 0x6E38, 0x620F) + ' A'
$AUTO_RESUME = [string]$cfg.autoPrefixes[0]
$AUTO_PUSH = [string]$cfg.autoPrefixes[1]
$G = $TRIG + $TASK
$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host ('FAIL ' + $what) } }
function Turn($users, [bool]$credits = $true, [string]$final = '', [int]$tools = 1, [bool]$cancelled = $false, [bool]$ended = $true) {
  return [pscustomobject]@{ Users = @($users); Ended = $ended; Credits = $credits; Cancelled = $cancelled; Footer = 'f'; Final = $final; ToolCalls = $tools; Items = 1 }
}
function NewState { return @{ Tries = 0; Mode = $null } }
function Apply($st, $d) { foreach ($k in $d.Set.Keys) { $st[$k] = $d.Set[$k] } }

# Plain sessions: finish, interruption, backoff, cap, nothing to quote, your own stop, no footer.
$st = NewState; $st.Tries = 2
$d = Get-KacDecision (Turn @($TASK)) $st $cfg
Check 'finish -> none' ($d.Action -eq 'none')
Check 'finish resets tries' ($d.Set.Tries -eq 0)
$st = NewState
$d = Get-KacDecision (Turn @($TASK) $false) $st $cfg
Check 'cut -> send resume' ($d.Action -eq 'send' -and $d.Kind -eq 'resume')
Check 'resume quotes the task' ($d.Text.Contains($TASK))
Check 'Chinese task -> Chinese resume' ($d.Text.StartsWith($AUTO_RESUME))
Check 'first resume after 60 s' ($d.After -eq 60)
Apply $st $d
Check 'tries 1' ($st.Tries -eq 1)
$d = Get-KacDecision (Turn @($d.Text, $TASK) $false) $st $cfg
Check 'second resume skips its own message and waits 300 s' ($d.Text.Contains($TASK) -and $d.After -eq 300)
$d = Get-KacDecision (Turn @($TASK) $false) @{ Tries = 3; Mode = $null } $cfg
Check 'after three continues: stop' ($d.Action -eq 'stop')
$d = Get-KacDecision (Turn @(($JIXU + (U 0x3002)), ($AUTO_RESUME + 'x')) $false) (NewState) $cfg
Check 'nothing to quote -> resumeNoTask' ($d.Action -eq 'send' -and $d.Text -eq (Format-KacText $cfg.words.zh.resumeNoTask @{}))
# A line of only = or - under text makes Kiro's chat render that text as a heading (11-02): no such lines.
$all = @(); foreach ($p in $cfg.words.PSObject.Properties) { foreach ($q in $p.Value.PSObject.Properties) { if ($q.Name -ne 'marks') { $all += @($q.Value) } } }
Check 'no template line is only = or -' (@($all | Where-Object { ([string]$_).Trim() -match '^[=\-]{2,}$' }).Count -eq 0)
$st = @{ Tries = 1; Mode = @{ Source = 'a'; Goal = 'a'; Pushes = 3; Verifying = $false; Idle = 0 } }
$d = Get-KacDecision (Turn @($TASK) $true '' 1 $true) $st $cfg
Check 'your stop -> none' ($d.Action -eq 'none')
Check 'your stop ends until-done' ($d.Set.ContainsKey('Mode') -and $null -eq $d.Set.Mode)
# Cut short by a Kiro restart: the restart message, with the same backoff as any cut-off.
$tr = Turn @($TASK) $false; $tr | Add-Member -NotePropertyName Restart -NotePropertyValue $true
$d = Get-KacDecision $tr (NewState) $cfg
Check 'restart -> restart message quoting the task' ($d.Kind -eq 'resume' -and $d.Text.StartsWith(@($cfg.words.zh.restart)[0]) -and $d.Text.Contains($TASK) -and $d.After -eq 60)
$tr = Turn @(($AUTO_RESUME + 'x')) $false; $tr | Add-Member -NotePropertyName Restart -NotePropertyValue $true
$d = Get-KacDecision $tr (NewState) $cfg
Check 'restart, nothing to quote -> restartNoTask' ($d.Text -eq (Format-KacText $cfg.words.zh.restartNoTask @{}))
# Out of credits: no try spent; once Kiro restarts, or the credits are back, it is continued.
$tl = Turn @($TASK) $false; $tl | Add-Member -NotePropertyName ErrorType -NotePropertyValue 'OverageLimitReachedError'
$d = Get-KacDecision $tl (NewState) $cfg
Check 'out of credits -> stop, no try spent' ($d.Action -eq 'stop' -and $d.Why -like 'out of credits*' -and -not $d.Set.ContainsKey('Tries'))
$tl | Add-Member -NotePropertyName Restart -NotePropertyValue $true
$d = Get-KacDecision $tl (NewState) $cfg
Check 'out of credits, then Kiro restarted -> restart message' ($d.Kind -eq 'resume' -and $d.Text.StartsWith(@($cfg.words.zh.restart)[0]))
$tc = Turn @($TASK) $false; $tc | Add-Member -NotePropertyName ErrorType -NotePropertyValue 'ConnectionResetError'
$d = Get-KacDecision $tc (NewState) $cfg
Check 'a dropped connection -> resume as before' ($d.Kind -eq 'resume' -and $d.Text.StartsWith($AUTO_RESUME) -and -not $d.Text.StartsWith(@($cfg.words.zh.restart)[0]))
$tb = Turn @($TASK) $false; $tb | Add-Member -NotePropertyName ErrorType -NotePropertyValue 'OverageLimitReachedError'; $tb | Add-Member -NotePropertyName CreditsBack -NotePropertyValue $true
$d = Get-KacDecision $tb (NewState) $cfg
Check 'out of credits, then credits back -> the plain continue message' ($d.Kind -eq 'resume' -and $d.Text.StartsWith($AUTO_RESUME) -and $d.Text.Contains($TASK) -and -not $d.Text.StartsWith(@($cfg.words.zh.restart)[0]))
$cfgT = Read-KacJson (Join-Path $repo 'config.default.json'); $cfgT.testing.cancelIsInterrupt = $true
$d = Get-KacDecision (Turn @($TASK) $true '' 1 $true) (NewState) $cfgT
Check 'testing: your stop counts as a cut' ($d.Kind -eq 'resume')
$d = Get-KacDecision (Turn @($TASK) $false '' 1 $false $false) (NewState) $cfg
Check 'no footer -> wait' ($d.Action -eq 'wait')

# Until-done: contract, pushes, goal every fifth push, verify on done, then stop.
$st = NewState
$d = Get-KacDecision (Turn @($G) $true 'shall I go on?') $st $cfg
Check 'trigger + early stop -> contract' ($d.Kind -eq 'contract')
Check 'contract carries the goal without the mark' ($d.Text.Contains($TASK) -and -not $d.Text.Contains($TRIG))
Check 'contract names both marks' ($d.Text.Contains($DONE) -and $d.Text.Contains($STUCK))
Apply $st $d
Check 'pushes 1' ($st.Mode.Pushes -eq 1)
$users = @($d.Text, $G); $kinds = @()
for ($i = 0; $i -lt 5; $i++) {
  $d = Get-KacDecision (Turn $users $true 'report' 2) $st $cfg
  $kinds += $d.Kind; Apply $st $d; $users = @($d.Text) + $users
}
Check ('push kinds ' + ($kinds -join ',')) (($kinds -join ',') -eq 'push,push,push,push,pushGoal')
$d = Get-KacDecision (Turn $users $true ("all good`n" + $DONE) 3) $st $cfg
Check 'done -> verify' ($d.Kind -eq 'verify')
Apply $st $d; $users = @($d.Text) + $users
$d = Get-KacDecision (Turn $users $true ("checked`n" + $DONE) 1) $st $cfg
Check 'done again -> done' ($d.Action -eq 'done')
Apply $st $d
Check 'until-done off after done' ($null -eq $st.Mode)

# Until-done: stuck, idle pushes, the cap, a new message from you, a restart of this tool.
$d = Get-KacDecision (Turn @($G) $true ("need a key`n" + $STUCK + ' API key')) (NewState) $cfg
Check 'stuck -> stop with the reason' ($d.Action -eq 'stop' -and $d.Why.Contains('API key'))
$mk = { param($p, $idle) @{ Tries = 0; Mode = @{ Source = $G; Goal = $TASK; Pushes = $p; Verifying = $false; Idle = $idle } } }
$d = Get-KacDecision (Turn @(($AUTO_PUSH + 'x'), $G) $true 'ok' 0) (& $mk 4 2) $cfg
Check 'third push without work -> stop' ($d.Action -eq 'stop')
$d = Get-KacDecision (Turn @(($AUTO_PUSH + 'x'), $G) $true 'ok' 5) (& $mk 4 2) $cfg
Check 'a turn with work -> push, idle back to 0' ($d.Action -eq 'send' -and $d.Set.Mode.Idle -eq 0)
$d = Get-KacDecision (Turn @(($AUTO_PUSH + 'x'), $G) $true 'ok' 5) (& $mk 30 0) $cfg
Check '30 pushes -> stop' ($d.Action -eq 'stop')
$d = Get-KacDecision (Turn @('new instructions', ($AUTO_PUSH + 'x'), $G) $true 'ok' 2) (& $mk 3 0) $cfg
Check 'a new message from you ends until-done' ($d.Action -eq 'none' -and $d.Set.ContainsKey('Mode') -and $null -eq $d.Set.Mode)
$d = Get-KacDecision (Turn @(($AUTO_PUSH + 'b'), ($AUTO_PUSH + 'a'), $G) $true 'report' 2) (NewState) $cfg
Check 'after a restart: count earlier pushes, no second contract' ($d.Kind -eq 'push' -and $d.Set.Mode.Pushes -eq 3)

# The code each stop and done carries (the notice's headline), and the words for every notice in both languages.
$codes = @{}
$codes['gaveUp'] = (Get-KacDecision (Turn @($TASK) $false) @{ Tries = 3; Mode = $null } $cfg).Code
$t9 = Turn @($TASK) $false; $t9 | Add-Member -NotePropertyName ErrorType -NotePropertyValue 'UsageLimitReachedError'
$codes['outOfCredits'] = (Get-KacDecision $t9 (NewState) $cfg).Code
$codes['stuck'] = (Get-KacDecision (Turn @($G) $true ("x`n" + $STUCK + ' y')) (NewState) $cfg).Code
$codes['idle'] = (Get-KacDecision (Turn @(($AUTO_PUSH + 'x'), $G) $true 'ok' 0) (& $mk 4 2) $cfg).Code
$codes['maxPushes'] = (Get-KacDecision (Turn @(($AUTO_PUSH + 'x'), $G) $true 'ok' 5) (& $mk 30 0) $cfg).Code
$vs = @{ Tries = 0; Mode = @{ Source = $G; Goal = $TASK; Pushes = 2; Verifying = $true; Idle = 0 } }
$codes['done'] = (Get-KacDecision (Turn @(($AUTO_PUSH + 'v'), $G) $true ("checked`n" + $DONE) 1) $vs $cfg).Code
foreach ($k in @($codes.Keys)) { Check ('code ' + $k + ': ' + $codes[$k]) ($codes[$k] -eq $k) }
foreach ($lang in 'zh', 'en') {
  foreach ($k in @($codes.Keys) + @('cannotSend', 'tooMany', 'test', 'prefix')) { Check ('notice words ' + $lang + '.' + $k) ([bool]$cfg.words.$lang.notice.$k) }
}

# Language, marks only on the last lines, bare continues.
$d = Get-KacDecision (Turn @('please refactor the parser') $false) (NewState) $cfg
Check 'English task -> English resume' ($d.Text.StartsWith('(auto-continue)'))
$d = Get-KacDecision (Turn @('[until-done] refactor the parser') $true 'shall I?') (NewState) $cfg
Check 'English trigger -> English contract' ($d.Kind -eq 'contract' -and $d.Text.Contains('[DONE]') -and $d.Text.Contains('Goal: refactor the parser'))
$d = Get-KacDecision (Turn @($G) $true ('I will write ' + $DONE + " at the end`nline 2`nline 3") 2) (NewState) $cfg
Check 'a mark above the last two lines does not count' ($d.Kind -eq 'contract')
Check 'bare continue with punctuation' (Test-KacBare ($JIXU + (U 0x3002)) $cfg)
Check 'a real message is not bare' (-not (Test-KacBare $TASK $cfg))
Check 'until-done off in config -> none' ((Get-KacDecision (Turn @($G) $true 'shall I?') (NewState) ((Read-KacJson (Join-Path $repo 'config.default.json')) | ForEach-Object { $_.untilDone.enabled = $false; $_ })).Action -eq 'none')

"{0} passed, {1} failed" -f $script:pass, $script:fail
exit $script:fail
