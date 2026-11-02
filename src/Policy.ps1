# What to do when a session stops. No UI calls in here: tests/Policy.Tests.ps1 drives these with made-up turns.
# ASCII only; every word the agent sees (templates, marks) lives in config.default.json.

function Get-KacShort([string]$s, [int]$n = 60) {
  $s = ($s -replace '\s+', ' ').Trim()
  if ($s.Length -gt $n) { return $s.Substring(0, $n) + '...' }
  return $s
}
# Messages this tool sent start with one of autoPrefixes.
function Test-KacAuto([string]$text, $cfg) {
  $t = $text.TrimStart()
  foreach ($p in @($cfg.autoPrefixes)) { if ($p -and $t.StartsWith([string]$p)) { return $true } }
  return $false
}
# A bare "continue" (or an empty message) carries no task.
function Test-KacBare([string]$text, $cfg) {
  $t = ($text -replace '[\s\p{P}]', '').ToLowerInvariant()
  if ($t -eq '') { return $true }
  foreach ($b in @($cfg.bareContinue)) { if ($t -eq (([string]$b) -replace '[\s\p{P}]', '').ToLowerInvariant()) { return $true } }
  return $false
}
# Index in $users (newest first) of the newest message that is neither ours nor a bare continue; -1 if none.
function Find-KacTaskIndex($users, $cfg) {
  $list = @($users)
  for ($i = 0; $i -lt $list.Count; $i++) {
    $u = [string]$list[$i]
    if (-not (Test-KacAuto $u $cfg) -and -not (Test-KacBare $u $cfg)) { return $i }
  }
  return -1
}
function Get-KacLang([string]$text, $cfg) {
  if ($cfg.language -and [string]$cfg.language -ne 'auto') { return [string]$cfg.language }
  if ($text -match '[\u3400-\u9fff]') { return 'zh' }
  return 'en'
}
function Get-KacWords($cfg, [string]$lang) {
  $w = $cfg.words.$lang
  if ($null -eq $w) { $w = $cfg.words.en }
  return $w
}
# All marks of one kind (trigger, done, stuck) across languages: a Chinese task may still end with [DONE].
function Get-KacMarks($cfg, [string]$kind) {
  $out = @()
  foreach ($p in $cfg.words.PSObject.Properties) { $m = $p.Value.marks.$kind; if ($m) { $out += [string]$m } }
  return $out
}
function Find-KacMark([string]$text, $marks) {
  foreach ($m in @($marks)) { if ($m -and $text.Contains([string]$m)) { return [string]$m } }
  return $null
}
# A template is a string or an array of lines; {name} is replaced from $vars.
function Format-KacText($tpl, $vars) {
  $s = (@($tpl) -join "`n")
  foreach ($k in $vars.Keys) { $s = $s.Replace('{' + $k + '}', [string]$vars[$k]) }
  return $s
}
# The last $n non-empty lines of a text.
function Get-KacTail([string]$text, [int]$n = 2) {
  $lines = @(($text -split '\r?\n') | Where-Object { $_.Trim() })
  if ($lines.Count -eq 0) { return '' }
  return ((@($lines) | Select-Object -Last $n) -join "`n")
}
# Code: for stop and done, what happened (the notice's headline, words.<lang>.notice).
function New-KacDecision([string]$action, [string]$why, $set, [string]$kind = '', [string]$text = '', [int]$after = 0, [string]$code = '') {
  if ($null -eq $set) { $set = @{} }
  return [pscustomobject]@{ Action = $action; Why = $why; Set = $set; Kind = $kind; Text = $text; After = $after; Code = $code }
}

