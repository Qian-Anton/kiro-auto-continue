# kiro-auto-continue: keeps Kiro IDE chat sessions going while you are away (Windows).
#   * A turn that was cut off (Kiro's record ends it with stopReason "error"; its chat footer shows the elapsed
#     time but no "Est. Credits Used") gets a continue message that quotes your last task, so the agent can
#     pick up even if it never saw that task.
#   * Optional until-done mode: put the trigger mark in your message ([until-done], or the Chinese mark in
#     config.default.json) and it pushes the agent on when it stops early, until it writes the done mark or the
#     stuck mark.
# It reads Kiro's session records (%USERPROFILE%\.kiro\sessions) and touches the chat panel only to type.
# Start: double-click start.cmd. Stop: close its window. Log: logs\kiro-auto-continue.log. See README.md.
# ASCII only: Windows PowerShell 5.1 reads BOM-less scripts in the system code page.
param(
  [string]$Config = '',  # another settings file, on top of config.default.json and config.json
  [string]$Only = '',    # watch only sessions whose title contains this (for trying it out)
  [switch]$OnlyNew,      # watch only sessions created after it started (for trying it out)
  [switch]$DryRun,       # decide and log, but never type
  [switch]$Inspect,      # print the recent sessions and what it would do with each, then exit
  [switch]$SelfTest,     # open a new session, send a short prompt, check the reply, close it, then exit
  [switch]$TestNotify,   # send a test notice to notify.webhook (your phone), then exit
  [switch]$TestRemote,   # connect to WeCom as the bot in remote.botId / remote.secret, check a heartbeat, then exit
  [switch]$Report        # make the morning status now, print it, send it the way notices go, then exit
)
$ErrorActionPreference = 'Stop'
$KacHome = $PSScriptRoot
foreach ($part in 'Native', 'KiroUi', 'Records', 'Policy', 'Config', 'Notify', 'Remote', 'Report') { . (Join-Path $KacHome "src\$part.ps1") }
$cfg = Read-KacConfig $KacHome $Config
$KacLogDir = Join-Path $KacHome 'logs'
if (-not (Test-Path $KacLogDir)) { New-Item -ItemType Directory -Path $KacLogDir | Out-Null }
$KacLogFile = Join-Path $KacLogDir 'kiro-auto-continue.log'
function Write-KacLog([string]$s) {
  $line = (Get-Date -Format 'MM-dd HH:mm:ss') + '  ' + $s
  Write-Host $line
  try { Add-Content -Path $KacLogFile -Value $line -Encoding UTF8 } catch {}
}
function Get-KacIf($cond, [string]$yes, [string]$no = '') { if ($cond) { return $yes }; return $no }
# A "<session> : <tag> - <detail>" line for the log that, when notify.webhook is set and notify.events lists its
# kind, also goes to your phone. $code says what happened (words.<lang>.notice): "done" is an until-done finish
# (kind done), everything else needs you (kind needsYou).
# $id: the session's id. The status on your phone shows the code until the session runs again, and with commands
# from your phone on, the notice names the session by its number too ("continue 3").
function Write-KacNotice([string]$code, [string]$name, [string]$tag, [string]$detail, [string]$id = '') {
  Write-KacLog ('{0} : {1} - {2}' -f $name, $tag, $detail)
  if ($id) { $script:attention[$id] = $code }
  if ($id -and $code -eq 'outOfCredits') { $script:creditWait[$id] = Get-Date }
  $kind = 'needsYou'; if ($code -eq 'done') { $kind = 'done' }
  if (-not $cfg.notify.webhook -or @($cfg.notify.events) -notcontains $kind) { return }
  $shown = $name
  if ($id -and $script:remote) { $shown = (Format-KacText (Get-KacWords $cfg (Get-KacNoticeLang $cfg)).remote.numbered @{ n = (Get-KacNumber $id) }) + $name }
  $err = Send-KacNotice $cfg (Format-KacNotice $cfg $code $shown $detail)
  if ($err) { Write-KacLog ('notice not sent: ' + $err) }
}
function Read-KacTurn([string]$dir) { return (Read-KacSessionTurn $dir ([long]$cfg.recordTailBytes)) }
function Set-KacRestartCutNow($turn) { return (Set-KacRestartCut $turn ([double]$cfg.restartWindowMinutes) (Get-KacKiroStart) @($cfg.resume.limitErrors)) }

# -Inspect: read-only. The most recently active sessions, what this tool would do with each, and their tabs.
function Show-KacInspect {
  foreach ($f in @(Get-KacSessionFiles | Sort-Object Modified -Descending | Select-Object -First 8)) {
    $info = Get-KacSessionInfo $f.Dir
    if ($null -eq $info) { continue }
    $turn = Read-KacTurn $f.Dir
    [void](Set-KacRestartCutNow $turn)
    $users = @($turn.Users); $ti = Find-KacTaskIndex $users $cfg
    $task = 'none'; if ($ti -ge 0) { $task = Get-KacShort $users[$ti] 50 }
    '== {0} | {1}  [{2}; record written {3:MM-dd HH:mm}]' -f $info.Workspace, $info.Title, $info.Status, $f.Modified
    '   last turn: {0}; tool calls: {1}' -f (Get-KacIf $turn.Ended $turn.Footer 'still running'), $turn.ToolCalls
    '   task it would quote: [{0}]' -f $task
    if ($turn.Final) { '   answer ends: [{0}]' -f (Get-KacShort (Get-KacTail $turn.Final 2) 90) }
    $d = Get-KacDecision $turn @{ Tries = 0; Mode = $null } $cfg
    '   would: {0} {1} - {2}' -f $d.Action, $d.Kind, $d.Why
    $tab = Find-KacSessionTab $info
    $where = 'not open in any tab'
    if ($tab.Count -eq 1) { $where = '[{0}] in the {1} window' -f $tab.Hit.Tab.State, $tab.Hit.Win.Workspace }
    elseif ($tab.Count -gt 1) { $where = '{0} tabs share this title' -f $tab.Count }
    '   tab: ' + $where
  }
}
if ($Inspect) { Show-KacInspect; exit 0 }

