# Unit tests for src/Notify.ps1: webhook kinds, the JSON bodies, Feishu's signature, the notice text. Sends nothing.
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\Notify.Tests.ps1
# ASCII only.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
foreach ($part in 'Config', 'Policy', 'Notify') { . (Join-Path $repo "src\$part.ps1") }
$cfg = Read-KacJson (Join-Path $repo 'config.default.json')
$script:pass = 0; $script:fail = 0
function Check([string]$what, [bool]$ok) { if ($ok) { $script:pass++ } else { $script:fail++; Write-Host ('FAIL ' + $what) } }

Check 'WeCom webhook' ((Get-KacNotifyKind 'https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc') -eq 'wecom')
Check 'Feishu webhook' ((Get-KacNotifyKind 'https://open.feishu.cn/open-apis/bot/v2/hook/abc') -eq 'feishu')
Check 'Lark webhook' ((Get-KacNotifyKind 'https://open.larksuite.com/open-apis/bot/v2/hook/abc') -eq 'feishu')
Check 'another URL: none' ((Get-KacNotifyKind 'https://example.com/hook') -eq '')
Check 'plain http: none' ((Get-KacNotifyKind 'http://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abc') -eq '')

$zh = -join [char[]]@(0x9700, 0x8981, 0x4F60)
$j = New-KacNotifyBody 'wecom' ("line 1`n" + $zh) | ConvertFrom-Json
Check 'WeCom body: a text message, Chinese and line break kept' ($j.msgtype -eq 'text' -and $j.text.content -eq ("line 1`n" + $zh))
$j = New-KacNotifyBody 'feishu' 'hi' | ConvertFrom-Json
Check 'Feishu body without a secret: no signature' ($j.msg_type -eq 'text' -and $j.content.text -eq 'hi' -and $null -eq $j.sign)
# Feishu's signature: HMAC-SHA256 keyed with "<timestamp>\n<secret>" over an empty message, in base64.
$j = New-KacNotifyBody 'feishu' 'hi' 'mysecret' 1700000000 | ConvertFrom-Json
$mac = New-Object System.Security.Cryptography.HMACSHA256 (, [Text.Encoding]::UTF8.GetBytes("1700000000`nmysecret"))
$want = [Convert]::ToBase64String($mac.ComputeHash([byte[]]@()))
Check 'Feishu body with a secret: timestamp and sign' ($j.timestamp -eq '1700000000' -and $j.sign -eq $want -and $j.msg_type -eq 'text' -and $j.content.text -eq 'hi')

$at = [DateTime]'2026-11-02T13:45:00'
$cfg.language = 'zh'
$n = Format-KacNotice $cfg 'outOfCredits' 'my_project | x' 'out of credits [error]' $at
$lines = @($n -split "`n")
Check ('notice: headline, session, detail, time: ' + $n.Replace("`n", ' / ')) ($lines.Count -eq 4 -and $lines[0] -eq ([string]$cfg.words.zh.notice.prefix + [string]$cfg.words.zh.notice.outOfCredits) -and $lines[1] -eq 'my_project | x' -and $lines[2] -eq 'out of credits [error]' -and $lines[3] -eq '11-02 13:45')
$cfg.language = 'en'
$n = Format-KacNotice $cfg 'done' 'ws | t' '' $at
Check ('English notice, empty detail left out: ' + $n.Replace("`n", ' / ')) (@($n -split "`n").Count -eq 3 -and $n.StartsWith('[kiro-auto-continue] Until-done: finished.'))
Check 'unknown code: the code itself' ((Format-KacNotice $cfg 'whatever' '' '' $at).Contains('whatever'))
$zh10 = -join (1..10 | ForEach-Object { [char]0x4E2D })
Check 'a short text is left alone' ((Limit-KacUtf8 'abc' 2000) -eq 'abc')
$cut = Limit-KacUtf8 $zh10 20; $nb = [Text.Encoding]::UTF8.GetByteCount($cut)
Check ('cut to at most 20 bytes, at a whole character: ' + $nb) ($nb -le 20 -and $cut.EndsWith('...') -and $cut.StartsWith((-join (1..5 | ForEach-Object { [char]0x4E2D }))))
$cfg.notify.webhook = ''
Check 'no webhook: not sent, says why' ((Send-KacNotice $cfg 'x') -like 'no webhook*')
$cfg.notify.webhook = 'https://example.com/hook'
Check 'not a WeCom or Feishu webhook: not sent' ((Send-KacNotice $cfg 'x') -like '*neither*')

"{0} passed, {1} failed" -f $script:pass, $script:fail
exit $script:fail
