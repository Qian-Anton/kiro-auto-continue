# Commands from your phone through a WeCom smart bot (API mode, long connection). The watcher keeps one WebSocket
# open to wss://openws.work.weixin.qq.com on a background runspace; you chat with the bot.
# The protocol, as WeCom's own aibot SDKs speak it (11-02): JSON text frames { cmd, headers: { req_id }, body }.
# Right after connecting: aibot_subscribe with body { bot_id, secret }, answered by a frame with the same req_id and
# errcode 0. Then a ping every 30 s, answered the same way. Messages to the bot arrive as aibot_msg_callback; a reply
# is aibot_respond_msg with the callback's req_id; aibot_send_msg sends a message on the bot's own (chatid: the
# user id, for a single chat). WeCom keeps one connection per bot: a new one takes over.
# Needs Config.ps1, Records.ps1 and Policy.ps1. ASCII only: every word you type or read is in config.default.json
# (words.<lang>.remote).

function New-KacReqId([string]$prefix) {
  return ('{0}_{1}_{2}' -f $prefix, [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), [guid]::NewGuid().ToString('N').Substring(0, 8))
}
# A reply to the message whose callback carried $reqId: a stream message $streamId showing $content. A frame with
# $finish false is replaced by the next one with the same stream id.
function New-KacReplyFrame([string]$reqId, [string]$streamId, [string]$content, [bool]$finish) {
  $f = [ordered]@{ cmd = 'aibot_respond_msg'; headers = [ordered]@{ req_id = $reqId }
    body = [ordered]@{ msgtype = 'stream'; stream = [ordered]@{ id = $streamId; finish = $finish; content = $content } } }
  return (ConvertTo-Json -Compress -Depth 6 $f)
}
# A message the bot sends on its own, not as a reply, to $chatid (in a single chat, the user id): markdown.
function New-KacPushFrame([string]$chatid, [string]$content) {
  $f = [ordered]@{ cmd = 'aibot_send_msg'; headers = [ordered]@{ req_id = (New-KacReqId 'aibot_send_msg') }
    body = [ordered]@{ chatid = $chatid; msgtype = 'markdown'; markdown = [ordered]@{ content = $content } } }
  return (ConvertTo-Json -Compress -Depth 6 $f)
}
# What you wrote: a text message, the transcript of a voice message, or the text parts of a mixed message; '' for
# an image or a file. An @mention in front (group chats) is dropped; full-width digits, spaces and question marks
# become plain ones.
function Get-KacRemoteText($body) {
  $t = ''
  if ($body.msgtype -eq 'text') { $t = [string]$body.text.content }
  elseif ($body.msgtype -eq 'voice') { $t = [string]$body.voice.content }
  elseif ($body.msgtype -eq 'mixed') {
    $parts = @()
    foreach ($it in @($body.mixed.msg_item)) { if ($it.msgtype -eq 'text' -and $it.text.content) { $parts += [string]$it.text.content } }
    $t = $parts -join "`n"
  }
  $t = $t -replace '^\s*(@\S+\s*)+', ''
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $t.ToCharArray()) {
    $c = [int]$ch
    if ($c -ge 0xFF10 -and $c -le 0xFF19) { [void]$sb.Append([char]($c - 0xFF10 + 0x30)) }
    elseif ($c -eq 0x3000) { [void]$sb.Append(' ') }
    elseif ($c -eq 0xFF1F) { [void]$sb.Append('?') }
    else { [void]$sb.Append($ch) }
  }
  return $sb.ToString().Trim()
}
# The words of one command in every language (words.<lang>.remote.cmd.<kind>), as a regex alternation.
function Get-KacRemoteAlt($cfg, [string]$kind) {
  $all = @()
  foreach ($p in $cfg.words.PSObject.Properties) { $all += @($p.Value.remote.cmd.$kind | Where-Object { $_ }) }
  return ((@($all) | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape([string]$_) }) -join '|')
}
# Your message as a command. Kind: status, help, continue / stop / clear (N; 0 when no number was given), send
# (N, Text), new (Text: the workspace and the message), pair (Code), or unknown. Sending needs a space, a colon or the
# number word ("2 ...", "2: ...") between the number and the text, and a new session a space or a colon after the
# word, so a message that merely starts with a digit or with that word is not typed anywhere.
function ConvertTo-KacRemoteCommand([string]$text, $cfg) {
  $t = ([string]$text).Trim()
  $none = [pscustomobject]@{ Kind = 'unknown'; N = 0; Text = ''; Code = '' }
  if (-not $t) { return $none }
  $opt = [Text.RegularExpressions.RegexOptions]'IgnoreCase, Singleline'
  $end = '[\s\p{P}]*$'
  $hao = Get-KacRemoteAlt $cfg 'number'
  $suffix = ''; if ($hao) { $suffix = '(?:' + $hao + ')?' }
  foreach ($k in 'status', 'help') {
    $a = Get-KacRemoteAlt $cfg $k
    if ($a -and [regex]::IsMatch($t, ('^(?:' + $a + ')' + $end), $opt)) { return [pscustomobject]@{ Kind = $k; N = 0; Text = ''; Code = '' } }
  }
  $a = Get-KacRemoteAlt $cfg 'pair'
  if ($a) {
    $m = [regex]::Match($t, ('^(?:' + $a + ')\s*(\d{4,8})' + $end), $opt)
    if ($m.Success) { return [pscustomobject]@{ Kind = 'pair'; N = 0; Text = ''; Code = $m.Groups[1].Value } }
  }
  foreach ($k in 'continue', 'stop', 'clear') {
    $a = Get-KacRemoteAlt $cfg $k
    if (-not $a) { continue }
    $m = [regex]::Match($t, ('^(?:' + $a + ')\s*(\d+)?\s*' + $suffix + $end), $opt)
    if ($m.Success) {
      $n = 0; if ($m.Groups[1].Success) { $n = [int]$m.Groups[1].Value }
      return [pscustomobject]@{ Kind = $k; N = $n; Text = ''; Code = '' }
    }
  }
  $a = Get-KacRemoteAlt $cfg 'new'
  if ($a) {
    $m = [regex]::Match($t, ('^(?:' + $a + ')(?:\s+|\s*[:\uFF1A]\s*)(\S.*)$'), $opt)
    if ($m.Success) { return [pscustomobject]@{ Kind = 'new'; N = 0; Text = $m.Groups[1].Value.Trim(); Code = '' } }
  }
  $sep = '[\s:,;\u3001\uFF0C\uFF1A\uFF1B]'
  $gap = $sep + '+'; if ($hao) { $gap = '(?:\s*(?:' + $hao + ')' + $sep + '*|' + $sep + '+)' }
  $a = Get-KacRemoteAlt $cfg 'send'
  $pre = ''; if ($a) { $pre = '(?:(?:' + $a + ')\s*)?' }
  $m = [regex]::Match($t, ('^' + $pre + '(?>(\d+))' + $gap + '(\S.*)$'), $opt)
  if ($m.Success) { return [pscustomobject]@{ Kind = 'send'; N = [int]$m.Groups[1].Value; Text = $m.Groups[2].Value.Trim(); Code = '' } }
  return $none
}
# The workspace a "new" command names, and the message after it. $workspaces: the open Kiro windows' workspace
# names. The first word picks one when it is a name, or the start of exactly one name (any case). With one window
# open the name may be left out. Workspace '' when it cannot tell.
function Split-KacNewCommand([string]$text, $workspaces) {
  $ws = @($workspaces | Where-Object { $_ })
  $m = [regex]::Match($text, '^(\S+)(?:\s+(\S.*))?$', 'Singleline')
  if ($m.Success) {
    $word = $m.Groups[1].Value; $rest = ''; if ($m.Groups[2].Success) { $rest = $m.Groups[2].Value.Trim() }
    $hit = @($ws | Where-Object { $_ -ieq $word })
    if ($hit.Count -eq 0) { $hit = @($ws | Where-Object { $_.StartsWith($word, [StringComparison]::OrdinalIgnoreCase) }) }
    if ($hit.Count -eq 1) { return [pscustomobject]@{ Workspace = [string]$hit[0]; Text = $rest } }
  }
  if ($ws.Count -eq 1) { return [pscustomobject]@{ Workspace = [string]$ws[0]; Text = $text.Trim() } }
  return [pscustomobject]@{ Workspace = ''; Text = $text.Trim() }
}
# How a session's last turn stands, for the status reply ($rw: words.<lang>.remote).
function Get-KacTurnState($turn, $rw, $now = $null) {
  if ($null -eq $now) { $now = Get-Date }
  if (-not $turn.Ended) { return [string]$rw.state.running }
  if ($turn.Restart) { return [string]$rw.state.restart }
  $at = ''
  $end = ConvertFrom-KacTime ([string]$turn.EndedAt)
  if ($end) { $l = $end.ToLocalTime(); if ($l.Date -eq ([DateTime]$now).Date) { $at = $l.ToString('HH:mm') } else { $at = $l.ToString('MM-dd HH:mm') } }
  $k = 'done'
  if ($turn.Stop -eq 'error') { $k = 'cut' } elseif ($turn.Cancelled) { $k = 'stopped' }
  return (Format-KacText $rw.state.$k @{ at = $at }).Trim()
}