# -SelfTest: checks that this machine's Kiro can be typed into and that its records can be read. Opens a new
# session, sends a two-line prompt, checks the reply in the record and in the panel, then closes that session
# (it stays in Kiro's session history).
function Invoke-KacSelfTest {
  $script:stPass = 0; $script:stFail = 0
  function Say([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:stPass++; $tag = 'PASS' } else { $script:stFail++; $tag = 'FAIL' }
    '{0}  {1}{2}' -f $tag, $what, (Get-KacIf $detail (' - ' + $detail))
  }
  Say ('found Kiro''s session records in ' + $KacSessionsRoot) ([System.IO.Directory]::Exists($KacSessionsRoot))
  $w = $null; $root = $null
  foreach ($x in Get-KacWindows) { $r = Get-KacChatRoot $x; if ($r) { $w = $x; $root = $r; break } }
  Say 'found a Kiro window with the chat panel open' ($null -ne $root) (Get-KacIf $w $w.Workspace)
  if ($null -eq $root) { return }
  'Waiting until the keyboard and mouse have been idle for 10 s (the chat panel will switch tabs)...'
  $t0 = Get-Date
  while ([KacNative]::IdleSeconds() -lt 10) {
    if (((Get-Date) - $t0).TotalMinutes -gt 5) { Say 'keyboard or mouse busy for 5 minutes' $false; return }
    Start-Sleep -Seconds 1
  }
  $back = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
  $before = @{}; foreach ($t in Get-KacTabs $root) { $before[$t.Id] = 1 }
  # One press only: with Kiro minimized, a new tab can take 10 s or more to appear in what UIA reads, and a
  # second press makes a second session (11-02). Kiro drops an empty session by itself once you leave it.
  $new = $null; $t1 = Get-Date
  if (New-KacSession $root) {
    for ($i = 0; $i -lt 60 -and $null -eq $new; $i++) {
      Start-Sleep -Milliseconds 500
      $new = Get-KacTabs $root | Where-Object { -not $before.ContainsKey($_.Id) } | Select-Object -First 1
    }
  }
  $shown = $false; if ($new) { $shown = Show-KacTab $root $new.Id }
  Say 'opened a new session and it is shown' $shown ('after {0:N1} s' -f ((Get-Date) - $t1).TotalSeconds)
  if (-not $shown) { if ($back) { [void](Show-KacTab $root $back.Id) }; return }
  # Wait until the panel holds the empty new session, not the one shown before: an empty session has no
  # message list at all ("none"), or one with no messages ("0|...").
  $ready = $false; $t1 = Get-Date
  for ($i = 0; $i -lt 60 -and -not $ready; $i++) { Start-Sleep -Milliseconds 500; $m = Get-KacViewMark $root; $ready = ($m -eq 'none' -or $m -like '0|*') }
  Say 'the panel now shows the empty new session' $ready ('after {0:N1} s' -f ((Get-Date) - $t1).TotalSeconds)
  if (-not $ready) { if ($back) { [void](Show-KacTab $root $back.Id) }; return }
  Start-Sleep -Seconds 1
  Invoke-KacSelfTestTurn $root $new $t0
  if ($back) { [void](Show-KacTab $root $back.Id) }
}
function Invoke-KacSelfTestTurn($root, $new, $t0) {
  $hit = $null
  try {
    $prompt = "kiro-auto-continue self-test. This is line 1.`nLine 2: reply with the single word OK and nothing else. Do not use any tools."
    $err = Send-KacMessage $root $new.Id $prompt $cfg.placeholders
    Say 'typed a two-line message, sent it, and the session started working' ($err -eq '') $err
    if ($err) { return }
    # Its record: a session folder made after the test began, whose last turn holds this prompt and ended.
    $rec = $null; $dir = $null
    for ($i = 0; $i -lt 90 -and $null -eq $rec; $i++) {
      Start-Sleep -Seconds 2
      foreach ($f in @(Get-KacSessionFiles | Where-Object { $_.Created -ge $t0.AddSeconds(-5) })) {
        $turn = Read-KacTurn $f.Dir
        if ($turn.Ended -and @($turn.Users).Count -gt 0 -and ([string]@($turn.Users)[0]) -match 'self-test') { $rec = $turn; $dir = $f.Dir; break }
      }
    }
    Say 'found its record, and the turn ended within 3 minutes' ($null -ne $rec)
    if ($null -eq $rec) { return }
    Say 'the record says it finished normally' ($rec.Stop -eq 'end_turn' -and $rec.Credits) $rec.Footer
    Say 'read the reply from the record' ($rec.Final -match 'OK') (Get-KacShort $rec.Final 40)
    $u = [string]@($rec.Users)[0]
    Say 'both lines arrived, with the line break' ($u -match 'line 1\.\s*\n\s*Line 2') (Get-KacShort $u 70)
    $info = Get-KacSessionInfo $dir
    $tab = $null
    for ($i = 0; $i -lt 30; $i++) { $tab = Find-KacSessionTab $info; if ($tab.Count -eq 1) { break }; Start-Sleep -Seconds 1; $info = Get-KacSessionInfo $dir }
    if ($tab.Count -eq 1) { $hit = $tab.Hit }
    Say ('found its tab by the title in the record [{0}]' -f $info.Title) ($tab.Count -eq 1) ('{0} match(es)' -f $tab.Count)
    $same = $false
    if ($hit -and (Open-KacTab $hit.Root $hit.Tab.Id)) { $same = Wait-KacShownUser $root $u 20 }
    $shownTab = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
    Say 'the panel shows the same last message as the record' $same ('shown tab [{0}], panel [{1}]' -f $shownTab.Title, (Get-KacShort (Get-KacShownUser $root) 40))
    $d = Get-KacDecision $rec @{ Tries = 0; Mode = $null } $cfg
    Say 'would leave a normal finish alone' ($d.Action -eq 'none') $d.Why
  } finally {
    $closed = Close-KacTab $root $new.Id
    if (-not $closed -and $hit) { $closed = Close-KacTab $hit.Root $hit.Tab.Id }
    Start-Sleep -Seconds 1
    Say 'closed the test session' $closed
  }
}
if ($SelfTest) { Invoke-KacSelfTest; '{0} passed, {1} failed' -f $script:stPass, $script:stFail; exit $script:stFail }

# -TestNotify: one test notice, so you can see that notify.webhook reaches your phone.
if ($TestNotify) {
  $err = Send-KacNotice $cfg (Format-KacNotice $cfg 'test' $env:COMPUTERNAME '')
  if ($err) { 'not sent: ' + $err; exit 1 }
  'sent a test notice ({0}); check your phone' -f (Get-KacNotifyKind ([string]$cfg.notify.webhook)); exit 0
}

# Other watchers running (not -Inspect / -SelfTest / -TestNotify / -TestRemote / -Report runs).
function Get-KacOtherWatchers {
  return @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" | Where-Object {
    $_.ProcessId -ne $PID -and $_.CommandLine -match 'kiro-auto-continue\.ps1' -and $_.CommandLine -notmatch '\s-(Inspect|SelfTest|TestNotify|TestRemote|Report)\b' })
}

# -TestRemote: connect to WeCom as the bot, check that it answers a heartbeat, disconnect. WeCom keeps one
# connection per bot, so this does not run while a watcher holds it.
if ($TestRemote) {
  if (-not $cfg.remote.botId -or -not $cfg.remote.secret) { 'not set: remote.botId and remote.secret (put them in config.json)'; exit 1 }
  $running = @(Get-KacOtherWatchers)
  if ($running.Count -gt 0) { 'a watcher is running (pid {0}) and holds the bot''s connection; look for "remote: online" in its log' -f $running[0].ProcessId; exit 1 }
  $sh = Start-KacRemote $cfg
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt 30 -and $sh.State -ne 'online' -and -not $sh.AuthFailed) { Start-Sleep -Milliseconds 300 }
  $pong = $false
  if ($sh.State -eq 'online') {
    $t1 = Get-Date; $sh.PingNow = $true
    while (((Get-Date) - $t1).TotalSeconds -lt 10 -and $sh.LastPong -lt $t1) { Start-Sleep -Milliseconds 300 }
    $pong = ($sh.LastPong -ge $t1)
  }
  $sh.Stop = $true; Start-Sleep -Milliseconds 800
  $line = $null; while ($sh.Log.TryDequeue([ref]$line)) { 'remote: ' + $line }
  if ($pong) { 'connected to WeCom as the bot, and it answered a heartbeat'; exit 0 }
  'not connected (state: {0})' -f $sh.State; exit 1
}

# One watcher at a time: two would both type into the same session.
$others = @(Get-KacOtherWatchers)
if (-not $Report) {
  if ($others.Count -gt 0) { Write-KacLog ('another kiro-auto-continue is already running (pid {0}); this one exits' -f $others[0].ProcessId); exit 1 }
  try { $Host.UI.RawUI.WindowTitle = 'kiro-auto-continue (close this window to stop)' } catch {}
  try { [KacNative]::NoQuickEdit() } catch {}
}

