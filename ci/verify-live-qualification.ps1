[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidencePath,
    [string]$ExternalEvidencePath
)

$ErrorActionPreference = 'Stop'

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Evidence file was not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Has-Property($object, [string]$name) {
    return $null -ne $object -and $object.PSObject.Properties.Name -contains $name
}

function Get-ExternalFlag($name, $evidence, $external) {
    if (Has-Property $external $name) { return [bool]$external.$name }
    if (Has-Property $evidence.qualification $name) { return [bool]$evidence.qualification.$name }
    return $false
}

$evidence = Read-JsonFile $EvidencePath
$external = $null
if ($ExternalEvidencePath) { $external = Read-JsonFile $ExternalEvidencePath }
$failures = [System.Collections.Generic.List[string]]::new()

if ($evidence.schema_version -ne 'bindery.ra2.live-acceptance/v2') { $failures.Add('schema_version is not bindery.ra2.live-acceptance/v2') }
if ([string]::IsNullOrWhiteSpace($evidence.session_id)) { $failures.Add('session_id is missing') }
if ([string]::IsNullOrWhiteSpace($evidence.golden_appliance_id)) { $failures.Add('golden_appliance_id is missing') }
if ($evidence.relay.provider_id -ne 'cncnet-private') { $failures.Add("live acceptance must use cncnet-private; observed '$($evidence.relay.provider_id)'") }
if ($evidence.telemetry.protocol -ne 'ra2yrcpp-protobuf-tcp') { $failures.Add('telemetry protocol is not ra2yrcpp-protobuf-tcp') }

$clients = @($evidence.clients)
if ($clients.Count -ne 2) { $failures.Add("exactly two clients are required; observed $($clients.Count)") }
if ($clients.Count -eq 2) {
    if (@($clients | ForEach-Object client_id | Sort-Object -Unique).Count -ne 2) { $failures.Add('client ids are not distinct') }
    if (@($clients | ForEach-Object account_id | Sort-Object -Unique).Count -ne 2) { $failures.Add('account ids are not distinct') }
    if (@($clients | ForEach-Object client_instance_id | Sort-Object -Unique).Count -ne 2) { $failures.Add('client instance ids are not distinct') }
    if (@($clients | ForEach-Object golden_appliance_id | Sort-Object -Unique).Count -ne 1 -or $clients[0].golden_appliance_id -ne $evidence.golden_appliance_id) { $failures.Add('clients do not identify one shared golden appliance') }
    if (@($clients | ForEach-Object game_executable_sha256 | Sort-Object -Unique).Count -ne 1) { $failures.Add('the two client executable hashes are not identical') }
}

if ([string]::IsNullOrWhiteSpace($evidence.relay.provider_id)) { $failures.Add('relay provider is missing') }
if ([string]::IsNullOrWhiteSpace($evidence.relay.allocation_id)) { $failures.Add('relay allocation id is missing') }
if ([string]::IsNullOrWhiteSpace($evidence.relay.endpoint)) { $failures.Add('relay endpoint is missing') }
if (-not $evidence.qualification.control_plane_lifecycle_complete) { $failures.Add('control-plane lifecycle did not complete for both clients') }
if ($evidence.final_session_phase -ne 'ended') { $failures.Add("final session phase is '$($evidence.final_session_phase)', expected 'ended'") }
if (@($evidence.final_enrollment_phases | Where-Object { $_ -ne 'departed' }).Count -ne 0) { $failures.Add('both final enrollment phases must be departed') }

