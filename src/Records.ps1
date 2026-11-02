# Kiro's own session records: %USERPROFILE%\.kiro\sessions\<workspace>\sess_<id>\session.json and messages.jsonl.
# Each turn in messages.jsonl ends with a usage_summary and a turn_end whose stopReason is end_turn (finished),
# cancelled (you pressed stop) or error (cut off: the chat footer then shows the time but no credits).
# Reading these needs no tab switching and does not lag when Kiro is minimized. ASCII only.
$KacSessionsRoot = Join-Path $env:USERPROFILE '.kiro\sessions'

# Every session folder with a record: Id, Dir, Messages (path), Size, Created (when the folder was made).
function Get-KacSessionFiles {
  $out = @()
  if (-not [System.IO.Directory]::Exists($KacSessionsRoot)) { return $out }
  foreach ($wsDir in [System.IO.Directory]::GetDirectories($KacSessionsRoot)) {
    foreach ($dir in [System.IO.Directory]::GetDirectories($wsDir, 'sess_*')) {
      $fi = New-Object System.IO.FileInfo ([System.IO.Path]::Combine($dir, 'messages.jsonl'))
      if (-not $fi.Exists) { continue }
      $out += [pscustomobject]@{ Id = [System.IO.Path]::GetFileName($dir); Dir = $dir; Messages = $fi.FullName; Size = $fi.Length; Modified = $fi.LastWriteTime; Created = [System.IO.Directory]::GetCreationTime($dir) }
    }
  }
  return $out
}
# session.json: Id, Title (what its tab shows), Workspace (folder name, as in the window title), Status.
function Get-KacSessionInfo([string]$dir) {
  try { $j = Get-Content -Raw -Encoding UTF8 -LiteralPath ([System.IO.Path]::Combine($dir, 'session.json')) | ConvertFrom-Json }
  catch { return $null }
  $ws = ''; $wp = @($j.workspacePaths)
  if ($wp.Count -gt 0 -and $wp[0]) { $ws = Split-Path ([string]$wp[0]) -Leaf }
  return [pscustomobject]@{ Id = [string]$j.id; Title = [string]$j.title; Workspace = $ws; Status = [string]$j.status }
}
# Complete lines of $path from byte $from on, at most the last $max bytes of it. End: where the next read starts
# (a line still being written is left for next time).
function Read-KacLines([string]$path, [long]$from = 0, [long]$max = 4194304) {
  $none = [pscustomobject]@{ Lines = @(); End = $from }
  try { $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)) }
  catch { return $none }
  try {
    $len = $fs.Length
    if ($len -lt $from) { $from = 0 }
    $start = [Math]::Max($from, $len - $max)
    $count = [int]($len - $start)
    if ($count -le 0) { return [pscustomobject]@{ Lines = @(); End = $len } }
    $buf = New-Object byte[] $count
    [void]$fs.Seek($start, [System.IO.SeekOrigin]::Begin)
    $n = 0
    while ($n -lt $count) { $got = $fs.Read($buf, $n, $count - $n); if ($got -le 0) { break }; $n += $got }
  } finally { $fs.Dispose() }
  $nl = -1; if ($n -gt 0) { $nl = [Array]::LastIndexOf($buf, [byte]10, $n - 1) }
  if ($nl -lt 0) { return [pscustomobject]@{ Lines = @(); End = $start } }
  $lines = @(([System.Text.Encoding]::UTF8.GetString($buf, 0, $nl)) -split "`n" | ForEach-Object { $_.TrimEnd("`r") })
  if ($start -gt $from -and $lines.Count -gt 0) { $lines = @($lines | Select-Object -Skip 1) }   # began mid-line
  return [pscustomobject]@{ Lines = $lines; End = $start + $nl + 1 }
}

