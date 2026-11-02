# Reading and driving the Kiro IDE chat panel through Windows UI Automation (UIA). ASCII only.
# Every global here starts with Kac: PowerShell names are case-insensitive, so a short name like $ts in a
# caller would silently replace a global like $TS.
$KacAE = [System.Windows.Automation.AutomationElement]
$KacScope = [System.Windows.Automation.TreeScope]
$KacWalker = [System.Windows.Automation.TreeWalker]::RawViewWalker
$KacAny = [System.Windows.Automation.Condition]::TrueCondition
$KacCT = [System.Windows.Automation.ControlType]
# FindAll/FindFirst search the control view by default, which leaves out Chromium's plain divs
# (session-view-timeline and friends). With this cache request pushed, they search the raw view.
$KacCache = New-Object System.Windows.Automation.CacheRequest
$KacCache.TreeFilter = [System.Windows.Automation.Automation]::RawViewCondition
$KacCache.Push()
$KacRoots = @{}

function KacByClass([string]$cls) { New-Object System.Windows.Automation.PropertyCondition($KacAE::ClassNameProperty, $cls) }
function KacByType($type) { New-Object System.Windows.Automation.PropertyCondition($KacAE::ControlTypeProperty, $type) }
function KacByName([string]$name) { New-Object System.Windows.Automation.PropertyCondition($KacAE::NameProperty, $name) }

# Top-level windows of Kiro.exe: El, Hwnd, Name, Workspace (the title reads "<editor tab> - <workspace> - Kiro").
function Get-KacWindows {
  $ids = @{}; foreach ($p in (Get-Process -Name Kiro -ErrorAction SilentlyContinue)) { $ids[$p.Id] = 1 }
  $out = @()
  foreach ($w in $KacAE::RootElement.FindAll($KacScope::Children, $KacAny)) {
    $c = $w.Current
    if (-not $ids.ContainsKey($c.ProcessId) -or $c.ClassName -ne 'Chrome_WidgetWin_1') { continue }
    $name = [string]$c.Name
    $parts = $name -split ' - '
    $ws = $name; if ($parts.Count -ge 2) { $ws = $parts[$parts.Count - 2] }
    $out += [pscustomobject]@{ El = $w; Hwnd = [int64]$c.NativeWindowHandle; Name = $name; Workspace = $ws }
  }
  return $out
}
# The chat panel's root (the element with id=root that holds the session tab bar), cached per window.
function Get-KacChatRoot($win) {
  $r = $KacRoots[$win.Hwnd]
  if ($null -ne $r) {
    try { if ($null -ne $r.FindFirst($KacScope::Descendants, (KacByClass 'tab-bar'))) { return $r } } catch {}
    $KacRoots.Remove($win.Hwnd)
  }
  $byId = New-Object System.Windows.Automation.PropertyCondition($KacAE::AutomationIdProperty, 'root')
  foreach ($c in $win.El.FindAll($KacScope::Descendants, $byId)) {
    if ($null -ne $c.FindFirst($KacScope::Descendants, (KacByClass 'tab-bar'))) { $KacRoots[$win.Hwnd] = $c; return $c }
  }
  return $null
}