# The connection, on its own runspace so that heartbeats go on while the watcher types into Kiro. $Shared:
# Inbox (callback frames, as text), Outbox (frames to send), Log (lines for the watcher's log), State, Stop,
# AuthFailed, PingNow, LastPong. It goes through the system proxy, and when connecting fails, every other try
# connects directly. Never logs the secret.
$KacRemoteLoop = {
  param($Shared, [string]$BotId, [string]$Secret, [string]$Url)
  $ErrorActionPreference = 'Stop'
  try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
  $none = [Threading.CancellationToken]::None
  function Say([string]$s) { $Shared.Log.Enqueue($s) }
  function NewId([string]$p) { return ('{0}_{1}_{2}' -f $p, [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), [guid]::NewGuid().ToString('N').Substring(0, 8)) }
  function SendFrame($sock, [string]$json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $bytes)
    if (-not $sock.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(15000)) { throw 'sending timed out' }
  }
  $failures = 0; $drops = New-Object System.Collections.ArrayList
  while (-not $Shared.Stop) {
    $sock = $null; $online = $false; $reason = ''; $direct = (($failures % 2) -eq 1)
    try {
      $Shared.State = 'connecting'
      $sock = New-Object System.Net.WebSockets.ClientWebSocket
      $sock.Options.KeepAliveInterval = [TimeSpan]::FromSeconds(30)
      if ($direct) { $sock.Options.Proxy = $null }
      if (-not $sock.ConnectAsync([Uri]$Url, $none).Wait(20000)) { throw 'connecting timed out' }
      $sub = NewId 'aibot_subscribe'
      SendFrame $sock (ConvertTo-Json -Compress @{ cmd = 'aibot_subscribe'; headers = @{ req_id = $sub }; body = @{ bot_id = $BotId; secret = $Secret } })
      $asked = Get-Date; $lastPing = Get-Date; $missed = 0
      $buf = New-Object byte[] 131072
      $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList (, $buf)
      $acc = New-Object System.IO.MemoryStream
      $recv = $sock.ReceiveAsync($seg, $none)
      while (-not $Shared.Stop) {
        if ($recv.IsCompleted) {
          $r = $recv.Result
          if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw ('WeCom closed the connection ({0} {1})' -f $sock.CloseStatus, $sock.CloseStatusDescription) }
          $acc.Write($buf, 0, $r.Count)
          if ($r.EndOfMessage) {
            $raw = [Text.Encoding]::UTF8.GetString($acc.ToArray()); [void]$acc.SetLength(0)
            $f = $null; try { $f = ConvertFrom-Json $raw } catch { Say ('unreadable frame: ' + $raw.Substring(0, [Math]::Min(100, $raw.Length))) }
            if ($null -ne $f) {
              $cmd = [string]$f.cmd; $rid = [string]$f.headers.req_id
              if ($cmd -eq 'aibot_msg_callback' -or $cmd -eq 'aibot_event_callback') { $Shared.Inbox.Enqueue($raw) }
              elseif ($rid -eq $sub) {
                if ([int]$f.errcode -ne 0) { $Shared.AuthFailed = $true; throw ('WeCom refused the bot id and secret: errcode {0}, {1}' -f $f.errcode, $f.errmsg) }
                $online = $true; $failures = 0; $Shared.AuthFailed = $false; $Shared.State = 'online'
                $how = ''; if ($direct) { $how = ' (connected directly, not through the system proxy)' }
                Say ('online' + $how)
              }
              elseif ($rid.StartsWith('ping')) { $missed = 0; $Shared.LastPong = Get-Date }
              elseif ($null -ne $f.errcode -and [int]$f.errcode -ne 0) {
                $what = 'a reply'; if ($rid.StartsWith('aibot_send_msg')) { $what = 'a message' }
                Say ('WeCom refused {0}: errcode {1}, {2}' -f $what, $f.errcode, $f.errmsg)
              }
              elseif ($cmd) { Say ('frame ' + $cmd) }
            }
          }
          $recv = $sock.ReceiveAsync($seg, $none)
          continue
        }
        if (-not $online) {
          if (((Get-Date) - $asked).TotalSeconds -gt 20) { throw 'no answer to subscribing' }
        } else {
          $o = $null
          while ($Shared.Outbox.TryDequeue([ref]$o)) { SendFrame $sock $o }
          if ($Shared.PingNow -or ((Get-Date) - $lastPing).TotalSeconds -ge 30) {
            if ($missed -ge 2) { throw 'no answer to two heartbeats' }
            $Shared.PingNow = $false
            SendFrame $sock (ConvertTo-Json -Compress @{ cmd = 'ping'; headers = @{ req_id = (NewId 'ping') } })
            $missed++; $lastPing = Get-Date
          }
        }
        Start-Sleep -Milliseconds 200
      }
    } catch {
      $m = $_.Exception; while ($null -ne $m.InnerException) { $m = $m.InnerException }
      $reason = [string]$m.Message
    } finally {
      if ($null -ne $sock) { try { $sock.Dispose() } catch {} }
      $Shared.State = 'offline'
    }
    if ($Shared.Stop) { break }
    if ($online) { [void]$drops.Add((Get-Date)) } else { $failures++ }
    while ($drops.Count -gt 0 -and ((Get-Date) - [DateTime]$drops[0]).TotalMinutes -gt 10) { $drops.RemoveAt(0) }
    if ($Shared.AuthFailed) { $wait = 600 }
    elseif ($drops.Count -ge 3) { $wait = 600; $drops.Clear(); $reason += '; it keeps dropping: is another program connected as this bot? WeCom keeps one connection per bot' }
    elseif ($online) { $wait = 2 }
    else { $wait = [int][Math]::Min(60, [Math]::Pow(2, [Math]::Min($failures, 6))) }
    Say ('offline: {0}; trying again in {1} s' -f $reason, $wait)
    $until = (Get-Date).AddSeconds($wait)
    while (-not $Shared.Stop -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
  }
  $Shared.State = 'stopped'
}

# Starts the connection with remote.botId, remote.secret and remote.url; returns $Shared (see above), plus Shell
# (the PowerShell that runs it).
function Start-KacRemote($cfg) {
  $shared = [hashtable]::Synchronized(@{})
  $shared.Inbox = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
  $shared.Outbox = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
  $shared.Log = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
  $shared.State = 'starting'; $shared.Stop = $false; $shared.AuthFailed = $false; $shared.PingNow = $false
  $shared.LastPong = [DateTime]::MinValue
  $url = [string]$cfg.remote.url; if (-not $url) { $url = 'wss://openws.work.weixin.qq.com' }
  $ps = [PowerShell]::Create()
  [void]$ps.AddScript($KacRemoteLoop.ToString()).AddArgument($shared).AddArgument([string]$cfg.remote.botId).AddArgument([string]$cfg.remote.secret).AddArgument($url)
  $shared.Shell = $ps
  $shared.Job = $ps.BeginInvoke()
  return $shared
}
