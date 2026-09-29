# Bindery RA2 lab: put this guest's appliance into the state one stage needs.
# Runs as SYSTEM through the QEMU guest agent (lab-run.sh). Idempotent.
#   prepare.ps1 -Stage 1|2|restore -RunId <id> -CommandPeer <client-a address> [-DeferServiceStart]
# Stage 1 : stock ra2yrcpp (v0.2 appliance), brutal-AI spawnmap.ini.
# Stage 2 : fork ra2yrcpp (C:\Bindery\lab\fork) + allowlisted ra2yrcpp.json;
#           on client-b a firewall rule admits TCP 14521 from client-a
#           (-CommandPeer) only. The fork DLL must be built with upstream's
#           docker MinGW toolchain (README): it then needs only zlib1.dll.
# restore : stock ra2yrcpp and no 14521 rule (spawnmap.ini stays as staged).
# -DeferServiceStart (stage 2 only): the fork starts its service on the first
#           game frame instead of at ExeRun (ra2yrcpp.json deferServiceStart;
#           needs a fork build with feat/defer-service-start).
# Every file replaced in the game tree is first copied, once, to
# C:\Bindery\backup-2026-09-27\<appliance>\.
param([Parameter(Mandatory)][ValidateSet('1', '2', 'restore')][string] $Stage, [string] $RunId = '',
      [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string] $CommandPeer = '', [switch] $DeferServiceStart)
$ErrorActionPreference = 'Stop'
$lab = 'C:\Bindery\lab'
switch -Wildcard ($env:COMPUTERNAME) { '*RA2-A' { $side = 'a' } '*RA2-B' { $side = 'b' } default { throw "unknown clone $env:COMPUTERNAME" } }
$app = "C:\Bindery\appliances\client-$side"
$backup = "C:\Bindery\backup-2026-09-27\client-$side"
New-Item -ItemType Directory -Force $backup | Out-Null

# One-time backup of everything this script may replace.
foreach ($f in 'libra2yrcpp.dll', 'zlib1.dll', 'ra2yrcpp.json', 'spawnmap.ini', 'SPAWN.INI', 'RA2MD.INI') {
  $src = Join-Path $app $f; $dst = Join-Path $backup $f
  if ((Test-Path $src) -and -not (Test-Path $dst)) { Copy-Item $src $dst; "backed up $f" }
}
$stock = @{ 'libra2yrcpp.dll' = '59B8D235A44398F20D290B44B60B92D7127FADA1353CFE1D2BFD0A8B646A0B28'
            'zlib1.dll'       = '535FB06FC2096A1322FF1A9BE3F5C975D73BBEF96360D49DE1D3712208044618'
            'ra2yrcpp.json'   = 'B29614C884058AE44DC2616A7B76D58CD8331ADDE3016163B5ACE0FF28F6101D' }
foreach ($k in $stock.Keys) {
  $h = (Get-FileHash (Join-Path $backup $k)).Hash
  if ($h -ne $stock[$k]) { throw "backup of $k is not the stock v0.2 file ($h); refusing to continue" }
}

function Install-Stock {
  foreach ($k in $stock.Keys) { Copy-Item -Force (Join-Path $backup $k) (Join-Path $app $k) }
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $app 'libmcfgthread-2.dll')
  "ra2yrcpp: stock v0.2"
}
function Install-Fork {
  foreach ($f in 'libra2yrcpp.dll', 'zlib1.dll') { Copy-Item -Force (Join-Path "$lab\fork" $f) (Join-Path $app $f) }
  # Left behind by an earlier nix mcfgthread build, which quit on frame 0.
  Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $app 'libmcfgthread-2.dll')
  # regex_search in the fork: anchor it, or 192.168.122.1 also matches .189.
  $hosts = if ($side -eq 'b') { '^(127\.0\.0\.1|' + [regex]::Escape($CommandPeer) + ')$' } else { '^127\.0\.0\.1$' }
  $config = [ordered]@{
    port              = 14521
    allowedHostsRegex = $hosts
    logFilename       = 'ra2yrcpp.log'
    recordFilename    = 'ra2yrcpp.record'
    allowedCommands   = @('GetGameState', 'ReadValue', 'UnitOrder', 'ProduceOrder', 'PlaceBuilding', 'PlaceQuery')
  }
  if ($DeferServiceStart) { $config.deferServiceStart = $true }
  $config | ConvertTo-Json | Set-Content -Encoding ascii (Join-Path $app 'ra2yrcpp.json')
  "ra2yrcpp: fork (hosts $hosts$(if ($DeferServiceStart) { ', service deferred to the first frame' }))"
}
$rule = 'Bindery RA2 lab command 14521'
function Remove-CommandRule { Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule }

switch ($Stage) {
  '1' { Install-Stock; Remove-CommandRule }
  'restore' { Install-Stock; Remove-CommandRule }
  '2' {
    if ($side -eq 'b' -and -not $CommandPeer) { throw 'stage 2 on client-b needs -CommandPeer (client-a address)' }
    Install-Fork
    if ($side -eq 'b') {
      $existing = Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
      if ($existing -and ($existing | Get-NetFirewallAddressFilter).RemoteAddress -ne $CommandPeer) { $existing | Remove-NetFirewallRule; $existing = $null }
      if (-not $existing) {
        New-NetFirewallRule -DisplayName $rule -Direction Inbound -Action Allow -Protocol TCP -LocalPort 14521 -RemoteAddress $CommandPeer | Out-Null
        "firewall: $rule (TCP 14521 from $CommandPeer)"
      }
    }
  }
}
if ($Stage -eq 'restore') { return }

# The scenario map: 4_cellular.map with the Brutal AI overlay appended (4 seats: 2 clients + 2 AI).
$want = '461A55939598D86E'
$sm = Join-Path $app 'spawnmap.ini'
if (-not (Test-Path $sm) -or -not (Get-FileHash $sm).Hash.StartsWith($want)) { Copy-Item -Force "$lab\spawnmap-brutal.ini" $sm; "staged spawnmap.ini" }

# Per-run telemetry: move the previous record/logs aside so counts belong to this run.
# syringe.log is only written when Syringe launches, so a game that never starts must not inherit the last run's.
if ($RunId) {
  $pre = "$lab\runs\$RunId\pre-$side"; New-Item -ItemType Directory -Force $pre | Out-Null
  foreach ($f in 'ra2yrcpp.record', 'ra2yrcpp.log', 'DDrawCompat-gamemd.log', 'syringe.log') { $p = Join-Path $app $f; if (Test-Path $p) { Move-Item -Force $p $pre } }
}
foreach ($f in 'libra2yrcpp.dll', 'zlib1.dll', 'ra2yrcpp.json', 'spawnmap.ini', 'gamemd.exe') {
  "{0,-16} {1}" -f $f, (Get-FileHash (Join-Path $app $f)).Hash.Substring(0, 16)
}