$read = @{}      # session id -> bytes of its record already read
$pending = @{}   # session id -> @{ Dir; StopAt; Due; Fails; Told... }: a turn ended and has not been dealt with yet
$state = @{}     # session id -> @{ Tries; Mode }: see Get-KacDecision
$names = @{}     # session id -> "workspace | title", for the log
$script:sent = 0
$script:kiroStart = $null   # when the Kiro process it last saw started (Get-KacKiroStart)
$script:awake = $false; $script:awakeAt = [DateTime]::MinValue   # keepAwake: on or not, when last checked
$script:remote = $null      # commands from your phone: the connection's shared state (Start-KacRemote), or off
$script:attention = @{}     # session id -> code of the last notice about it, until it runs again
$script:creditWait = @{}    # session id -> when it stopped out of credits; continued once credits are back
$script:reportAt = [DateTime]::MinValue   # when the morning status was last looked at
function Get-KacSessionState([string]$id) {
  if (-not $state.ContainsKey($id)) { $state[$id] = @{ Tries = 0; Mode = $null } }
  return $state[$id]
}
function Set-KacSessionState($st, $set) { foreach ($k in @($set.Keys)) { $st[$k] = $set[$k] } }
function Wait-KacLook([string]$id, [string]$why, [int]$seconds = 60) {
  $pd = $pending[$id]; if ($null -eq $pd) { return }
  $pd.Fails++
  if ($pd.Fails -ge 6) { $pending.Remove($id); Write-KacNotice 'cannotSend' $names[$id] 'NEEDS YOU' ('{0}; gave up after {1} tries' -f $why, $pd.Fails) $id; return }
  $pd.Due = (Get-Date).AddSeconds($seconds)
  Write-KacLog ('{0} : {1}; looking again in {2} s' -f $names[$id], $why, $seconds)
}

# A turn ended a while ago: decide from the record, and only if there is something to send, find the session's
# tab, show it, check it is the right one, type, send, and show your tab again.
function Invoke-KacLook([string]$id) {
  $pd = $pending[$id]
  $info = Get-KacSessionInfo $pd.Dir
  if ($null -eq $info) { Wait-KacLook $id 'cannot read its session.json'; return }
  $name = '{0} | {1}' -f $info.Workspace, $info.Title; $names[$id] = $name
  $turn = Read-KacTurn $pd.Dir
  [void](Set-KacRestartCutNow $turn)
  if (-not $turn.Ended) { $pending.Remove($id); return }   # it is running again
  # A normal finish shows the credits work: sessions that stopped out of credits can go on.
  $normal = ($turn.Credits -and -not $turn.Cancelled -and -not $turn.Restart)
  if ($normal -and $script:creditWait.Count -gt 0) { Resume-KacCreditWaits $id $name }
  if ($pd.CreditsBack) { $turn | Add-Member -NotePropertyName CreditsBack -NotePropertyValue $true -Force }
  $st = Get-KacSessionState $id
  # A message from your phone that waited for this turn goes in first.
  $queued = $null; if ($normal) { $queued = Get-KacQueuedFirst $id }
  if ($queued) { $d = New-KacDecision 'send' ('a message from your phone was waiting [{0}]' -f $turn.Footer) @{ Tries = 0 } 'queued' ([string]$queued.Text) 0 }
  else { $d = Get-KacDecision $turn $st $cfg }
  if ($d.Action -eq 'none') { Set-KacSessionState $st $d.Set; $pending.Remove($id); Write-KacLog ('{0} : {1}' -f $name, $d.Why); return }
  if ($d.Action -eq 'stop') { Set-KacSessionState $st $d.Set; $pending.Remove($id); Write-KacNotice $d.Code $name 'NEEDS YOU' $d.Why $id; return }
  if ($d.Action -eq 'done') { Set-KacSessionState $st $d.Set; $pending.Remove($id); Write-KacNotice 'done' $name 'DONE' $d.Why $id; return }
  if ($d.Action -eq 'wait') { Wait-KacLook $id $d.Why; return }
  $due = $pd.StopAt.AddSeconds($d.After)
  if ($due -gt (Get-Date)) { $pd.Due = $due; Write-KacLog ('{0} : {1}; will send in {2} s' -f $name, $d.Why, [int]($due - (Get-Date)).TotalSeconds); return }
  if ($script:sent -ge [int]$cfg.resume.maxPerRun) { $pending.Remove($id); Write-KacNotice 'tooMany' $name 'NEEDS YOU' ('{0}; not sent, {1} messages this run already' -f $d.Why, $script:sent) $id; return }
  if ($DryRun) { $pending.Remove($id); Write-KacLog ('{0} : {1}; dry run, would send {2}: [{3}]' -f $name, $d.Why, $d.Kind, (Get-KacShort $d.Text 80)); return }
  # While Kiro is down or still bringing its tabs back after a restart, try again shortly (not a failure).
  $ks = Get-KacKiroStart
  if ($null -eq $ks -or ((Get-Date) - $ks).TotalSeconds -lt [int]$cfg.kiroStartupSeconds) {
    if (-not $pd.ToldKiro) { $pd.ToldKiro = $true; Write-KacLog ('{0} : {1}; waiting for Kiro to start up' -f $name, $d.Why) }
    $pd.Due = (Get-Date).AddSeconds(15); return
  }
  $idleNeed = [int]$cfg.idleSeconds; if ($d.Kind -eq 'queued') { $idleNeed = [int]$cfg.remote.idleSeconds }
  if ([KacNative]::IdleSeconds() -lt $idleNeed) {
    if (-not $pd.Told) { $pd.Told = $true; Write-KacLog ('{0} : {1}; will send once keyboard and mouse have been idle for {2} s' -f $name, $d.Why, $idleNeed) }
    $pd.Due = (Get-Date).AddSeconds(15); return
  }
  $found = Find-KacSessionTab $info
  if ($found.Count -ne 1) { Wait-KacLook $id (Get-KacIf ($found.Count -eq 0) 'its tab is not open in Kiro' 'several tabs have its title') 300; return }
  $root = $found.Hit.Root; $tab = $found.Hit.Tab
  # Working: it runs again. Not after a restart: the record shows the turn was lost, and a tab Kiro brought
  # back may still show what it was doing then.
  if ($tab.State -eq 'Working') {
    if (-not $turn.Restart) { $pending.Remove($id); return }
    if (-not $pd.ToldWorking) { $pd.ToldWorking = $true; Write-KacLog ('{0} : its tab says Working, but its record shows the turn was lost in the restart' -f $name) }
  }
  $locked = Get-KacIf (Test-KacLocked) ' (screen locked)'
  $back = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
  try {
    if (-not (Open-KacTab $root $tab.Id 40)) { Wait-KacLook $id ('could not switch to its tab' + $locked); return }
    $lastUser = ''; if (@($turn.Users).Count -gt 0) { $lastUser = [string]@($turn.Users)[0] }
    if (-not (Wait-KacShownUser $root $lastUser 20)) { Wait-KacLook $id ('its tab does not show its last message yet' + $locked); return }
    $err = Send-KacMessage $root $tab.Id $d.Text $cfg.placeholders
    if ($err -eq 'draft') { Wait-KacLook $id 'the chat box holds a draft; not touching it' 300; return }
    if ($err) { Wait-KacLook $id ('could not send: ' + $err + $locked); return }
    Set-KacSessionState $st $d.Set; $script:sent++; $pending.Remove($id)
    Write-KacLog ('{0} : {1}; sent {2}{3}' -f $name, $d.Why, $d.Kind, $locked)
    if ($d.Kind -eq 'queued') {
      Remove-KacQueuedFirst $id; $script:attention.Remove($id)
      $rw = (Get-KacWords $cfg (Get-KacNoticeLang $cfg)).remote
      Send-KacRemotePush ([string]$queued.From) (Format-KacText $rw.queueSent @{ n = (Get-KacNumber $id); text = (Get-KacShort ([string]$queued.Text) 60) })
    }
  } finally {
    if ($back -and $back.Id -ne $tab.Id) { [void](Show-KacTab $root $back.Id) }
  }
}
# Kiro started at $ks: queue the sessions whose last turn a restart cut short, from the records written since
# $since. A turn left open by the restart never shows up as a turn ending, so it has to be looked for.
function Add-KacRestartCuts($since, $ks) {
  foreach ($f in @(Get-KacSessionFiles | Where-Object { $_.Modified -ge $since })) {
    if ($pending.ContainsKey($f.Id) -or ($OnlyNew -and $f.Created -lt $startAt)) { continue }
    $t = Read-KacTurn $f.Dir
    if (-not (Set-KacRestartCutNow $t)) { continue }
    $info = Get-KacSessionInfo $f.Dir
    if ($null -eq $info -or ($Only -and $info.Title -notlike "*$Only*")) { continue }
    $names[$f.Id] = '{0} | {1}' -f $info.Workspace, $info.Title
    Write-KacLog ('{0} : its last turn was cut short by the restart [{1}]' -f $names[$f.Id], $t.Footer)
    $pending[$f.Id] = @{ Dir = $f.Dir; StopAt = $ks; Due = (Get-Date).AddSeconds([int]$cfg.settleSeconds); Fails = 0; Told = $false }
  }
}
# On start: the turns cut off (stopReason error) in the last resume.catchUpMinutes, which a watcher that ran
# before this one may still have been waiting to continue, so restarting this tool drops nothing. Out of credits
# is skipped here: Add-KacCreditWaits puts those on the wait for credits.
function Add-KacRecentCuts([double]$minutes) {
  $since = (Get-Date).AddMinutes(-$minutes)
  foreach ($f in @(Get-KacSessionFiles | Where-Object { $_.Modified -ge $since })) {
    if ($pending.ContainsKey($f.Id) -or ($OnlyNew -and $f.Created -lt $startAt)) { continue }
    $t = Read-KacTurn $f.Dir
    if (-not $t.Ended -or $t.Stop -ne 'error') { continue }
    if ($t.ErrorType -and @($cfg.resume.limitErrors) -contains $t.ErrorType) { continue }
    $end = ConvertFrom-KacTime $t.EndedAt
    if ($null -eq $end -or $end.ToLocalTime() -lt $since) { continue }
    $info = Get-KacSessionInfo $f.Dir
    if ($null -eq $info -or ($Only -and $info.Title -notlike "*$Only*")) { continue }
    $names[$f.Id] = '{0} | {1}' -f $info.Workspace, $info.Title
    Write-KacLog ('{0} : cut off at {1:HH:mm:ss}, before this started [{2}]' -f $names[$f.Id], $end.ToLocalTime(), $t.Footer)
    $pending[$f.Id] = @{ Dir = $f.Dir; StopAt = $end.ToLocalTime(); Due = (Get-Date).AddSeconds([int]$cfg.settleSeconds); Fails = 0; Told = $false }
  }
}

