# Short notices to your phone through a group bot's webhook: WeCom (qyapi.weixin.qq.com: in a group, ... > Message
# push > Add) or Feishu / Lark (open.feishu.cn, open.larksuite.com: a custom bot; with signature verification on,
# put its secret in notify.secret). Anyone holding the webhook can post to that group: keep it in config.json,
# which stays out of git, and out of the log. Needs Policy.ps1 (Get-KacWords). ASCII only.

function Get-KacNotifyKind([string]$url) {
  if ($url -match '^https://qyapi\.weixin\.qq\.com/') { return 'wecom' }
  if ($url -match '^https://open\.(feishu\.cn|larksuite\.com)/') { return 'feishu' }
  return ''
}
# The JSON to post. A Feishu bot with signature verification also wants timestamp (seconds) and sign: base64 of
# HMAC-SHA256 keyed with "<timestamp>`n<secret>" over an empty message.
function New-KacNotifyBody([string]$kind, [string]$text, [string]$secret = '', [long]$now = 0) {
  if ($kind -eq 'wecom') { return (ConvertTo-Json -Compress -Depth 4 ([ordered]@{ msgtype = 'text'; text = [ordered]@{ content = $text } })) }
  $body = [ordered]@{}
  if ($secret) {
    if ($now -le 0) { $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
    $mac = New-Object System.Security.Cryptography.HMACSHA256 (, [Text.Encoding]::UTF8.GetBytes(('{0}' -f $now) + "`n" + $secret))
    $body.timestamp = [string]$now
    $body.sign = [Convert]::ToBase64String($mac.ComputeHash([byte[]]@()))
  }
  $body.msg_type = 'text'
  $body.content = [ordered]@{ text = $text }
  return (ConvertTo-Json -Compress -Depth 4 $body)
}
# The language of the notices: the language setting, or with "auto" the language Windows shows.
function Get-KacNoticeLang($cfg) {
  if ($cfg.language -and [string]$cfg.language -ne 'auto') { return [string]$cfg.language }
  if ((Get-UICulture).Name -like 'zh*') { return 'zh' }
  return 'en'
}
# A notice: the headline for $code (words.<lang>.notice), the session, the detail, the time.
function Format-KacNotice($cfg, [string]$code, [string]$name, [string]$detail, $when = $null) {
  if ($null -eq $when) { $when = Get-Date }
  $w = Get-KacWords $cfg (Get-KacNoticeLang $cfg)
  $head = [string]$w.notice.$code; if (-not $head) { $head = $code }
  $lines = @(([string]$w.notice.prefix) + $head)
  foreach ($x in @($name, $detail)) { if ($x) { $lines += $x } }
  $lines += ('{0:MM-dd HH:mm}' -f $when)
  return ($lines -join "`n")
}
# Post $text to notify.webhook. '' once it went out, otherwise why not (never the webhook itself).
function Send-KacNotice($cfg, [string]$text) {
  $url = [string]$cfg.notify.webhook
  if (-not $url) { return 'no webhook in notify.webhook' }
  $kind = Get-KacNotifyKind $url
  if (-not $kind) { return 'notify.webhook is neither a WeCom nor a Feishu webhook' }
  $text = Limit-KacUtf8 $text 2000   # WeCom takes at most 2048 bytes of text
  $json = New-KacNotifyBody $kind $text ([string]$cfg.notify.secret)
  $timeout = [int]$cfg.notify.timeoutSeconds; if ($timeout -le 0) { $timeout = 10 }
  try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    # Windows PowerShell would send a string body as ISO-8859-1: send the UTF-8 bytes.
    $r = Invoke-RestMethod -Method Post -Uri $url -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec $timeout
  } catch { return ('could not reach the webhook: ' + $_.Exception.Message) }
  $code = $null; $msg = ''
  if ($null -ne $r.errcode) { $code = [int]$r.errcode; $msg = [string]$r.errmsg }   # WeCom
  elseif ($null -ne $r.code) { $code = [int]$r.code; $msg = [string]$r.msg }   # Feishu
  elseif ($null -ne $r.StatusCode) { $code = [int]$r.StatusCode; $msg = [string]$r.StatusMessage }   # older Feishu
  if ($null -ne $code -and $code -ne 0) { return ('the webhook answered {0}: {1}' -f $code, $msg) }
  return ''
}

# At most $maxBytes bytes of $text in UTF-8 (WeCom's limit for one text message), cut at a whole character.
function Limit-KacUtf8([string]$text, [int]$maxBytes) {
  $enc = [Text.Encoding]::UTF8
  if ($enc.GetByteCount($text) -le $maxBytes) { return $text }
  $n = $text.Length
  while ($n -gt 0 -and $enc.GetByteCount($text.Substring(0, $n)) -gt ($maxBytes - 3)) { $n-- }
  if ($n -gt 0 -and [char]::IsHighSurrogate($text[$n - 1])) { $n-- }
  return $text.Substring(0, $n) + '...'
}
