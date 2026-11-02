# Settings: config.default.json, then config.json (yours, kept out of git), then the file given with -Config.
# Later files override earlier ones, key by key. ASCII only.
function Merge-KacObject($base, $over) {
  foreach ($p in $over.PSObject.Properties) {
    $b = $base.PSObject.Properties[$p.Name]
    $bothObjects = $b -and ($b.Value -is [System.Management.Automation.PSCustomObject]) -and ($p.Value -is [System.Management.Automation.PSCustomObject])
    if ($bothObjects) { Merge-KacObject $b.Value $p.Value }
    elseif ($b) { $b.Value = $p.Value }
    else { $base | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value }
  }
}
function Read-KacJson([string]$path) {
  try { return (Get-Content -Raw -Encoding UTF8 -Path $path | ConvertFrom-Json) }
  catch { throw ("cannot read {0}: {1}" -f $path, $_.Exception.Message) }
}
function Read-KacConfig([string]$dir, [string]$extra) {
  $cfg = Read-KacJson (Join-Path $dir 'config.default.json')
  foreach ($f in @((Join-Path $dir 'config.json'), $extra)) {
    if ($f -and (Test-Path -LiteralPath $f)) { Merge-KacObject $cfg (Read-KacJson $f) }
  }
  return $cfg
}
