# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

#requires -Version 7.2
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repoRoot ('build/gui-dependency-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$metadata = Join-Path $work 'dependency-lock.json'
$officialPage = '<a href="builds/wintun-1.2.3.zip">Download</a><br>SHA2-256: <code>' + ('a' * 64) + '</code>'
$assets = @('Xray-windows-64.zip','Xray-windows-arm64-v8a.zip','geoip.dat','geosite.dat') | ForEach-Object {
    @{ name = $_; browser_download_url = "https://example.invalid/$_"; digest = 'sha256:' + ('b' * 64) }
}
function Release([string]$Tag, [string]$Published, [bool]$Pre, [bool]$Draft = $false) {
    @{ tag_name = $Tag; published_at = $Published; prerelease = $Pre; draft = $Draft; assets = $assets }
}
function Invoke-RestMethod($Uri, $Headers, $MaximumRedirection) {
    if ($Uri -match '/releases\?') { return $releases }
    if ($Uri -match '/commits/') { return @{ sha = 'c' * 40 } }
    throw "Unexpected fixture API request: $Uri"
}
function Invoke-WebRequest($Uri, $UserAgent) {
    if ($Uri -ne 'https://www.wintun.net/') { throw "Unexpected fixture web request: $Uri" }
    @{ Content = $officialPage }
}
try {
    $releases = @(
        (Release 'older-stable' '2026-10-01T00:00:00Z' $false),
        (Release 'newer-pre' '2026-10-02T00:00:00Z' $true),
        (Release 'future-draft' '2026-10-03T00:00:00Z' $false $true)
    )
    & "$repoRoot/scripts/resolve_windows_gui_deps.ps1" -LockFile $metadata -GitHubToken ''
    $first = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
    if ($first.xray.version -ne 'newer-pre' -or $first.geodata.version -ne 'newer-pre') { throw 'Latest prerelease was not selected' }
    if ($first.wintun.version -ne '1.2.3' -or $first.wintun.sha256 -ne ('a' * 64)) { throw 'Wintun version/checksum was pinned instead of resolved' }
    if ($first.wintun.url -ne 'https://www.wintun.net/builds/wintun-1.2.3.zip') { throw 'Incorrect official Wintun URL' }

    $releases = @(
        (Release 'older-pre' '2026-10-01T00:00:00Z' $true),
        (Release 'newer-stable' '2026-10-04T00:00:00Z' $false)
    )
    & "$repoRoot/scripts/resolve_windows_gui_deps.ps1" -LockFile $metadata -GitHubToken ''
    $second = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
    if ($second.xray.version -ne 'newer-stable' -or $second.geodata.version -ne 'newer-stable') { throw 'Existing metadata was reused instead of resolving the latest stable release' }

    $officialPage = 'No current archive checksum'
    $rejected = $false
    try { & "$repoRoot/scripts/resolve_wintun.ps1" | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Missing official checksum was accepted' }
    Write-Host 'PASS: latest prerelease/stable, draft exclusion, fresh resolution and official Wintun metadata'
} finally {
    Remove-Item -LiteralPath $metadata -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $work
}