# Out of credits: such sessions wait in $script:creditWait. When another session ($byId, $byName) finishes a turn
# normally, the credits are back: continue them like a cut-off (CreditsBack lets Get-KacDecision send).
function Resume-KacCreditWaits([string]$byId, [string]$byName) {
  foreach ($w in @($script:creditWait.Keys)) {
    $script:creditWait.Remove($w)
    if ($w -eq $byId -or $pending.ContainsKey($w)) { continue }
    $f = @(Get-KacSessionFiles | Where-Object { $_.Id -eq $w }) | Select-Object -First 1
    if ($null -eq $f) { continue }
    $label = Get-KacSessionLabel $f
    Write-KacLog ('{0} : credits are back ({1} finished a turn); continuing it' -f $label, $byName)
    $pending[$w] = @{ Dir = $f.Dir; StopAt = (Get-Date).AddMinutes(-10); Due = (Get-Date).AddSeconds([int]$cfg.settleSeconds); Fails = 0; Told = $false; CreditsBack = $true }
  }
}
# On start: the sessions whose last turn ran out of credits in the last $hours wait for the credits to come back.
function Add-KacCreditWaits([double]$hours) {
  $since = (Get-Date).AddHours(-$hours)
  foreach ($f in @(Get-KacSessionFiles | Where-Object { $_.Modified -ge $since })) {
    if ($pending.ContainsKey($f.Id) -or ($OnlyNew -and $f.Created -lt $startAt)) { continue }
    $t = Read-KacTurn $f.Dir
    if (-not $t.Ended -or $t.Stop -ne 'error' -or -not $t.ErrorType -or @($cfg.resume.limitErrors) -notcontains $t.ErrorType) { continue }
    $info = Get-KacSessionInfo $f.Dir
    if ($null -eq $info -or ($Only -and $info.Title -notlike "*$Only*")) { continue }
    $names[$f.Id] = '{0} | {1}' -f $info.Workspace, $info.Title
    $script:creditWait[$f.Id] = Get-Date; $script:attention[$f.Id] = 'outOfCredits'
    Write-KacLog ('{0} : out of credits before this started; continued once credits are back or Kiro restarts' -f $names[$f.Id])
  }
}