foreach ($client in $clients) {
    $reports = @($client.reports)
    foreach ($requiredReport in @('ready', 'started', 'exited')) {
        if ($reports -notcontains $requiredReport) { $failures.Add("client $($client.client_id) is missing lifecycle report '$requiredReport'") }
    }
    if ($null -eq $client.process_exit_code -or [int]$client.process_exit_code -ne 0) { $failures.Add("client $($client.client_id) did not exit with code 0") }
    # Exit code 0 is not proof the game ran: Syringe is a debugger and returns
    # 0 after the process it hosted crashes. A failed report or a recorded
    # failure must veto the run even when the exit code looks clean.
    if ($reports -contains 'failed') { $failures.Add("client $($client.client_id) reported a failed lifecycle") }
    if (-not [string]::IsNullOrWhiteSpace($client.failure)) { $failures.Add("client $($client.client_id) recorded a failure: $($client.failure)") }
    if ($client.game_executable_sha256 -notmatch '^sha256:[0-9a-f]{64}$') { $failures.Add("client $($client.client_id) has no valid executable hash") }
    if ($client.spawn_ini_sha256 -notmatch '^sha256:[0-9a-f]{64}$') { $failures.Add("client $($client.client_id) has no valid spawn INI hash") }
}

$relayTrafficObserved = Get-ExternalFlag 'relay_traffic_observed' $evidence $external
$packetsForwarded = 0
if (Has-Property $external 'packets_forwarded') { $packetsForwarded = [long]$external.packets_forwarded }
elseif ((Has-Property $evidence.relay 'packets_forwarded') -and $null -ne $evidence.relay.packets_forwarded) { $packetsForwarded = [long]$evidence.relay.packets_forwarded }
if (-not $relayTrafficObserved -or $packetsForwarded -le 0) { $failures.Add('positive relay traffic evidence is missing') }
$telemetryObserved = Get-ExternalFlag 'telemetry_raw_events_observed' $evidence $external
$rawEventCount = 0
if (Has-Property $external 'telemetry_raw_event_count') { $rawEventCount = [long]$external.telemetry_raw_event_count }
elseif ((Has-Property $evidence.telemetry 'raw_event_count') -and $null -ne $evidence.telemetry.raw_event_count) { $rawEventCount = [long]$evidence.telemetry.raw_event_count }
if (-not $telemetryObserved -or $rawEventCount -le 0) { $failures.Add('positive ra2yrcpp telemetry evidence is missing') }
if (-not (Get-ExternalFlag 'kctl_intake_authorized' $evidence $external)) { $failures.Add('Kctl knowledge.candidate.intake authority is not evidenced') }
if (-not (Get-ExternalFlag 'oracle_reads_traced' $evidence $external)) { $failures.Add('oracle read tracing is not evidenced') }
# Human acceptance is not a separate boolean anyone can set: it is an operator
# attesting that they watched a specific match complete. What the packet needs
# is who observed it and which run, so the claim is attributable rather than a
# flag someone flipped. A completed match with no observer recorded is still
# unaccepted, and an observer recorded against a run that did not complete is a
# contradiction rather than an acceptance.
$observer = $null
if (Has-Property $external 'match_observed_by') { $observer = [string]$external.match_observed_by }
$observedRun = $null
if (Has-Property $external 'match_observed_run_id') { $observedRun = [string]$external.match_observed_run_id }
if ([string]::IsNullOrWhiteSpace($observer)) {
    $failures.Add('no operator is recorded as having observed the match (match_observed_by)')
} elseif ([string]::IsNullOrWhiteSpace($observedRun)) {
    $failures.Add('the observation does not name a run (match_observed_run_id)')
} elseif ($observedRun -ne $evidence.run_id) {
    $failures.Add("the recorded observation is of run '$observedRun', not this run '$($evidence.run_id)'")
} elseif (-not $evidence.qualification.control_plane_lifecycle_complete) {
    $failures.Add('a match observation is recorded for a run whose clients did not complete')
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: evidence is not ready for global qualification submission.' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "- $_" }
    exit 1
}

Write-Host 'PASS: evidence contains the required live acceptance and external qualification inputs.' -ForegroundColor Green
Write-Host 'Global qualification remains an external authority decision; this script does not set or publish qualification.'
exit 0