# Session tabs: El, Id (UIA runtime id: survives renames, changes when Kiro restarts), Title, Selected,
# State (the indicator's name: Working, Updated, Needs your input, or empty).
function Get-KacTabs($root) {
  $tabs = @()
  foreach ($t in $root.FindAll($KacScope::Descendants, (KacByClass 'tab-bar-item'))) {
    $sel = $false
    try { $sel = $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
    $title = [string]$t.Current.Name
    $label = $t.FindFirst($KacScope::Descendants, (KacByClass 'tab-bar-item-label'))
    $lt = $null; if ($label) { $lt = $label.FindFirst($KacScope::Descendants, (KacByType $KacCT::Text)) }
    if ($lt -and $lt.Current.Name) { $title = [string]$lt.Current.Name }
    else { $ix = $title.IndexOf(' Close '); if ($ix -ge 0) { $title = $title.Substring($ix + 7) } }
    $ind = $t.FindFirst($KacScope::Children, (KacByClass 'tab-bar-item-indicator'))
    $state = ''; if ($ind) { $state = [string]$ind.Current.Name }
    $tabs += [pscustomobject]@{ El = $t; Id = ($t.GetRuntimeId() -join '.'); Title = $title; Selected = $sel; State = $state }
  }
  return $tabs
}
function Find-KacTab($root, [string]$id) { return (Get-KacTabs $root | Where-Object { $_.Id -eq $id } | Select-Object -First 1) }
# Select a tab and wait until it is the one shown (when Kiro is busy, the view switches a while later).
function Show-KacTab($root, [string]$id) {
  for ($k = 0; $k -lt 2; $k++) {
    $t = Find-KacTab $root $id
    if ($null -eq $t) { return $false }
    if ($t.Selected) { return $true }
    $t.El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    for ($i = 0; $i -lt 10; $i++) {
      Start-Sleep -Milliseconds 500
      $t = Find-KacTab $root $id
      if ($t -and $t.Selected) { return $true }
    }
  }
  return $false
}
# The flat list of items (messages, tool blocks, footers) of the session the panel shows: session-view-content
# in a long session, the session-view-timeline itself in a short one (11-02 probe of a fresh session).
function Get-KacItemList($root) {
  $c = $root.FindFirst($KacScope::Descendants, (KacByClass 'session-view-content'))
  if ($null -ne $c) { return $c }
  return $root.FindFirst($KacScope::Descendants, (KacByClass 'session-view-timeline'))
}
# The text of one item in a session. A list gives one line per entry; anything else gives its name, or its text
# pieces joined with $sep (an answer's inline code and bold arrive as separate pieces, so they join with '').
function Get-KacText($el, [string]$sep = '') {
  $c = $el.Current
  if ($c.ControlType -eq $KacCT::List) {
    $lines = @()
    foreach ($li in $el.FindAll($KacScope::Children, (KacByType $KacCT::ListItem))) { $lines += ([string]$li.Current.Name).Trim() }
    if ($lines.Count -gt 0) { return ($lines -join "`n") }
  }
  $n = [string]$c.Name
  if ($n.Trim()) { return $n }
  $parts = @()
  foreach ($t in $el.FindAll($KacScope::Descendants, (KacByType $KacCT::Text))) { $parts += [string]$t.Current.Name }
  return ($parts -join $sep)
}
# The newest user message in the session the panel shows ('' if none). Kiro renders a user message as Markdown,
# so one message can be several items in the list (a heading, paragraphs, a list): everything between the
# newest "Kiro" label (agent-message-role, with the avatar image in front) and the checkpoint bar, or the end
# of the previous turn, before it.
function Get-KacShownUser($root) {
  $content = Get-KacItemList $root
  if ($null -eq $content) { return '' }
  $roles = $content.FindAll($KacScope::Children, (KacByClass 'agent-message-role'))
  if ($roles.Count -eq 0) { return '' }
  $parts = @(); $p = $roles[$roles.Count - 1]
  for ($i = 0; $i -lt 80; $i++) {
    $p = $KacWalker.GetPreviousSibling($p)
    if ($null -eq $p) { break }
    $c = $p.Current; $cls = [string]$c.ClassName
    if ($c.ControlType -eq $KacCT::Image) { continue }
    if ($c.ControlType -eq $KacCT::Button -or $cls -like '*kiro-checkpoint-bar*' -or $cls -like '*kiro-copy-more-options*' -or $cls -like '*agent-message-role*') { break }
    $parts = @([string](Get-KacText $p "`n")) + $parts
  }
  return ($parts -join "`n")
}
# A short fingerprint of the session the panel shows. With Kiro minimized or behind other windows, what UIA
# reads trails by up to ~10 s: right after a tab switch it can still hold the old session.
function Get-KacViewMark($root) {
  $content = Get-KacItemList $root
  if ($null -eq $content) { return 'none' }
  $bars = $content.FindAll($KacScope::Children, (KacByClass 'kiro-checkpoint-bar'))
  $last = $KacWalker.GetLastChild($content); $lastName = ''; if ($last) { $lastName = ([string]$last.Current.Name).Trim() }
  return '{0}|{1}|{2}' -f $bars.Count, (Get-KacShownUser $root).Trim(), $lastName
}
# Show a tab and wait until its contents have reached UIA and stopped changing. $true once it is safe to read.
function Open-KacTab($root, [string]$id, [int]$seconds = 20) {
  $t = Find-KacTab $root $id
  if ($null -eq $t) { return $false }
  if ($t.Selected) { return $true }
  $before = Get-KacViewMark $root
  if (-not (Show-KacTab $root $id)) { return $false }
  $changed = $false
  for ($i = 0; $i -lt $seconds * 2 -and -not $changed; $i++) {
    Start-Sleep -Milliseconds 500
    $changed = ((Get-KacViewMark $root) -ne $before)
  }
  if (-not $changed) { return $false }
  $a = Get-KacViewMark $root
  for ($i = 0; $i -lt 10; $i++) {
    Start-Sleep -Seconds 1
    $b = Get-KacViewMark $root
    if ($b -eq $a) { return $true }
    $a = $b
  }
  return $false
}
# Same message? Compares the first 20 letters with spaces, punctuation and symbols dropped (the record keeps a
# Markdown line break as two spaces and a newline; the chat shows a plain line break).
function Get-KacSquash([string]$s) { return ($s -replace '[\s\p{P}\p{S}]', '') }
function Test-KacSameText([string]$a, [string]$b) {
  $x = Get-KacSquash $a; $y = Get-KacSquash $b
  $n = [Math]::Min(20, [Math]::Min($x.Length, $y.Length))
  if ($n -eq 0) { return ($x.Length -eq 0 -and $y.Length -eq 0) }
  return ($x.Substring(0, $n) -eq $y.Substring(0, $n))
}
# Wait up to $seconds for the panel to show $text as its newest user message (right after a turn, Kiro can
# rebuild the view and the message list is briefly missing). $true once it does.
function Wait-KacShownUser($root, [string]$text, [int]$seconds = 20) {
  for ($i = 0; $i -le $seconds; $i++) {
    if (Test-KacSameText (Get-KacShownUser $root) $text) { return $true }
    Start-Sleep -Seconds 1
  }
  return $false
}
# The tab of a session: in the Kiro window of its workspace (any window if none matches), the tabs titled
# like the session. Count: how many matched; Hit: the match when there is exactly one.
function Find-KacSessionTab($info) {
  $wins = @(Get-KacWindows)
  $mine = @($wins | Where-Object { $_.Workspace -eq $info.Workspace })
  if ($mine.Count -eq 0) { $mine = $wins }
  $hits = @()
  foreach ($w in $mine) {
    $root = Get-KacChatRoot $w
    if ($null -eq $root) { continue }
    foreach ($t in Get-KacTabs $root) { if ($t.Title -eq $info.Title) { $hits += [pscustomobject]@{ Win = $w; Root = $root; Tab = $t } } }
  }
  $hit = $null; if ($hits.Count -eq 1) { $hit = $hits[0] }
  return [pscustomobject]@{ Count = $hits.Count; Hit = $hit }
}

# The shown session's chat box: Editor, Send (the arrow-up button), SendEnabled, Text, Empty. Empty: no text
# besides the placeholder, or (when the send button was found) the button is greyed out, as it is when empty.
function Get-KacInput($root, $placeholders) {
  $wrap = $root.FindFirst($KacScope::Descendants, (KacByClass 'chat-input-wrapper'))
  if ($null -eq $wrap) { return $null }
  $ed = $null; $send = $null
  foreach ($x in $wrap.FindAll($KacScope::Descendants, $KacAny)) {
    $c = $x.Current
    if ($null -eq $ed -and ([string]$c.ClassName) -like '*chat-input-content*') { $ed = $x }
    if ($c.ControlType -eq $KacCT::Button) {
      $icon = $KacWalker.GetFirstChild($x)
      if ($icon -and ([string]$icon.Current.ClassName) -like '*codicon-arrow-up*') { $send = $x }
    }
  }
  $txt = @()
  if ($ed) {
    foreach ($t in $ed.FindAll($KacScope::Descendants, (KacByType $KacCT::Text))) {
      $n = [string]$t.Current.Name
      if ($n.Trim() -and @($placeholders) -notcontains $n.Trim()) { $txt += $n }
    }
  }
  $en = $false; if ($send) { $en = [bool]$send.Current.IsEnabled }
  $empty = ($txt.Count -eq 0) -or ($null -ne $send -and -not $en)
  return [pscustomobject]@{ Editor = $ed; Send = $send; SendEnabled = $en; Text = ($txt -join "`n"); Empty = $empty }
}
function Set-KacInputText($inp, [string]$s) {
  $p = $inp.Editor; $h = [IntPtr]::Zero
  while ($null -ne $p) {
    if ($p.Current.ClassName -eq 'Chrome_RenderWidgetHostHWND') { $h = [IntPtr]$p.Current.NativeWindowHandle; break }
    $p = $KacWalker.GetParent($p)
  }
  if ($h -eq [IntPtr]::Zero) { return 'no render window above the chat box' }
  $r = $inp.Editor.Current.BoundingRectangle
  return [KacNative]::SetValue($h, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, $s)
}
# Does the chat box start with $text? Spaces, punctuation and symbols are ignored: the editor may reflow them.
function Test-KacHolds($inp, [string]$text) {
  $want = Get-KacSquash $text; $got = Get-KacSquash ([string]$inp.Text)
  if ($want.Length -eq 0) { return $false }
  return $got.StartsWith($want.Substring(0, [Math]::Min(12, $want.Length)))
}
function New-KacSession($root) {
  $b = $root.FindFirst($KacScope::Descendants, (KacByName 'New session'))
  if ($null -eq $b) { return $false }
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  return $true
}
function Close-KacTab($root, [string]$id) {
  $t = Find-KacTab $root $id
  if ($null -eq $t) { return $false }
  $b = $t.El.FindFirst($KacScope::Descendants, (KacByClass 'tab-bar-item-close'))
  if ($null -eq $b) { return $false }
  $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  return $true
}
# Type $text into the shown session's chat box and send it, then wait for its tab to say Working.
# Slow on purpose: with Kiro minimized or behind other windows, what UIA reads trails by up to ~10 s, and text
# set right after a tab switch can land on the old chat box (11-02: never showed; set again, showed after 8 s).
# Returns "" once sent, otherwise why not. Never types over a draft (returns "draft").
function Send-KacMessage($root, [string]$id, [string]$text, $placeholders) {
  $inp = Get-KacInput $root $placeholders
  if ($null -eq $inp -or $null -eq $inp.Editor) { return 'chat box not found' }
  if (-not $inp.Empty) { return 'draft' }
  $typed = $false; $why = ''
  for ($attempt = 0; $attempt -lt 2 -and -not $typed; $attempt++) {
    $err = Set-KacInputText $inp $text
    if ($err) { $why = $err }
    for ($i = 0; $i -lt 30 -and -not $typed; $i++) {
      Start-Sleep -Milliseconds 500
      $inp = Get-KacInput $root $placeholders
      if ($null -eq $inp -or $null -eq $inp.Editor) { return 'chat box not found' }
      $typed = Test-KacHolds $inp $text
    }
    if (-not $typed -and -not $inp.Empty) { return ('the box holds something else: [{0}]' -f (Get-KacShort $inp.Text 40)) }
  }
  if (-not $typed) { if ($why) { return $why }; return 'the text never showed up in the box' }
  for ($i = 0; $i -lt 20 -and -not $inp.SendEnabled; $i++) { Start-Sleep -Milliseconds 500; $inp = Get-KacInput $root $placeholders }
  if (-not $inp.SendEnabled) { return 'the send button stays greyed out' }
  for ($k = 0; $k -lt 2; $k++) {
    if ($k -gt 0) {
      # A press soon after a stop is sometimes dropped and the text stays in the box: press once more.
      $inp = Get-KacInput $root $placeholders
      if (-not (Test-KacHolds $inp $text)) { return '' }   # the box emptied: it went out, Working just has not shown
      if (-not $inp.SendEnabled) { break }
    }
    $inp.Send.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 30; $i++) {
      Start-Sleep -Seconds 1
      $t = Find-KacTab $root $id
      if ($t -and $t.State -eq 'Working') { return '' }
    }
  }
  $inp = Get-KacInput $root $placeholders
  if ($inp -and -not (Test-KacHolds $inp $text)) { return '' }
  return 'pressed send twice; the text is still in the box'
}

# Stop the running turn of the shown session with its Cancel button (the panel has one only while a turn runs; the
# last one is the shown session's). '' once its tab no longer says Working, otherwise why not.
function Stop-KacTurn($root, [string]$id) {
  $c = New-Object System.Windows.Automation.AndCondition((KacByType $KacCT::Button), (KacByName 'Cancel'))
  $all = $root.FindAll($KacScope::Descendants, $c)
  if ($all.Count -eq 0) { return 'noCancel' }
  $all[$all.Count - 1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $t = Find-KacTab $root $id
    if ($t -and $t.State -ne 'Working') { return '' }
  }
  return 'the tab still says Working'
}