# Lines are matched with regexes, not parsed whole: a line can hold megabytes (images, tool output).
$KacRxHead = [regex]'"payload":\{"type":"(\w+)"'
$KacRxContent = [regex]'"payload":\{"type":"\w+","content":"((?:[^"\\]|\\.)*)"'
$KacRxStop = [regex]'"stopReason":"(\w+)"'
$KacRxStatus = [regex]'"status":"(\w+)"'
$KacRxElapsed = [regex]'"elapsedTime":(\d+)'
$KacRxUsage = [regex]'"usage":([0-9.eE+-]+)'
$KacRxSay = [regex]'"operationType":"Say"'
function ConvertFrom-KacJsonString([string]$line) {
  $c = $KacRxContent.Match($line)
  if (-not $c.Success) { return '' }
  try { return [string](ConvertFrom-Json ('"' + $c.Groups[1].Value + '"')) } catch { return '' }
}
$KacRxTime = [regex]'"timestamp":"([^"]+)"'
$KacRxBoundary = [regex]'"boundarySource":"(\w+)"'
$KacRxErrType = [regex]'"errorType":"(\w+)"'
$KacRxErrText = [regex]'"message":"((?:[^"\\]|\\.)*)"'
# A record timestamp ("2026-11-01T18:57:41.550Z") as a UTC DateTime; $null if there is none.
function ConvertFrom-KacTime([string]$s) {
  if (-not $s) { return $null }
  try { return [DateTime]::Parse($s, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) } catch { return $null }
}
# One line of messages.jsonl, reduced to what the decisions need; $null for the kinds that do not matter.
# At: the line's timestamp (UTC, as written). Recovered: a turn_end Kiro wrote for a turn it lost in a restart
# (boundarySource "recovered", stopReason "cancelled"; see Set-KacRestartCut).
function ConvertTo-KacEntry([string]$line) {
  $m = $KacRxHead.Match($line)
  if (-not $m.Success) { return $null }
  $e = $null
  switch ($m.Groups[1].Value) {
    # Kiro writes a few user lines itself, mid-turn ("syntheticUserMessageReason": max-tokens-recovery,
    # fallback-continuation; 19 of 8969 so far). They are not yours: not the task to quote.
    'user' { if (-not $line.Contains('"syntheticUserMessageReason":"')) { $e = [pscustomobject]@{ Type = 'user'; Text = (ConvertFrom-KacJsonString $line) } } }
    'assistant' { if ($KacRxSay.IsMatch($line)) { $e = [pscustomobject]@{ Type = 'say'; Text = (ConvertFrom-KacJsonString $line) } } }
    'tool_call' { $e = [pscustomobject]@{ Type = 'tool' } }
    'turn_start' { $e = [pscustomobject]@{ Type = 'start' } }
    'turn_end' {
      $s = $KacRxStop.Match($line); $b = $KacRxBoundary.Match($line)
      $why = ''; if ($s.Success) { $why = $s.Groups[1].Value }
      $e = [pscustomobject]@{ Type = 'end'; Stop = $why; Recovered = ($b.Success -and $b.Groups[1].Value -eq 'recovered') }
    }
    'session_metadata' {
      # What the chat showed when a turn failed, as errorType and message (every error turn_end has one).
      if ($line.Contains('"key":"displayError"')) {
        $et = $KacRxErrType.Match($line); $mt = $KacRxErrText.Match($line)
        $etype = ''; if ($et.Success) { $etype = $et.Groups[1].Value }
        $etext = ''; if ($mt.Success) { try { $etext = [string](ConvertFrom-Json ('"' + $mt.Groups[1].Value + '"')) } catch {} }
        $e = [pscustomobject]@{ Type = 'error'; ErrorType = $etype; Text = $etext }
      }
    }
    'usage_summary' {
      $st = $KacRxStatus.Match($line); $el = $KacRxElapsed.Match($line)
      $cr = 0.0; foreach ($u in $KacRxUsage.Matches($line)) { $cr += [double]$u.Groups[1].Value }
      $status = ''; if ($st.Success) { $status = $st.Groups[1].Value }
      $ms = 0; if ($el.Success) { $ms = [long]$el.Groups[1].Value }
      $e = [pscustomobject]@{ Type = 'usage'; Status = $status; Ms = $ms; Credits = $cr }
    }
  }
  if ($e) { $t = $KacRxTime.Match($line); $at = ''; if ($t.Success) { $at = $t.Groups[1].Value }; $e | Add-Member -NotePropertyName At -NotePropertyValue $at }
  return $e
}
# The last turn of a session from its record lines, in the shape Get-KacDecision takes: Users (newest first),
# Ended, Credits (false when it was cut off: stopReason error), Cancelled (you pressed stop), Footer (for the
# log), Final (what it said after its last tool call), ToolCalls, Stop, ErrorType and ErrorText (what the chat
# showed for a failed turn). For Set-KacRestartCut: Recovered, Restart, StartedAt, EndedAt, LastAt (timestamp of
# the turn's last line read).
function Get-KacRecordTurn($lines) {
  $entries = New-Object System.Collections.ArrayList
  foreach ($l in $lines) { if ($l.Length -gt 0) { $e = ConvertTo-KacEntry $l; if ($e) { [void]$entries.Add($e) } } }
  $users = @()
  for ($i = $entries.Count - 1; $i -ge 0 -and $users.Count -lt 30; $i--) { if ($entries[$i].Type -eq 'user') { $users += [string]$entries[$i].Text } }
  $turn = [pscustomobject]@{ Users = $users; Ended = $false; Credits = $false; Cancelled = $false; Footer = ''; Final = ''; ToolCalls = 0; Items = 0
    Stop = ''; ErrorType = ''; ErrorText = ''; Recovered = $false; Restart = $false; StartedAt = ''; EndedAt = ''; LastAt = '' }
  $s = -1; for ($i = $entries.Count - 1; $i -ge 0; $i--) { if ($entries[$i].Type -eq 'start') { $s = $i; break } }
  if ($s -lt 0) { return $turn }
  $turn.StartedAt = $entries[$s].At; $turn.LastAt = $entries[$s].At
  $says = @(); $usage = $null
  for ($i = $s + 1; $i -lt $entries.Count; $i++) {
    $e = $entries[$i]; $turn.Items++
    if ($e.At) { $turn.LastAt = $e.At }
    if ($e.Type -eq 'tool') { $turn.ToolCalls++; $says = @() }
    elseif ($e.Type -eq 'say') { if ($e.Text.Trim()) { $says += $e.Text } }
    elseif ($e.Type -eq 'usage') { $usage = $e }
    elseif ($e.Type -eq 'error') { $turn.ErrorType = $e.ErrorType; $turn.ErrorText = $e.Text }
    elseif ($e.Type -eq 'end') { $turn.Ended = $true; $turn.Stop = $e.Stop; $turn.Recovered = $e.Recovered; $turn.EndedAt = $e.At }
  }
  $turn.Final = ($says -join "`n").Trim()
  # Recovered: Kiro closed it after losing it in a restart. Neither you nor the agent stopped it.
  $turn.Restart = $turn.Recovered
  $turn.Cancelled = ($turn.Stop -eq 'cancelled' -and -not $turn.Recovered)
  $turn.Credits = ($turn.Ended -and $turn.Stop -ne 'error' -and -not $turn.Recovered)
  $parts = @($turn.Stop)
  if ($turn.Stop -eq 'error' -and $turn.ErrorType) { $parts = @('error (' + $turn.ErrorType + ')') }
  if ($turn.Recovered) { $parts += 'recovered' }
  if ($usage) { $parts += @($usage.Status, ('{0:0.##} credits' -f $usage.Credits), ('{0:0}s' -f ($usage.Ms / 1000))) }
  $turn.Footer = (($parts | Where-Object { $_ }) -join ', ')
  return $turn
}