# $turn: what Get-KacRecordTurn read (Users newest first, Ended, Credits, Cancelled, Footer, Final, ToolCalls,
# Restart: cut short by a Kiro restart, see Set-KacRestartCut).
# $st: this session's state, @{ Tries = 0; Mode = $null }.
# Action none: leave it. wait: look again later. stop: it needs you (logged). done: until-done finished (logged).
# send: type Text, no sooner than
# After seconds after the stop. Set: state changes to apply (for send, only once the message has gone out).
function Get-KacDecision($turn, $st, $cfg) {
  $ud = $cfg.untilDone
  if (-not $turn.Ended) { return New-KacDecision 'wait' 'the last turn has no footer yet' $null }
  $cancelIsCut = [bool]$cfg.testing.cancelIsInterrupt
  if ($turn.Cancelled -and -not $cancelIsCut) {
    return New-KacDecision 'none' ('stopped by you [{0}]' -f $turn.Footer) @{ Tries = 0; Mode = $null }
  }
  $users = @($turn.Users)
  $ti = Find-KacTaskIndex $users $cfg
  $task = $null; if ($ti -ge 0) { $task = [string]$users[$ti] }
  $lastUser = ''; if ($users.Count -gt 0) { $lastUser = [string]$users[0] }
  $langFrom = $lastUser; if ($task) { $langFrom = $task }
  $w = Get-KacWords $cfg (Get-KacLang $langFrom $cfg)
  $vars = @{ task = $task; goal = ''; done = $w.marks.done; stuck = $w.marks.stuck }

  # Interrupted: the footer shows only the elapsed time, no credits.
  if (-not $turn.Credits -or $turn.Cancelled) {
    # Out of credits: trying again right away cannot work. It is continued once Kiro restarts (Set-KacRestartCut
    # sets Restart), or once some session finishes a turn normally again, which shows the credits are back (the
    # watcher then sets CreditsBack).
    if (-not $turn.Restart -and -not $turn.CreditsBack -and $turn.ErrorType -and (@($cfg.resume.limitErrors) -contains $turn.ErrorType)) {
      return New-KacDecision 'stop' ('out of credits [{0}]; it continues once the credits are back or Kiro restarts' -f $turn.Footer) $null -code 'outOfCredits'
    }
    $n = [int]$st.Tries
    if ($n -ge [int]$cfg.resume.maxTries) {
      return New-KacDecision 'stop' ('interrupted again after {0} continues [{1}]' -f $n, $turn.Footer) $null -code 'gaveUp'
    }
    $delays = @($cfg.resume.delaysSeconds)
    $after = [int]$delays[[Math]::Min($n, $delays.Count - 1)]
    $tpl = $w.resumeNoTask; if ($task) { $tpl = $w.resume }
    $what = 'interrupted'
    if ($turn.Restart) { $tpl = $w.restartNoTask; if ($task) { $tpl = $w.restart }; $what = 'cut short by a Kiro restart' }
    return New-KacDecision 'send' ('{0} [{1}]' -f $what, $turn.Footer) @{ Tries = $n + 1 } 'resume' (Format-KacText $tpl $vars) $after
  }

  # Finished normally. Until-done starts when your newest real message carries the trigger mark.
  $set = @{ Tries = 0 }
  $mode = $st.Mode
  if ($null -ne $mode -and $task -and $task -ne $mode.Source) { $mode = $null; $set.Mode = $null }  # you wrote something new
  if ($null -eq $mode) {
    $trig = Get-KacMarks $cfg 'trigger'
    if (-not $ud.enabled -or -not $task -or -not (Find-KacMark $task $trig)) {
      return New-KacDecision 'none' ('finished [{0}]' -f $turn.Footer) $set
    }
    $goal = $task; foreach ($m in $trig) { $goal = $goal.Replace($m, '') }
    # After a restart of this tool, count the messages it already sent since the trigger.
    $pushed = 0; for ($i = 0; $i -lt $ti; $i++) { if (Test-KacAuto ([string]$users[$i]) $cfg) { $pushed++ } }
    $mode = @{ Source = $task; Goal = $goal.Trim(); Pushes = $pushed; Verifying = $false; Idle = 0 }
  }
  $vars.goal = $mode.Goal
  $tail = Get-KacTail ([string]$turn.Final) 2
  $stuck = Find-KacMark $tail (Get-KacMarks $cfg 'stuck')
  if ($stuck) {
    $set.Mode = $null
    $why = $tail.Substring($tail.IndexOf($stuck) + $stuck.Length).Trim()
    return New-KacDecision 'stop' ('until-done: stuck, needs you: {0}' -f (Get-KacShort $why 160)) $set -code 'stuck'
  }
  if (Find-KacMark $tail (Get-KacMarks $cfg 'done')) {
    if ($ud.verifyOnDone -and -not $mode.Verifying) {
      $m2 = $mode.Clone(); $m2.Verifying = $true; $m2.Pushes = [int]$mode.Pushes + 1; $set.Mode = $m2
      return New-KacDecision 'send' 'until-done: says done; asking for one check against the goal' $set 'verify' (Format-KacText $w.verify $vars)
    }
    $set.Mode = $null
    return New-KacDecision 'done' ('until-done: done after {0} pushes' -f $mode.Pushes) $set -code 'done'
  }
  # Stopped without a mark (a question, "shall I go on?", a half-way report): push, unless pushing stopped helping.
  $idle = 0
  $noWork = ([int]$turn.ToolCalls -eq 0) -and (([string]$turn.Final).Length -lt [int]$ud.idleChars)
  if ((Test-KacAuto $lastUser $cfg) -and $noWork) { $idle = [int]$mode.Idle + 1 }
  if ($idle -ge [int]$ud.maxIdlePushes) {
    $set.Mode = $null
    return New-KacDecision 'stop' ('until-done: {0} pushes in a row did no work' -f $idle) $set -code 'idle'
  }
  if ([int]$mode.Pushes -ge [int]$ud.maxPushes) {
    $set.Mode = $null
    return New-KacDecision 'stop' ('until-done: reached {0} pushes' -f $ud.maxPushes) $set -code 'maxPushes'
  }
  $kind = 'push'
  if ([int]$mode.Pushes -eq 0) { $kind = 'contract' }
  elseif (([int]$mode.Pushes % [int]$ud.goalEvery) -eq 0) { $kind = 'pushGoal' }
  $m2 = $mode.Clone(); $m2.Pushes = [int]$mode.Pushes + 1; $m2.Idle = $idle; $set.Mode = $m2
  $why = 'until-done: stopped without a mark, push {0}/{1}' -f $m2.Pushes, $ud.maxPushes
  return New-KacDecision 'send' $why $set $kind (Format-KacText $w.$kind $vars)
}

# What "continue N" from your phone types: after a cut-off or a Kiro restart, the continue message that quotes your
# task; after a normal finish or your own stop, a plain "continue" (words.<lang>.remote.continueText). $null while
# the turn still runs.
function Get-KacManualContinue($turn, $cfg) {
  if (-not $turn.Ended) { return $null }
  $users = @($turn.Users)
  $ti = Find-KacTaskIndex $users $cfg
  $task = $null; if ($ti -ge 0) { $task = [string]$users[$ti] }
  $langFrom = ''; if ($users.Count -gt 0) { $langFrom = [string]$users[0] }
  if ($task) { $langFrom = $task }
  $w = Get-KacWords $cfg (Get-KacLang $langFrom $cfg)
  $vars = @{ task = $task }
  if ($turn.Restart) { $tpl = $w.restartNoTask; if ($task) { $tpl = $w.restart }; return (Format-KacText $tpl $vars) }
  if (-not $turn.Credits -and -not $turn.Cancelled) { $tpl = $w.resumeNoTask; if ($task) { $tpl = $w.resume }; return (Format-KacText $tpl $vars) }
  return [string]$w.remote.continueText
}
