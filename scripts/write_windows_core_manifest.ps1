# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Arch,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [Parameter(Mandatory)][string]$LibeventLicense)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function Git-Value([string]$Directory, [string[]]$Arguments) {
    $value = & git -c "safe.directory=$($Directory.Replace('\','/'))" -C $Directory @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source provenance' }
    "$value".Trim()
}
$licenses = Join-Path $OutputDirectory 'third-party'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'build/Release/mqvpn.exe') -Destination $OutputDirectory
$sources = @{
    'xquic.txt' = 'third_party/xquic/LICENSE'; 'boringssl.txt' = 'third_party/xquic/third_party/boringssl/LICENSE'
    'lwip.txt' = 'third_party/lwip/LICENSE'; 'yyjson.txt' = 'third_party/yyjson/LICENSE'
}
foreach ($name in $sources.Keys) { Copy-Item -LiteralPath (Join-Path $repo $sources[$name]) -Destination (Join-Path $licenses $name) }
Copy-Item -LiteralPath $LibeventLicense -Destination (Join-Path $licenses 'libevent.txt')
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ schema = 1; architecture = $Arch; sourceSha = Git-Value $repo @('rev-parse','HEAD')
   xquicSha = Git-Value (Join-Path $repo 'third_party/xquic') @('rev-parse','HEAD')
   boringSslSha = Git-Value (Join-Path $repo 'third_party/xquic/third_party/boringssl') @('rev-parse','HEAD')
   files = $files } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'core-build.json') -Encoding utf8NoBOM