# The last turn of a session. Reads back from the end of its record, four times further each round (up to
# 64 MB), until a user message turns up: big tool output can push the last one out of a 4 MB tail.
function Read-KacSessionTurn([string]$dir, [long]$tail = 4194304) {
  $path = [System.IO.Path]::Combine($dir, 'messages.jsonl')
  $size = 0; try { $size = (New-Object System.IO.FileInfo $path).Length } catch {}
  $n = [Math]::Max([long]65536, $tail)
  while ($true) {
    $turn = Get-KacRecordTurn (Read-KacLines $path 0 $n).Lines
    if (@($turn.Users).Count -gt 0 -or $n -ge $size -or $n -ge 67108864) { return $turn }
    $n = $n * 4
  }
}

# Is any session working? Some record written in the last $minutes (Get-KacSessionFiles gives $files). A sub-agent
# writes to sess_<id>\sub-executions\*.jsonl and leaves messages.jsonl alone for as long as it runs (20 minutes and
# more, 11-02), so those count too, in the sessions written in the last 12 hours.
function Test-KacAnyWorking($files, [double]$minutes) {
  $since = (Get-Date).AddMinutes(-$minutes)
  foreach ($f in $files) { if ($f.Modified -ge $since) { return $true } }
  $day = (Get-Date).AddHours(-12)
  foreach ($f in $files) {
    if ($f.Modified -lt $day) { continue }
    $sub = [System.IO.Path]::Combine($f.Dir, 'sub-executions')
    if (-not [System.IO.Directory]::Exists($sub)) { continue }
    foreach ($s in [System.IO.Directory]::GetFiles($sub, '*.jsonl')) { if ([System.IO.File]::GetLastWriteTime($s) -ge $since) { return $true } }
  }
  return $false
}

