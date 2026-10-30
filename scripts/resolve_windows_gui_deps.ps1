# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$LockFile, [string]$GitHubToken = $env:GITHUB_TOKEN)
$ErrorActionPreference = 'Stop'
function Read-GitHubApi([string]$Path) {
    $headers = @{ 'User-Agent' = 'MQVPN-Packaging'; Accept = 'application/vnd.github+json' }
    if ($GitHubToken) { $headers.Authorization = "Bearer $GitHubToken" }
    # Authentication is confined to non-redirecting api.github.com requests.
    Invoke-RestMethod -Uri "https://api.github.com/$Path" -Headers $headers -MaximumRedirection 0
}
function Resolve-Release([string]$Repository, [string[]]$Assets) {
    $releases = Read-GitHubApi "repos/$Repository/releases?per_page=30"
    $release = @($releases | Where-Object { -not $_.draft })[0]
    if (-not $release) { throw "No release for $Repository" }
    $commit = Read-GitHubApi "repos/$Repository/commits/$([uri]::EscapeDataString($release.tag_name))"
    $selected = foreach ($name in $Assets) {
        $selectedAssets = @($release.assets | Where-Object name -eq $name)
        if ($selectedAssets.Count -ne 1) { throw "$Repository $($release.tag_name) lacks unique $name" }
        if ($selectedAssets[0].digest -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw "$Repository $($release.tag_name) lacks a SHA256 digest for $name" }
        @{ name = $name; url = $selectedAssets[0].browser_download_url; digest = $selectedAssets[0].digest }
    }
    @{ repository = $Repository; version = $release.tag_name; revision = $commit.sha; assets = @($selected)
       licenseUrl = "https://raw.githubusercontent.com/$Repository/$($commit.sha)/LICENSE" }
}
$lock = [ordered]@{
    schema = 1
    wintun = @{ version = '0.14.1'; url = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
        sha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51' }
    xray = Resolve-Release 'XTLS/Xray-core' @('Xray-windows-64.zip', 'Xray-windows-arm64-v8a.zip')
    geodata = Resolve-Release 'Loyalsoldier/v2ray-rules-dat' @('geoip.dat', 'geosite.dat')
}
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($LockFile))
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$lock | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $LockFile -Encoding utf8NoBOM
