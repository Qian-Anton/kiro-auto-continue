# The morning status: what the watcher did overnight, counted from its own log. ASCII only.

# Counts per session name ("workspace | title", as the log writes it) in the log lines written since $since:
# Resumes (continue messages sent), Pushes (until-done and queued messages sent), Needs (it said NEEDS YOU),
# Done (until-done finished). The log has no year: dates after $now belong to the year before.
function Get-KacLogCounts($lines, [DateTime]$since, [DateTime]$now) {
  $out = [pscustomobject]@{ Resumes = @{}; Pushes = @{}; Needs = @{}; Done = @{} }
  $rx = [regex]'^(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)  (.+?) : (.*)$'
  foreach ($l in @($lines)) {
    $m = $rx.Match([string]$l)
    if (-not $m.Success) { continue }
    $g = $m.Groups
    try { $at = New-Object DateTime ($now.Year, [int]$g[1].Value, [int]$g[2].Value, [int]$g[3].Value, [int]$g[4].Value, [int]$g[5].Value) } catch { continue }
    if ($at -gt $now.AddMinutes(1)) { $at = $at.AddYears(-1) }
    if ($at -lt $since) { continue }
    $name = $g[6].Value; $rest = $g[7].Value
    if ($rest -match '; sent resume\b') { $out.Resumes[$name] = 1 + [int]$out.Resumes[$name] }
    elseif ($rest -match '; sent (contract|push|pushGoal|verify|queued)\b') { $out.Pushes[$name] = 1 + [int]$out.Pushes[$name] }
    if ($rest.StartsWith('NEEDS YOU - ')) { $out.Needs[$name] = 1 }
    if ($rest.StartsWith('DONE - ')) { $out.Done[$name] = 1 }
  }
  return $out
}
# Sum of a counts table.
function Get-KacTotal($table) { $n = 0; foreach ($v in @($table.Values)) { $n += [int]$v }; return $n }
# When today's report is due: $time ("08:00") today; $null when reports are off or the time cannot be read.
function Get-KacReportDue([string]$time, [DateTime]$now) {
  if (-not $time) { return $null }
  $m = [regex]::Match($time, '^\s*(\d{1,2}):(\d\d)\s*$')
  if (-not $m.Success) { return $null }
  $h = [int]$m.Groups[1].Value; $mi = [int]$m.Groups[2].Value
  if ($h -gt 23 -or $mi -gt 59) { return $null }
  return $now.Date.AddHours($h).AddMinutes($mi)
}