# When Kiro's main process (its oldest Kiro.exe) started, in local time; $null while Kiro is not running. When it
# runs but no start time can be read (Kiro run as administrator, say): [DateTime]::MinValue, "up long ago".
function Get-KacKiroStart {
  $ps = @(Get-Process -Name Kiro -ErrorAction SilentlyContinue)
  if ($ps.Count -eq 0) { return $null }
  $times = @(foreach ($p in $ps) { try { $t = $p.StartTime; if ($t) { $t } } catch {} })
  if ($times.Count -eq 0) { return [DateTime]::MinValue }
  return [DateTime]($times | Sort-Object | Select-Object -First 1)
}
# Should this turn be continued now that Kiro has restarted? $kiroStart: when Kiro's current process started.
# From the records of 11-02, when the credits ran out and Kiro was restarted (up again at 02:56:11):
#  * The turns that asked the model for something got an error (OverageLimitReachedError, 02:54:33-02:55:00).
#    An error counts if it came at most $windowMinutes before Kiro started; one of the $limitErrors types (out
#    of credits) however long before: that turn was waiting for the credits, and the restart (signing in again,
#    for example) may be what brought them back.
#  * A turn busy with a tool got nothing and stays open in the record. Kiro closes such a turn only when that
#    session is used again, a few seconds before the next message (a turn_end with boundarySource "recovered",
#    stopReason "cancelled"; every such line in the history came 2-20 s before someone typed "continue"). So an
#    open turn counts when its last line is older than Kiro's start: nothing can be running it.
#  * That recovered turn_end itself (Restart is already set by Get-KacRecordTurn).
# If so, marks the turn (Restart; an open one also Ended, without credits) and returns $true.
function Set-KacRestartCut($turn, [double]$windowMinutes, $kiroStart, $limitErrors = @()) {
  if ($turn.Restart) { return $true }
  if ($null -eq $kiroStart -or $kiroStart -eq [DateTime]::MinValue) { return $false }
  $ks = ([DateTime]$kiroStart).ToUniversalTime()
  if (-not $turn.Ended) {
    $last = ConvertFrom-KacTime $turn.LastAt
    if ($null -eq $last -or $last.ToUniversalTime() -ge $ks) { return $false }
    $turn.Ended = $true; $turn.Restart = $true; $turn.Credits = $false; $turn.Cancelled = $false; $turn.Footer = 'no turn_end'
    return $true
  }
  if ($turn.Stop -ne 'error') { return $false }
  $end = ConvertFrom-KacTime $turn.EndedAt
  if ($null -eq $end) { return $false }
  $gap = ($ks - $end.ToUniversalTime()).TotalMinutes
  $limit = $turn.ErrorType -and (@($limitErrors) -contains $turn.ErrorType)
  if ($gap -lt 0 -or ($gap -gt $windowMinutes -and -not $limit)) { return $false }
  $turn.Restart = $true
  return $true
}