# ---- Commands from your phone: a WeCom smart bot (remote.*, src/Remote.ps1) ----
$KacNumbersFile = Join-Path $KacLogDir 'session-numbers.json'
$KacAllowFile = Join-Path $KacHome 'remote-allow.json'
$KacQueueFile = Join-Path $KacLogDir 'queue.json'
$KacReportFile = Join-Path $KacLogDir 'report-state.json'
$script:remoteSeen = New-Object 'System.Collections.Generic.Queue[string]'   # message ids already handled
$script:remoteEnded = $false; $script:pairCode = $null; $script:pairTries = 0
function Write-KacJsonFile([string]$path, $value) {
  [System.IO.File]::WriteAllText($path, (ConvertTo-Json -Compress -Depth 6 $value), (New-Object System.Text.UTF8Encoding $false))
}
# The number of a session on your phone ("continue 3"): given the first time it is listed and kept in a file, so it
# stays the same across runs, and a -Report run uses the same numbers.
function Get-KacNumbers {
  $m = @{}
  try { $j = Get-Content -Raw -Encoding UTF8 -LiteralPath $KacNumbersFile | ConvertFrom-Json; foreach ($p in $j.PSObject.Properties) { $m[$p.Name] = [int]$p.Value } } catch {}
  return $m
}
function Get-KacNumber([string]$id) {
  $m = Get-KacNumbers
  if (-not $m.ContainsKey($id)) {
    $max = 0; foreach ($v in @($m.Values)) { if ($v -gt $max) { $max = $v } }
    $m[$id] = $max + 1
    try { Write-KacJsonFile $KacNumbersFile $m } catch {}
  }
  return $m[$id]
}
function Find-KacNumbered([int]$n) {
  $m = Get-KacNumbers
  foreach ($k in @($m.Keys)) { if ($m[$k] -eq $n) { return $k } }
  return $null
}
function Get-KacNumberedFile([int]$n) {
  $id = Find-KacNumbered $n
  if (-not $id) { return $null }
  return (@(Get-KacSessionFiles | Where-Object { $_.Id -eq $id }) | Select-Object -First 1)
}
function Get-KacSessionLabel($f) {
  if ($names.ContainsKey($f.Id)) { return $names[$f.Id] }
  $info = Get-KacSessionInfo $f.Dir
  if ($info) { $names[$f.Id] = '{0} | {1}' -f $info.Workspace, $info.Title; return $names[$f.Id] }
  return $f.Id
}
function Get-KacWhy($rw, [string]$why) { $w = [string]$rw.why.$why; if (-not $w) { $w = $why }; return $w }
# Who may give commands: remote.allowFrom, and whoever paired (remote-allow.json, kept out of git).
function Get-KacAllowed {
  $set = @{}
  foreach ($u in @($cfg.remote.allowFrom)) { if ($u) { $set[[string]$u] = 1 } }
  try { $j = Get-Content -Raw -Encoding UTF8 -LiteralPath $KacAllowFile | ConvertFrom-Json; foreach ($u in @($j.users)) { if ($u) { $set[[string]$u] = 1 } } } catch {}
  return $set
}
function Add-KacPaired([string]$user) {
  $users = @()
  try { $j = Get-Content -Raw -Encoding UTF8 -LiteralPath $KacAllowFile | ConvertFrom-Json; $users = @($j.users | Where-Object { $_ }) } catch {}
  if ($users -notcontains $user) { $users += $user }
  Write-KacJsonFile $KacAllowFile @{ users = @($users) }
}
function Send-KacRemoteReply([string]$req, [string]$sid, [string]$content, [bool]$finish = $true) {
  if ($script:remote) { $script:remote.Outbox.Enqueue((New-KacReplyFrame $req $sid $content $finish)) }
}
# A message the bot sends on its own (not a reply): to the account $to, or to every paired account.
function Send-KacRemotePush([string]$to, [string]$text) {
  if (-not $script:remote) { return }
  $targets = @($to); if (-not $to) { $targets = @((Get-KacAllowed).Keys) }
  foreach ($u in $targets) { $script:remote.Outbox.Enqueue((New-KacPushFrame ([string]$u) $text)) }
}
# Messages from your phone waiting for a running session to finish its turn (logs\queue.json, read and written each
# time, so they survive a restart of the watcher): session id -> list of { Text, From, At }.
function Get-KacQueueAll {
  $all = @{}
  try { $j = Get-Content -Raw -Encoding UTF8 -LiteralPath $KacQueueFile | ConvertFrom-Json; foreach ($p in $j.PSObject.Properties) { $all[$p.Name] = @($p.Value) } } catch {}
  return $all
}
function Save-KacQueueAll($all) {
  $o = [ordered]@{}; foreach ($k in @($all.Keys)) { if (@($all[$k]).Count -gt 0) { $o[$k] = @($all[$k]) } }
  Write-KacJsonFile $KacQueueFile $o
}
function Get-KacQueued([string]$id) { $all = Get-KacQueueAll; if ($all.ContainsKey($id)) { return @($all[$id]) }; return @() }
function Get-KacQueuedFirst([string]$id) { $q = @(Get-KacQueued $id); if ($q.Count -gt 0) { return $q[0] }; return $null }
function Add-KacQueued([string]$id, [string]$text, [string]$from) {
  $all = Get-KacQueueAll
  $list = @(); if ($all.ContainsKey($id)) { $list = @($all[$id]) }
  $list += [pscustomobject]@{ Text = $text; From = $from; At = (Get-Date).ToString('o') }
  $all[$id] = $list; Save-KacQueueAll $all
  return $list.Count
}
function Remove-KacQueuedFirst([string]$id) {
  $all = Get-KacQueueAll
  if ($all.ContainsKey($id)) { $all[$id] = @($all[$id] | Select-Object -Skip 1); Save-KacQueueAll $all }
}
function Clear-KacQueued([string]$id) {
  $all = Get-KacQueueAll; $n = 0
  if ($all.ContainsKey($id)) { $n = @($all[$id]).Count; $all.Remove($id); Save-KacQueueAll $all }
  return $n
}
# Before typing a command from your phone: remote.idleSeconds without keyboard or mouse input, waiting at most
# remote.waitSeconds. $true once idle.
function Wait-KacRemoteIdle {
  $t0 = Get-Date
  while ([KacNative]::IdleSeconds() -lt [int]$cfg.remote.idleSeconds) {
    if (((Get-Date) - $t0).TotalSeconds -ge [int]$cfg.remote.waitSeconds) { return $false }
    Start-Sleep -Seconds 1
  }
  return $true
}
# One line per session whose record was written in the last $hours, newest first, with its number, how its last
# turn stands and what the watcher knows about it; with $counts (Get-KacLogCounts), how often it was continued.
function Get-KacStatusLines($rw, [double]$hours, $counts = $null) {
  $since = (Get-Date).AddHours(-$hours)
  $files = @(Get-KacSessionFiles | Where-Object { $_.Modified -ge $since } | Sort-Object Modified -Descending | Select-Object -First ([int]$cfg.remote.maxList))
  $lines = @()
  foreach ($f in $files) {
    $info = Get-KacSessionInfo $f.Dir
    if ($null -eq $info) { continue }
    $t = Read-KacTurn $f.Dir
    [void](Set-KacRestartCutNow $t)
    $nm = '{0} | {1}' -f $info.Workspace, $info.Title
    $s = Get-KacTurnState $t $rw
    if ($pending.ContainsKey($f.Id)) { $s += [string]$rw.pending }
    $code = [string]$script:attention[$f.Id]
    if ($code -and $rw.need.$code) { $s += (Format-KacText $rw.needFormat @{ what = $rw.need.$code }) }
    if ($state.ContainsKey($f.Id) -and $null -ne $state[$f.Id].Mode) { $s += (Format-KacText $rw.untilDone @{ pushes = $state[$f.Id].Mode.Pushes }) }
    $qn = @(Get-KacQueued $f.Id).Count
    if ($qn -gt 0) { $s += (Format-KacText $rw.queuedAnn @{ k = $qn }) }
    if ($counts -and [int]$counts.Resumes[$nm] -gt 0) { $s += (Format-KacText $rw.countAnn @{ k = $counts.Resumes[$nm] }) }
    $lines += (Format-KacText $rw.line @{ n = (Get-KacNumber $f.Id); name = $nm; state = $s })
  }
  return $lines
}
# "status": the sessions written in the last remote.listHours.
function Get-KacRemoteStatus($rw) {
  $hours = [double]$cfg.remote.listHours
  $lines = @(Get-KacStatusLines $rw $hours)
  if ($lines.Count -eq 0) { return (Format-KacText $rw.none @{ hours = $hours }) }
  return (($lines + @('', (Format-KacText $rw.hint @{}))) -join "`n")
}
# Show a session's tab and check it is the right one: the panel shows the record's last message. A running
# session's panel keeps changing, so it waits for the switch and that message only. '' once shown, or why not.
function Open-KacRemoteTab($root, $tab, $turn) {
  $lastUser = ''; if (@($turn.Users).Count -gt 0) { $lastUser = [string]@($turn.Users)[0] }
  if ($turn.Ended) {
    if (-not (Open-KacTab $root $tab.Id 40)) { return 'notShown' }
  } elseif (-not $tab.Selected) {
    $before = Get-KacViewMark $root
    if (-not (Show-KacTab $root $tab.Id)) { return 'notShown' }
    $changed = $false
    for ($i = 0; $i -lt 40 -and -not $changed; $i++) { Start-Sleep -Milliseconds 500; $changed = ((Get-KacViewMark $root) -ne $before) }
    if (-not $changed) { return 'notShown' }
  }
  if (-not (Wait-KacShownUser $root $lastUser 20)) { return 'notShown' }
  return ''
}
# Type $text into the session whose record is $f, checked the way a continue is: its tab found by title, the panel
# showing its last message. '' once sent, else a key of words.<lang>.remote.why or an error text.
function Send-KacRemoteText($f, $turn, [string]$text) {
  $info = Get-KacSessionInfo $f.Dir
  if ($null -eq $info) { return 'cannot read its session.json' }
  if (-not (Wait-KacRemoteIdle)) { return 'busy' }
  $found = Find-KacSessionTab $info
  if ($found.Count -eq 0) { return 'noTab' }
  if ($found.Count -gt 1) { return 'twoTabs' }
  $root = $found.Hit.Root; $tab = $found.Hit.Tab
  if ($tab.State -eq 'Working' -and -not $turn.Restart) { return 'working' }
  $back = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
  try {
    $why = Open-KacRemoteTab $root $tab $turn
    if ($why) { return $why }
    $err = Send-KacMessage $root $tab.Id $text $cfg.placeholders
    if ($err -eq 'draft') { return 'draft' }
    return $err
  } finally {
    if ($back -and $back.Id -ne $tab.Id) { [void](Show-KacTab $root $back.Id) }
  }
}
# Press stop on the running session whose record is $f. '' once its tab no longer says Working, or why not.
function Stop-KacRemoteSession($f, $turn) {
  $info = Get-KacSessionInfo $f.Dir
  if ($null -eq $info) { return 'cannot read its session.json' }
  if (-not (Wait-KacRemoteIdle)) { return 'busy' }
  $found = Find-KacSessionTab $info
  if ($found.Count -eq 0) { return 'noTab' }
  if ($found.Count -gt 1) { return 'twoTabs' }
  $root = $found.Hit.Root; $tab = $found.Hit.Tab
  $back = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
  try {
    $why = Open-KacRemoteTab $root $tab $turn
    if ($why) { return $why }
    Start-Sleep -Seconds 1
    return (Stop-KacTurn $root $tab.Id)
  } finally {
    if ($back -and $back.Id -ne $tab.Id) { [void](Show-KacTab $root $back.Id) }
  }
}
# "continue N" ($text $null: the continue message, see Get-KacManualContinue) and "N <text>". Text for a session
# that is running waits for its turn to finish (Get-KacQueuedFirst in Invoke-KacLook).
function Invoke-KacRemoteSend([string]$req, [string]$sid, [int]$n, $text, $rw, [string]$from = '') {
  if ($n -le 0) { Send-KacRemoteReply $req $sid ([string]$rw.noNumber); return }
  $vars = @{ n = $n }
  $f = Get-KacNumberedFile $n
  if ($null -eq $f) { Send-KacRemoteReply $req $sid (Format-KacText $rw.noSuch $vars); return }
  $id = $f.Id; $name = Get-KacSessionLabel $f; $isSend = ($null -ne $text)
  $turn = Read-KacTurn $f.Dir
  [void](Set-KacRestartCutNow $turn)
  if (-not $turn.Ended) {
    if (-not $isSend) { Send-KacRemoteReply $req $sid (Format-KacText $rw.working $vars); return }
    $k = Add-KacQueued $id ([string]$text) $from
    Write-KacLog ('{0} : a message from your phone waits for this turn to finish ({1} waiting): [{2}]' -f $name, $k, (Get-KacShort ([string]$text) 60))
    Send-KacRemoteReply $req $sid (Format-KacText $rw.queued @{ n = $n; k = $k })
    return
  }
  if (-not $isSend) { $text = Get-KacManualContinue $turn $cfg }
  Send-KacRemoteReply $req $sid (Format-KacText $rw.sending $vars) $false
  $why = Send-KacRemoteText $f $turn ([string]$text)
  if ($why -eq '') {
    $st = Get-KacSessionState $id; $st.Tries = 0
    $pending.Remove($id); $script:attention.Remove($id)
    Write-KacLog ('{0} : sent from your phone: [{1}]' -f $name, (Get-KacShort ([string]$text) 60))
    Send-KacRemoteReply $req $sid (Format-KacText $rw.sent $vars)
  } elseif ($why -eq 'working' -and $isSend) {
    $k = Add-KacQueued $id ([string]$text) $from
    Write-KacLog ('{0} : a message from your phone waits for this turn to finish ({1} waiting): [{2}]' -f $name, $k, (Get-KacShort ([string]$text) 60))
    Send-KacRemoteReply $req $sid (Format-KacText $rw.queued @{ n = $n; k = $k })
  } else {
    Write-KacLog ('{0} : not sent from your phone: {1}' -f $name, $why)
    Send-KacRemoteReply $req $sid (Format-KacText $rw.notSent @{ why = (Get-KacWhy $rw $why) })
  }
}
# "stop N": press stop on a running session.
function Invoke-KacRemoteStop([string]$req, [string]$sid, [int]$n, $rw) {
  if ($n -le 0) { Send-KacRemoteReply $req $sid ([string]$rw.noNumber); return }
  $vars = @{ n = $n }
  $f = Get-KacNumberedFile $n
  if ($null -eq $f) { Send-KacRemoteReply $req $sid (Format-KacText $rw.noSuch $vars); return }
  $name = Get-KacSessionLabel $f
  $turn = Read-KacTurn $f.Dir
  [void](Set-KacRestartCutNow $turn)
  if ($turn.Ended) { Send-KacRemoteReply $req $sid (Format-KacText $rw.notRunning $vars); return }
  Send-KacRemoteReply $req $sid (Format-KacText $rw.stopping $vars) $false
  $why = Stop-KacRemoteSession $f $turn
  if ($why -eq '') {
    Write-KacLog ('{0} : stopped from your phone' -f $name)
    Send-KacRemoteReply $req $sid (Format-KacText $rw.stopped $vars)
  } else {
    Write-KacLog ('{0} : not stopped from your phone: {1}' -f $name, $why)
    Send-KacRemoteReply $req $sid (Format-KacText $rw.notStopped @{ why = (Get-KacWhy $rw $why) })
  }
}
# "clear N": drop what waits for session N.
function Invoke-KacRemoteClear([string]$req, [string]$sid, [int]$n, $rw) {
  if ($n -le 0) { Send-KacRemoteReply $req $sid ([string]$rw.noNumber); return }
  $id = Find-KacNumbered $n
  $k = 0; if ($id) { $k = Clear-KacQueued $id }
  if ($k -gt 0) {
    Write-KacLog ('remote: dropped {0} message(s) waiting for #{1}' -f $k, $n)
    Send-KacRemoteReply $req $sid (Format-KacText $rw.cleared @{ n = $n; k = $k })
  } else { Send-KacRemoteReply $req $sid (Format-KacText $rw.nothingQueued @{ n = $n }) }
}
# Open a new session in the Kiro window of $workspace and send $text. Why ('' once sent) and Id (its record's
# session id, once it shows up).
function New-KacRemoteSession([string]$workspace, [string]$text) {
  $out = [pscustomobject]@{ Why = ''; Id = '' }
  if (-not (Wait-KacRemoteIdle)) { $out.Why = 'busy'; return $out }
  $win = @(Get-KacWindows | Where-Object { $_.Workspace -eq $workspace }) | Select-Object -First 1
  $root = $null; if ($win) { $root = Get-KacChatRoot $win }
  if ($null -eq $root) { $out.Why = 'noWindow'; return $out }
  $back = Get-KacTabs $root | Where-Object { $_.Selected } | Select-Object -First 1
  $before = @{}; foreach ($t in Get-KacTabs $root) { $before[$t.Id] = 1 }
  $t0 = Get-Date
  # One press only: with Kiro minimized the new tab can take 30 s to show up, and a second press opens a second one.
  if (-not (New-KacSession $root)) { $out.Why = 'noButton'; return $out }
  $new = $null
  for ($i = 0; $i -lt 80 -and $null -eq $new; $i++) { Start-Sleep -Milliseconds 500; $new = Get-KacTabs $root | Where-Object { -not $before.ContainsKey($_.Id) } | Select-Object -First 1 }
  if ($null -eq $new) { $out.Why = 'newTab'; return $out }
  try {
    if (-not (Show-KacTab $root $new.Id)) { $out.Why = 'newTab'; return $out }
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) { Start-Sleep -Milliseconds 500; $m = Get-KacViewMark $root; $ready = ($m -eq 'none' -or $m -like '0|*') }
    if (-not $ready) { $out.Why = 'notReady'; return $out }
    Start-Sleep -Seconds 1
    $err = Send-KacMessage $root $new.Id $text $cfg.placeholders
    if ($err) { $out.Why = $err; return $out }
  } finally {
    if ($back) { [void](Show-KacTab $root $back.Id) }
  }
  # Its record: a session folder made since, whose newest message is this text.
  for ($i = 0; $i -lt 20 -and -not $out.Id; $i++) {
    Start-Sleep -Seconds 1
    foreach ($f in @(Get-KacSessionFiles | Where-Object { $_.Created -ge $t0.AddSeconds(-5) })) {
      $u = @((Read-KacTurn $f.Dir).Users)
      if ($u.Count -gt 0 -and (Test-KacSameText ([string]$u[0]) $text)) { $out.Id = $f.Id; break }
    }
  }
  return $out
}
# "new [workspace] <text>".
function Invoke-KacRemoteNew([string]$req, [string]$sid, [string]$text, $rw) {
  $open = @(Get-KacWindows | ForEach-Object { $_.Workspace } | Where-Object { $_ } | Select-Object -Unique)
  $c = Split-KacNewCommand $text $open
  if (-not $c.Workspace) { Send-KacRemoteReply $req $sid (Format-KacText $rw.newWhich @{ list = ($open -join ', ') }); return }
  if (-not $c.Text) { Send-KacRemoteReply $req $sid (Format-KacText $rw.newNoText @{ ws = $c.Workspace }); return }
  Send-KacRemoteReply $req $sid (Format-KacText $rw.newOpening @{ ws = $c.Workspace }) $false
  $r = New-KacRemoteSession $c.Workspace $c.Text
  if ($r.Why) {
    Write-KacLog ('remote: no new session in {0}: {1}' -f $c.Workspace, $r.Why)
    Send-KacRemoteReply $req $sid (Format-KacText $rw.notOpened @{ why = (Get-KacWhy $rw $r.Why) })
    return
  }
  Write-KacLog ('remote: opened a new session in {0} from your phone: [{1}]' -f $c.Workspace, (Get-KacShort $c.Text 60))
  if ($r.Id) { Send-KacRemoteReply $req $sid (Format-KacText $rw.newOpened @{ n = (Get-KacNumber $r.Id) }) }
  else { Send-KacRemoteReply $req $sid (Format-KacText $rw.newOpenedNoNumber @{ ws = $c.Workspace }) }
}
# One frame from WeCom. Only messages from an account in Get-KacAllowed are obeyed; until one has paired, the
# pairing code shown in the log pairs whoever sends it (five wrong codes turn pairing off until the next start).
function Invoke-KacRemoteFrame([string]$raw) {
  $fr = ConvertFrom-Json $raw
  if ([string]$fr.cmd -ne 'aibot_msg_callback') { return }
  $b = $fr.body; $req = [string]$fr.headers.req_id
  $mid = [string]$b.msgid
  if ($mid) {
    if ($script:remoteSeen.Contains($mid)) { return }
    $script:remoteSeen.Enqueue($mid); while ($script:remoteSeen.Count -gt 200) { [void]$script:remoteSeen.Dequeue() }
  }
  $from = [string]$b.from.userid
  $rw = (Get-KacWords $cfg (Get-KacNoticeLang $cfg)).remote
  $text = Get-KacRemoteText $b
  $c = ConvertTo-KacRemoteCommand $text $cfg
  $sid = New-KacReqId 'stream'
  if (-not (Get-KacAllowed).ContainsKey($from)) {
    if ($c.Kind -eq 'pair' -and $script:pairCode) {
      if ($c.Code -eq $script:pairCode) {
        Add-KacPaired $from; $script:pairCode = $null
        Write-KacLog ('remote: paired with {0}; only paired accounts are obeyed' -f $from)
        Send-KacRemoteReply $req $sid ([string]$rw.paired)
      } else {
        $script:pairTries++
        if ($script:pairTries -ge 5) { $script:pairCode = $null; Write-KacLog 'remote: five wrong pairing codes; pairing is off until this starts again' }
        Send-KacRemoteReply $req $sid ([string]$rw.pairWrong)
      }
      return
    }
    Write-KacLog ('remote: ignored a message from {0}, who has not paired' -f $from)
    $msg = [string]$rw.notAllowed; if ($script:pairCode) { $msg += "`n" + [string]$rw.pairHint }
    Send-KacRemoteReply $req $sid $msg
    return
  }
  if (-not $text) {
    Write-KacLog ('remote: {0} sent a {1} message; only text is read' -f $from, $b.msgtype)
    Send-KacRemoteReply $req $sid ([string]$rw.textOnly)
    return
  }
  $what = $c.Kind; if ($c.N -gt 0) { $what += ' ' + $c.N }; if ($c.Kind -eq 'unknown') { $what += ': [' + (Get-KacShort $text 40) + ']' }
  Write-KacLog ('remote: {0} sent {1}' -f $from, $what)
  switch ($c.Kind) {
    'status' { Send-KacRemoteReply $req $sid (Get-KacRemoteStatus $rw) }
    'continue' { Invoke-KacRemoteSend $req $sid $c.N $null $rw $from }
    'send' { Invoke-KacRemoteSend $req $sid $c.N $c.Text $rw $from }
    'stop' { Invoke-KacRemoteStop $req $sid $c.N $rw }
    'clear' { Invoke-KacRemoteClear $req $sid $c.N $rw }
    'new' { Invoke-KacRemoteNew $req $sid $c.Text $rw }
    'pair' { Send-KacRemoteReply $req $sid ([string]$rw.pairClosed) }
    default { Send-KacRemoteReply $req $sid (Format-KacText $rw.help @{}) }
  }
}
# ---- The morning status (report.*, src/Report.ps1) ----
# The sessions written in the last report.hours, with how often each was continued, and one line on what the
# watcher did in that time. $null when no session wrote its record.
function Get-KacReportText {
  $w = Get-KacWords $cfg (Get-KacNoticeLang $cfg); $rw = $w.remote
  $hours = [double]$cfg.report.hours
  $now = Get-Date
  $lines = @(); try { $lines = @(Get-Content -Encoding UTF8 -LiteralPath $KacLogFile -Tail 20000) } catch {}
  $counts = Get-KacLogCounts $lines $now.AddHours(-$hours) $now
  $rows = @(Get-KacStatusLines $rw $hours $counts)
  if ($rows.Count -eq 0) { return $null }
  $head = [string]$w.notice.prefix + (Format-KacText $rw.report.title @{ hours = $hours })
  $sum = Format-KacText $rw.report.summary @{ resumes = (Get-KacTotal $counts.Resumes); pushes = (Get-KacTotal $counts.Pushes); needs = @($counts.Needs.Keys).Count }
  return ((@($head, $sum, '') + $rows) -join "`n")
}
# Sent the way notices go (notify.webhook), or else by the bot to the paired accounts. '' once sent.
function Send-KacReport([string]$text) {
  if ($cfg.notify.webhook) { return (Send-KacNotice $cfg $text) }
  if ($script:remote -and (Get-KacAllowed).Count -gt 0) { Send-KacRemotePush '' $text; return '' }
  return 'no notify.webhook and no account paired with the bot'
}
# Once a minute: today's report, once, between report.time and report.windowHours after it.
function Invoke-KacReportCheck {
  $now = Get-Date
  $due = Get-KacReportDue ([string]$cfg.report.time) $now
  if ($null -eq $due -or $now -lt $due -or $now -gt $due.AddHours([double]$cfg.report.windowHours)) { return }
  $today = $now.ToString('yyyy-MM-dd')
  $last = ''; try { $last = [string](Get-Content -Raw -Encoding UTF8 -LiteralPath $KacReportFile | ConvertFrom-Json).last } catch {}
  if ($last -eq $today) { return }
  Write-KacJsonFile $KacReportFile @{ last = $today }
  $text = Get-KacReportText
  if ($null -eq $text) { Write-KacLog 'morning status: no session wrote its record overnight; not sent'; return }
  $err = Send-KacReport $text
  if ($err) { Write-KacLog ('morning status not sent: ' + $err) } else { Write-KacLog 'morning status sent' }
}
# Once per loop: the connection's log lines, then up to five messages.
function Invoke-KacRemote {
  $line = $null
  while ($script:remote.Log.TryDequeue([ref]$line)) { Write-KacLog ('remote: ' + $line) }
  $sh = $script:remote.Shell
  if (-not $script:remoteEnded -and $sh.InvocationStateInfo.State -ne 'Running') {
    $script:remoteEnded = $true
    $why = [string]$sh.InvocationStateInfo.Reason
    if (-not $why -and $sh.Streams.Error.Count -gt 0) { $why = [string]$sh.Streams.Error[0] }
    Write-KacLog ('remote: the connection thread stopped: ' + $why)
  }
  $raw = $null
  for ($i = 0; $i -lt 5 -and $script:remote.Inbox.TryDequeue([ref]$raw); $i++) {
    try { Invoke-KacRemoteFrame $raw } catch { Write-KacLog ('remote: error while handling a message: ' + $_.Exception.Message) }
  }
}

