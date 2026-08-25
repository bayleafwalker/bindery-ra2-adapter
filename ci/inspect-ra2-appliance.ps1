[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$ApplianceId = 'ra2-yr-cncnet-v0.2',
    [string]$SpawnerPath = 'Syringe.exe',
    [string]$InstrumentationPath = 'libra2yrcpp.dll'
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $InstallPath -ErrorAction Stop).Path
$required = @('gamemd.exe', 'RA2MD.exe', $SpawnerPath, 'CnCNet-Spawner.dll', $InstrumentationPath, 'zlib1.dll', 'ra2yrcpp.json')
$artifacts = [System.Collections.Generic.List[object]]::new()

foreach ($relativePath in $required | Select-Object -Unique) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Golden appliance artifact was not found: $path"
    }

    $file = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $artifacts.Add([ordered]@{
        relative_path = $relativePath
        sha256 = "sha256:$hash"
        bytes = [long]$file.Length
    })
}

$spawnerArtifact = @($artifacts | Where-Object { $_.relative_path -eq 'CnCNet-Spawner.dll' })[0]
$manifest = [ordered]@{
    schema_version = 'bindery.ra2.golden-appliance/v2'
    appliance_id = $ApplianceId
    source_install_path = $root
    source_kind = 'steam-owned-local-assets-plus-pinned-open-runtime'
    game_family = 'yuris-revenge'
    game_version = 'cncnet-ra2-mode'
    transport_profile = 'cncnet-private'
    artifacts = @($artifacts)
    clone_policy = 'clone the golden VM, then assign unique hostname, NIC identity, Bindery identity, client instance id, and per-match SPAWN.INI'
    spawner_provenance = [ordered]@{
        source_repository = 'https://github.com/CnCNet/cncnet-yr-client-package'
        source_ref = 'yr-9.3.2'
        source_commit = '8ac409fc4e9f820b404d4ee7adf5f13d7a41b857'
        release_asset = 'package_9.3.2.zip'
        artifact_relative_path = $spawnerArtifact.relative_path
        artifact_sha256 = $spawnerArtifact.sha256
        artifact_bytes = [long]$spawnerArtifact.bytes
        selection_reason = 'Use the official package-embedded CnCNetYR build; retain standalone yrpp-spawner v0.0.0.16 only as a non-selected provenance reference.'
    }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Wrote golden appliance manifest: $OutputPath"
