#requires -Version 7.2
<# Packages only an explicitly supplied, current-source Windows core artifact. #>
[CmdletBinding()]
param(
    [ValidateSet('x64','arm64')][string]$Arch = 'x64',
    [Parameter(Mandatory)][string]$CoreDirectory,
    [Parameter(Mandatory)][string]$DependencyLock,
    [string]$OutputDir = '', [string]$ZipOutput = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$rid = "win-$Arch"
$CoreDirectory = (Resolve-Path -LiteralPath $CoreDirectory).Path
$DependencyLock = (Resolve-Path -LiteralPath $DependencyLock).Path
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "dist/mqvpn_windows_gui_$Arch" }
if (-not $ZipOutput) { $ZipOutput = Join-Path $repoRoot "dist/mqvpn_windows_gui_$Arch.zip" }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$ZipOutput = [IO.Path]::GetFullPath($ZipOutput)
# Never recursively delete a caller-supplied path. CI uses a fresh output.
if (Test-Path -LiteralPath $OutputDir) { throw "Output already exists; choose a fresh output directory: $OutputDir" }
if (Test-Path -LiteralPath $ZipOutput) { throw "Archive already exists: $ZipOutput" }
$sourceSha = (& git -c "safe.directory=$($repoRoot.Replace('\','/'))" -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve current source SHA' }
$core = Get-Content -LiteralPath (Join-Path $CoreDirectory 'core-build.json') -Raw | ConvertFrom-Json
if ($core.schema -ne 1 -or $core.architecture -ne $Arch -or $core.sourceSha -ne $sourceSha) { throw 'Core artifact is not for this source SHA and architecture' }
$xquicSha = (& git -c "safe.directory=$($repoRoot.Replace('\','/'))" -C $repoRoot rev-parse HEAD:third_party/xquic).Trim()
if ($LASTEXITCODE -ne 0 -or $core.xquicSha -ne $xquicSha) { throw 'Core artifact xquic pin mismatch' }
foreach ($entry in $core.files) {
    $file = Join-Path $CoreDirectory $entry.path
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Core artifact checksum mismatch: $($entry.path)" }
}
$lock = Get-Content -LiteralPath $DependencyLock -Raw | ConvertFrom-Json
if ($lock.schema -ne 1) { throw 'Unsupported dependency lock' }
$workParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'build/gui-packaging'))
$work = Join-Path $workParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Path $OutputDir | Out-Null
foreach ($relative in @('runtime/mqvpn','runtime/xray','third-party')) { New-Item -ItemType Directory -Path (Join-Path $OutputDir $relative) -Force | Out-Null }
$downloaded = [Collections.Generic.List[object]]::new()
function Download-Plain([string]$Url, [string]$Destination, [string]$Digest = '') {
    # No API credentials or authenticated headers are used for asset downloads.
    Write-Host "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UserAgent 'MQVPN-Packaging'
    $hash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Digest -and $hash -ne ($Digest -replace '^sha256:', '')) { throw "Download checksum mismatch: $Url" }
    $downloaded.Add(@{ url = $Url; sha256 = $hash })
}
function Publish-SelfContained([string]$Project) {
    & dotnet publish (Join-Path $repoRoot $Project) -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
}
function Export-NuGetLicenses {
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'windows/gui/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($item in $assets.libraries.GetEnumerator() | Sort-Object Key) {
        if ($item.Value.type -ne 'package') { continue }
        $packageRoot = $null
        foreach ($folder in $assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $item.Value.path
            if (Test-Path -LiteralPath $candidate) { $packageRoot = $candidate; break }
        }
        if (-not $packageRoot) { throw "Missing restored package: $($item.Key)" }
        $nuspec = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File)[0]
        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $license = $metadata.package.metadata.license
        $name = $item.Key.Replace('/','-')
        $destination = Join-Path $OutputDir "third-party/nuget-$name.txt"
        $includedLicense = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -match '^LICENSE(\.(txt|md))?$')[0]
        if ($license.type -eq 'file') {
            Copy-Item -LiteralPath (Join-Path $packageRoot $license.InnerText) -Destination $destination
        } elseif ($includedLicense) {
            Copy-Item -LiteralPath $includedLicense.FullName -Destination $destination
        } elseif ($license.type -eq 'expression') {
            Download-Plain "https://licenses.nuget.org/$([uri]::EscapeDataString($license.InnerText))" $destination
        } elseif ($metadata.package.metadata.licenseUrl) {
            Download-Plain $metadata.package.metadata.licenseUrl $destination
        } else { throw "Package does not declare a license: $($item.Key)" }
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -match '^(THIRD-PARTY-NOTICES|ThirdPartyNotices)\.txt$')[0]
        if ($notices) { Copy-Item -LiteralPath $notices.FullName -Destination (Join-Path $OutputDir "third-party/nuget-$name-notices.txt") }
        $lines.Add("$($item.Key): $($license.InnerText) $($metadata.package.metadata.licenseUrl)")
    }
    $lines | Set-Content -LiteralPath (Join-Path $OutputDir 'third-party/nuget-licenses.txt') -Encoding utf8NoBOM
    $framework = @($assets.project.frameworks.Values)[0]
    $runtimeDependency = @($framework.downloadDependencies | Where-Object name -eq "Microsoft.NETCore.App.Runtime.$rid")
    if ($runtimeDependency.Count -ne 1) { throw 'Cannot resolve exact restored .NET runtime version' }
    $runtimeVersion = $runtimeDependency[0].version.Trim('[',']').Split(',')[0].Trim()
    $version = Join-Path (@($assets.packageFolders.Keys)[0]) "microsoft.netcore.app.runtime.$rid/$runtimeVersion"
    foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
        $pattern = if ($name -eq 'LICENSE.txt') { '^LICENSE\.(txt|md)$' } else { '^(THIRD-PARTY-NOTICES|ThirdPartyNotices)\.txt$' }
        $source = @(Get-ChildItem -LiteralPath $version -File | Where-Object Name -match $pattern)[0]
        if (-not $source) { throw "Runtime license missing: $name" }
        Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $OutputDir "third-party/dotnet-$name")
    }
    $desktop = @($framework.downloadDependencies | Where-Object name -eq "Microsoft.WindowsDesktop.App.Runtime.$rid")
    if ($desktop.Count -ne 1) { throw 'Cannot resolve exact Windows Desktop runtime license' }
    $desktopVersion = $desktop[0].version.Trim('[',']').Split(',')[0].Trim()
    $desktopRoot = Join-Path (@($assets.packageFolders.Keys)[0]) "microsoft.windowsdesktop.app.runtime.$rid/$desktopVersion"
    $desktopLicense = @(Get-ChildItem -LiteralPath $desktopRoot -File | Where-Object Name -match '^LICENSE(\.(txt|md))?$')[0]
    if (-not $desktopLicense) { throw 'Windows Desktop runtime license missing' }
    Copy-Item -LiteralPath $desktopLicense.FullName -Destination (Join-Path $OutputDir 'third-party/desktop-LICENSE.txt')
}
try {
    Publish-SelfContained 'windows/gui/TruckersMP.Proxy.csproj'
    Publish-SelfContained 'windows/updater/MqvpnUpgrade.csproj'
    foreach ($binary in Get-ChildItem -LiteralPath $CoreDirectory -File | Where-Object Extension -in @('.exe','.dll')) {
        Copy-Item -LiteralPath $binary.FullName -Destination (Join-Path $OutputDir 'runtime/mqvpn')
    }
    Copy-Item -LiteralPath (Join-Path $CoreDirectory 'core-build.json') -Destination (Join-Path $OutputDir 'core-build.json')
    Copy-Item -Path (Join-Path $CoreDirectory 'third-party/*') -Destination (Join-Path $OutputDir 'third-party')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE'),(Join-Path $repoRoot 'NOTICE') -Destination $OutputDir
    $guiReadme = (Get-Content -LiteralPath (Join-Path $repoRoot 'windows/README.md') -Raw).
        Replace('(../README.md#', "(https://github.com/r11234567/mqvpn/blob/$sourceSha/README.md#").
        Replace('(PHASE_B_REVIEW.md)', "(https://github.com/r11234567/mqvpn/blob/$sourceSha/windows/PHASE_B_REVIEW.md)")
    [IO.File]::WriteAllText((Join-Path $OutputDir 'README-GUI.md'), $guiReadme, [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path (Join-Path $OutputDir 'examples') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'windows/examples/client.json') -Destination (Join-Path $OutputDir 'examples/client.json')
    Copy-Item -LiteralPath $DependencyLock -Destination (Join-Path $OutputDir 'dependency-lock.json')
    $wintunZip = Join-Path $work 'wintun.zip'
    Download-Plain $lock.wintun.url $wintunZip $lock.wintun.sha256
    $wintunDir = Join-Path $work 'wintun'
    Expand-Archive -LiteralPath $wintunZip -DestinationPath $wintunDir
    $wintunArch = if ($Arch -eq 'x64') { 'amd64' } else { 'arm64' }
    $wintun = Join-Path $wintunDir "wintun/bin/$wintunArch/wintun.dll"
    foreach ($kernel in @('mqvpn','xray')) { Copy-Item -LiteralPath $wintun -Destination (Join-Path $OutputDir "runtime/$kernel/wintun.dll") }
    $wintunLicense = @(Get-ChildItem -LiteralPath (Join-Path $wintunDir 'wintun') -File | Where-Object Name -match '^(COPYING|LICENSE)(\.txt)?$')[0]
    if (-not $wintunLicense) { throw 'Wintun license missing' }
    Copy-Item -LiteralPath $wintunLicense.FullName -Destination (Join-Path $OutputDir 'third-party/wintun.txt')
    $xrayName = if ($Arch -eq 'x64') { 'Xray-windows-64.zip' } else { 'Xray-windows-arm64-v8a.zip' }
    $asset = @($lock.xray.assets | Where-Object name -eq $xrayName)
    if ($asset.Count -ne 1) { throw "No exact $xrayName in dependency lock" }
    $xrayZip = Join-Path $work 'xray.zip'
    Download-Plain $asset[0].url $xrayZip $asset[0].digest
    Expand-Archive -LiteralPath $xrayZip -DestinationPath (Join-Path $work 'xray')
    Copy-Item -LiteralPath (Join-Path $work 'xray/xray.exe') -Destination (Join-Path $OutputDir 'runtime/xray/xray.exe')
    Download-Plain $lock.xray.licenseUrl (Join-Path $OutputDir 'third-party/xray.txt')
    Download-Plain $lock.geodata.licenseUrl (Join-Path $OutputDir 'third-party/geodata.txt')
    foreach ($name in @('geoip.dat','geosite.dat')) {
        $asset = @($lock.geodata.assets | Where-Object name -eq $name)
        if ($asset.Count -ne 1) { throw "No exact $name in dependency lock" }
        Download-Plain $asset[0].url (Join-Path $OutputDir "runtime/xray/$name") $asset[0].digest
    }
    Export-NuGetLicenses
    $files = @(Get-ChildItem -LiteralPath $OutputDir -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($OutputDir,$_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ schema = 1; architecture = $Arch; rid = $rid; sourceSha = $core.sourceSha; xquicSha = $core.xquicSha; boringSslSha = $core.boringSslSha
       dependencies = $lock; downloads = $downloaded.ToArray(); files = $files; selfContained = $true } |
        ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDir 'package-manifest.json') -Encoding utf8NoBOM
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ZipOutput) | Out-Null
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $ZipOutput -CompressionLevel Optimal
    Write-Host "Created $ZipOutput for $sourceSha ($rid). Native package verification is a separate required CI step."
} finally {
    # Verify the resolved, unique build staging target before recursive cleanup.
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $prefix = $workParent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWork.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup target' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
