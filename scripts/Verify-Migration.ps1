param([string]$SourceBackupRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SourceBackupRoot) { $SourceBackupRoot = Join-Path $root 'TestResults/Migration/retired-originals/host' }
$inventory = Get-Content -LiteralPath "$root/docs/migration/source-inventory.json" -Raw | ConvertFrom-Json
$normalizedProductionCount = 0
foreach ($entry in $inventory) {
    $target = Join-Path $root $entry.target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.targetSha256) { throw "Target differs from migration snapshot: $($entry.target)" }
    $source = Join-Path $SourceBackupRoot $entry.source
    if (!(Test-Path -LiteralPath $source)) { continue }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sourceSha256) { throw "Source backup changed: $($entry.source)" }
    if ($entry.source -match '^Plugins/MySmallTools/MySmallTools/.+\.(cs|axaml)$' -and $entry.source -notmatch '/AssemblyInfo.cs$') {
        $expected = [IO.File]::ReadAllText($source).Replace('MySmallTools','VideoSecurityPlayer').Replace('mysmalltools','videosecurityplayer')
        $expected = $expected.Replace('avares://VideoSecurityPlayer/','avares://VideoSecurityPlayer.Plugin/').Replace('assembly=VideoSecurityPlayer"','assembly=VideoSecurityPlayer.Plugin"').Replace('VideoSecurityPlayer.dll','VideoSecurityPlayer.Plugin.dll')
        if ($entry.source.EndsWith('/SecretVideoUserData.cs')) { $expected = $expected.Replace('"VideoSecurityPlayer",','"MySmallTools",') }
        $actual = [IO.File]::ReadAllText($target)
        if ($actual.Replace("`r`n","`n") -ne $expected.Replace("`r`n","`n")) { throw "Unexpected production change: $($entry.target)" }
        $normalizedProductionCount++
    }
}
Write-Output "Verified $($inventory.Count) migrated target digests; $normalizedProductionCount production C#/AXAML files match permitted mechanical renames."
if ($normalizedProductionCount -eq 0) { Write-Output 'Source backup unavailable: target digests checked, source comparison not performed.' }