# -Report: the morning status now: print it and send it the way notices go (notify.webhook), then exit. The bot can
# only send while the watcher holds its connection.
if ($Report) {
  $text = Get-KacReportText
  if ($null -eq $text) { 'no session wrote its record in the last {0} hours; nothing to send' -f $cfg.report.hours; exit 0 }
  $text
  if (-not $cfg.notify.webhook) { 'not sent: notify.webhook is not set'; exit 1 }
  $err = Send-KacNotice $cfg $text
  if ($err) { 'not sent: ' + $err; exit 1 }
  'sent'; exit 0
}

$flags = @()
if ($DryRun) { $flags += 'dry run' }
if ($Only) { $flags += "only sessions titled *$Only*" }
if ($OnlyNew) { $flags += 'only sessions created from now on' }
if ($cfg.testing.cancelIsInterrupt) { $flags += 'your own stops count as interruptions (testing)' }
if ($cfg.keepAwake.enabled) { $flags += 'keeps Windows awake while a session works' }
if ($cfg.remote.botId -and $cfg.remote.secret) { $flags += 'takes commands from your phone through the WeCom bot' }
if ($cfg.notify.webhook) { $flags += ('notices to {0}: {1}' -f (Get-KacIf (Get-KacNotifyKind ([string]$cfg.notify.webhook)) (Get-KacNotifyKind ([string]$cfg.notify.webhook)) 'an unknown webhook'), (@($cfg.notify.events) -join ', ')) }
Write-KacLog ('watching Kiro''s session records every {0} s; types only after {1} s without keyboard or mouse input; until-done {2}{3}' -f `
  $cfg.pollSeconds, $cfg.idleSeconds, (Get-KacIf $cfg.untilDone.enabled 'on' 'off'), (Get-KacIf $flags.Count ('; ' + ($flags -join '; '))))
if ($cfg.remote.botId -and $cfg.remote.secret) {
  $script:remote = Start-KacRemote $cfg
  if ((Get-KacAllowed).Count -eq 0) {
    $rb = New-Object byte[] 4; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rb)
    $script:pairCode = '{0:D6}' -f ([BitConverter]::ToUInt32($rb, 0) % 1000000)
    $pw = [string]@((Get-KacWords $cfg (Get-KacNoticeLang $cfg)).remote.cmd.pair)[0]
    Write-KacLog ('remote: no account has paired yet; to pair yours, send "{0} {1}" to the bot in WeCom' -f $pw, $script:pairCode)
  }
}
$startAt = Get-Date
$first = $true
while ($true) {
  try {
    if ($script:remote) { Invoke-KacRemote }
    foreach ($f in Get-KacSessionFiles) {
      $id = $f.Id
      if ($OnlyNew -and $f.Created -lt $startAt) { continue }
      if (-not $read.ContainsKey($id)) {
        # A record that was there when this started: only turns that end from now on count. A session made
        # since then: from its first line, so a turn cut off seconds after you sent it is not missed.
        if ($first -or $f.Created -lt $startAt) { $read[$id] = $f.Size; continue }
        $read[$id] = 0
      }
      if ($f.Size -eq $read[$id]) { continue }
      $r = Read-KacLines $f.Messages $read[$id] ([long]$cfg.recordTailBytes)
      $read[$id] = $r.End
      $lastStart = -1; $lastEnd = -1; $why = ''
      for ($i = 0; $i -lt $r.Lines.Count; $i++) {
        $l = $r.Lines[$i]
        if ($l.Contains('"type":"turn_end"')) {
          $lastEnd = $i; $m = $KacRxStop.Match($l); $why = ''; if ($m.Success) { $why = $m.Groups[1].Value }
          if ($l.Contains('"boundarySource":"recovered"')) { $why = 'lost in a Kiro restart' }
        }
        elseif ($l.Contains('"type":"turn_start"')) { $lastStart = $i }
      }
      if ($lastStart -gt $lastEnd) { $pending.Remove($id); $script:attention.Remove($id); $script:creditWait.Remove($id); continue }   # it started (again)
      if ($lastEnd -lt 0) { continue }
      $info = Get-KacSessionInfo $f.Dir
      if ($null -eq $info -or ($Only -and $info.Title -notlike "*$Only*")) { continue }
      $names[$id] = '{0} | {1}' -f $info.Workspace, $info.Title
      Write-KacLog ('{0} : turn ended ({1})' -f $names[$id], $why)
      $now = Get-Date
      $pending[$id] = @{ Dir = $f.Dir; StopAt = $now; Due = $now.AddSeconds([int]$cfg.settleSeconds); Fails = 0; Told = $false }
    }
    if ($first) {
      $busy = @()
      foreach ($f in @(Get-KacSessionFiles | Sort-Object Modified -Descending | Select-Object -First 30)) {
        $info = Get-KacSessionInfo $f.Dir
        if ($OnlyNew -or $null -eq $info -or $info.Status -ne 'in_progress') { continue }
        if (-not $Only -or $info.Title -like "*$Only*") { $busy += ('{0} | {1}' -f $info.Workspace, $info.Title) }
      }
      Write-KacLog ('{0} session records; working now: {1}' -f $read.Count, (Get-KacIf $busy.Count ($busy -join '; ') 'none'))
      if ([double]$cfg.resume.catchUpMinutes -gt 0) { Add-KacRecentCuts ([double]$cfg.resume.catchUpMinutes) }
      if ([double]$cfg.resume.creditWaitHours -gt 0) { Add-KacCreditWaits ([double]$cfg.resume.creditWaitHours) }
    }
    # Kiro started (again): look for the turns the restart cut short, in the records written since the Kiro
    # start before, or at least in the restartWindowMinutes before this one. On the first pass only if Kiro
    # started within restartWindowMinutes (this was opened right after it).
    $ks = Get-KacKiroStart
    if ($ks -and $ks -ne [DateTime]::MinValue -and $ks -ne $script:kiroStart) {
      $win = [double]$cfg.restartWindowMinutes
      $since = $ks.AddMinutes(-$win)
      if ($script:kiroStart -and $script:kiroStart -ne [DateTime]::MinValue -and $script:kiroStart -lt $since) { $since = $script:kiroStart }
      if (-not $first) { Write-KacLog ('Kiro started at {0:HH:mm:ss}' -f $ks) }
      if (-not $first -or ((Get-Date) - $ks).TotalMinutes -le $win) { Add-KacRestartCuts $since $ks }
    }
    if ($ks) { $script:kiroStart = $ks }
    $first = $false
    # The morning status (report.time), looked at once a minute.
    if (((Get-Date) - $script:reportAt).TotalSeconds -ge 60) { $script:reportAt = Get-Date; Invoke-KacReportCheck }
    # keepAwake: while a session works, Windows does not go to sleep on idle. Looked at once a minute.
    if ($cfg.keepAwake.enabled -and ((Get-Date) - $script:awakeAt).TotalSeconds -ge 60) {
      $script:awakeAt = Get-Date
      $on = Test-KacAnyWorking (Get-KacSessionFiles) ([double]$cfg.keepAwake.minutes)
      if ($on -ne $script:awake) {
        $ok = [KacNative]::KeepAwake($on); $script:awake = $on
        if ($on) { Write-KacLog ('keeping Windows awake: a session is working' + (Get-KacIf (-not $ok) ' (the request failed)')) }
        else { Write-KacLog ('letting Windows sleep again: no session has worked for {0} min' -f $cfg.keepAwake.minutes) }
      }
    }
    $now = Get-Date
    foreach ($id in @($pending.Keys | Where-Object { $pending[$_].Due -le $now } | Select-Object -First 1)) {
      try { Invoke-KacLook $id }
      catch { Write-KacLog ('{0} : error while looking at it: {1}' -f $names[$id], $_.Exception.Message); Wait-KacLook $id 'error' }
    }
  } catch { Write-KacLog ('error: ' + $_.Exception.Message) }
  # Sleep, but wake up for a message from your phone.
  $wake = (Get-Date).AddSeconds([int]$cfg.pollSeconds)
  while ((Get-Date) -lt $wake -and -not ($script:remote -and $script:remote.Inbox.Count -gt 0)) { Start-Sleep -Milliseconds 300 }
}
