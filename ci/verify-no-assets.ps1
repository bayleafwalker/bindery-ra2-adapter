# SPDX-License-Identifier: GPL-3.0-or-later
# Fails when a retail Red Alert 2 / Yuri's Revenge asset is present under the repository (or -Root).
# Retail content ships as archives (*.mix), maps (*.map, *.mpr, *.yrm, *.mmx), string tables (*.csf), art
# (*.shp, *.vxl, *.hva, *.pal), audio banks (*.bag, *.idx), the game's INI files and its executables, so the
# check matches extensions and name patterns rather than a handful of exact file names, and rejects a maps/
# directory outright (a retail map folder, whatever its files are called).
param(
    [string] $Root = (Get-Location).Path
)
$ErrorActionPreference = 'Stop'

$forbiddenExtensions = @('.mix', '.map', '.mpr', '.yrm', '.mmx', '.csf', '.shp', '.vxl', '.hva', '.pal', '.bag', '.idx')
$forbiddenNamePatterns = @(
    'ra2.exe', 'ra2md.exe', 'gamemd.exe', 'game.exe', 'yuri.exe', 'blowfish.dll',
    '*md.ini',
    'rules.ini', 'art.ini', 'ai.ini', 'sound.ini', 'theme.ini', 'eva.ini', 'ui.ini', 'battle.ini', 'mapsel.ini', 'ra2.ini'
)

$found = [System.Collections.Generic.List[string]]::new()
$items = Get-ChildItem -LiteralPath $Root -Recurse -Force |
    Where-Object { $_.FullName -notmatch '[\\/]\.git([\\/]|$)' }
foreach ($item in $items) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $item.FullName)
    if ($item.PSIsContainer) {
        if ($item.Name -ieq 'maps') { $found.Add("$relative (retail map directory)") }
        continue
    }
    if ($forbiddenExtensions -contains $item.Extension.ToLowerInvariant()) { $found.Add($relative); continue }
    foreach ($pattern in $forbiddenNamePatterns) {
        if ($item.Name -ilike $pattern) { $found.Add($relative); break }
    }
}

if ($found.Count -gt 0) {
    throw "Proprietary game assets found in adapter repository: $($found -join ', ')"
}
Write-Host 'No proprietary game assets found.'
