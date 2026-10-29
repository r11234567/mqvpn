<#
.SYNOPSIS
    Packages the complete MQVPN Windows GUI distribution for x64 or arm64.
.DESCRIPTION
    Builds single-file MQVPN.exe and MqvpnUpgrade.exe, bundles the matching mqvpn core,
    downloads the latest Xray pre-release core, downloads latest GeoData rules (geoip/geosite),
    and embeds matching Wintun driver DLLs.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',

    [string]$OutputDir = '',
    [string]$ZipOutput = '',
    [string]$CoreMqvpnExe = '',
    [string]$CoreMqvpnZip = '',
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path

$rid = if ($Arch -eq 'arm64') { 'win-arm64' } else { 'win-x64' }
$wintunArch = if ($Arch -eq 'arm64') { 'arm64' } else { 'amd64' }

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot "dist\mqvpn_windows_gui_$Arch"
}
if (-not $ZipOutput) {
    $ZipOutput = Join-Path $repoRoot "dist\mqvpn_windows_gui_$Arch.zip"
}

Write-Host "================================================================================" -ForegroundColor Cyan
Write-Host ">>> Assembling Full MQVPN Windows GUI Distribution Package ($Arch) <<<" -ForegroundColor Cyan
Write-Host " Target RID:       $rid"
Write-Host " Output Directory: $OutputDir"
Write-Host " Zip Output:       $ZipOutput"
Write-Host "================================================================================" -ForegroundColor Cyan

if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir "runtime\mqvpn") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir "runtime\xray") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDir "config") | Out-Null

$headers = @{ 'User-Agent' = 'MQVPN-Packaging-Script' }
if ($GitHubToken) {
    $headers['Authorization'] = "token $GitHubToken"
}

# 1. Publish .NET 9 Single-File GUI & Updater
Write-Host "`n[1/5] Publishing MQVPN.exe and MqvpnUpgrade.exe for $rid..." -ForegroundColor Yellow
$guiProj = Join-Path $repoRoot "windows\gui\TruckersMP.Proxy.csproj"
$updaterProj = Join-Path $repoRoot "windows\updater\MqvpnUpgrade.csproj"

dotnet publish $guiProj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish MQVPN GUI" }

dotnet publish $updaterProj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish MqvpnUpgrade" }

# 2. Download and Extract Wintun Driver DLL (matching arch)
Write-Host "`n[2/5] Downloading and bundling Wintun driver ($wintunArch)..." -ForegroundColor Yellow
$wintunTemp = Join-Path ([System.IO.Path]::GetTempPath()) "wintun-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $wintunTemp | Out-Null
$wintunZip = Join-Path $wintunTemp "wintun.zip"

try {
    Invoke-WebRequest -Uri "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wintunZip -Headers $headers
    Expand-Archive -Path $wintunZip -DestinationPath $wintunTemp -Force
    $wintunDll = Join-Path $wintunTemp "wintun\bin\$wintunArch\wintun.dll"
    if (-not (Test-Path $wintunDll)) {
        throw "wintun.dll not found at $wintunDll"
    }
    Copy-Item $wintunDll (Join-Path $OutputDir "runtime\mqvpn\wintun.dll") -Force
    Copy-Item $wintunDll (Join-Path $OutputDir "runtime\xray\wintun.dll") -Force
    Write-Host "  Bundled wintun.dll ($wintunArch) successfully" -ForegroundColor Green
} finally {
    Remove-Item -Recurse -Force $wintunTemp -ErrorAction SilentlyContinue
}

# 3. Bundle Core mqvpn.exe
Write-Host "`n[3/5] Bundling core mqvpn.exe..." -ForegroundColor Yellow
$targetCoreExe = Join-Path $OutputDir "runtime\mqvpn\mqvpn.exe"

if ($CoreMqvpnExe -and (Test-Path $CoreMqvpnExe)) {
    Write-Host "  Using provided core executable: $CoreMqvpnExe"
    Copy-Item $CoreMqvpnExe $targetCoreExe -Force
} elseif ($CoreMqvpnZip -and (Test-Path $CoreMqvpnZip)) {
    Write-Host "  Extracting core from archive: $CoreMqvpnZip"
    $coreExtractTemp = Join-Path ([System.IO.Path]::GetTempPath()) "mqvpn-core-$([Guid]::NewGuid().ToString('N'))"
    Expand-Archive -Path $CoreMqvpnZip -DestinationPath $coreExtractTemp -Force
    $foundExe = Get-ChildItem -Path $coreExtractTemp -Filter "mqvpn.exe" -Recurse | Select-Object -First 1
    if (-not $foundExe) { throw "mqvpn.exe not found in archive $CoreMqvpnZip" }
    Copy-Item $foundExe.FullName $targetCoreExe -Force
    # If xquic.dll exists, copy it too
    $foundXquic = Get-ChildItem -Path $coreExtractTemp -Filter "xquic.dll" -Recurse | Select-Object -First 1
    if ($foundXquic) {
        Copy-Item $foundXquic.FullName (Join-Path $OutputDir "runtime\mqvpn\xquic.dll") -Force
    }
    Remove-Item -Recurse -Force $coreExtractTemp -ErrorAction SilentlyContinue
} else {
    Write-Host "  Querying latest mqvpn core release from GitHub..."
    $mqvpnReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/mp0rta/mqvpn/releases" -Headers $headers
    $targetRelease = $mqvpnReleases | Where-Object { -not $_.draft } | Select-Object -First 1
    $pattern = if ($Arch -eq 'arm64') { '*windows*arm64*.zip' } else { '*windows*amd64*.zip' }
    $asset = $targetRelease.assets | Where-Object { $_.name -like $pattern -and $_.name -notlike '*gui*' } | Select-Object -First 1
    if (-not $asset) {
        # Fallback to general windows zip
        $asset = $targetRelease.assets | Where-Object { $_.name -like '*windows*.zip' -and $_.name -notlike '*gui*' } | Select-Object -First 1
    }
    if (-not $asset) {
        throw "Could not find compatible mqvpn core release asset for $Arch"
    }
    Write-Host "  Downloading $($asset.name) ($($asset.browser_download_url))..."
    $coreTemp = Join-Path ([System.IO.Path]::GetTempPath()) "mqvpn-core-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $coreTemp | Out-Null
    $coreZip = Join-Path $coreTemp $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $coreZip -Headers $headers
    Expand-Archive -Path $coreZip -DestinationPath $coreTemp -Force
    $found = Get-ChildItem -Path $coreTemp -Filter "mqvpn.exe" -Recurse | Select-Object -First 1
    if (-not $found) { throw "mqvpn.exe not found in downloaded package" }
    Copy-Item $found.FullName $targetCoreExe -Force
    $foundXquic = Get-ChildItem -Path $coreTemp -Filter "xquic.dll" -Recurse | Select-Object -First 1
    if ($foundXquic) {
        Copy-Item $foundXquic.FullName (Join-Path $OutputDir "runtime\mqvpn\xquic.dll") -Force
    }
    Remove-Item -Recurse -Force $coreTemp -ErrorAction SilentlyContinue
}
Write-Host "  Core mqvpn.exe bundled successfully" -ForegroundColor Green

# 4. Download and Bundle Latest Xray Pre-Release
Write-Host "`n[4/5] Downloading latest Xray-core pre-release..." -ForegroundColor Yellow
$xrayReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/XTLS/Xray-core/releases" -Headers $headers
$latestXray = $xrayReleases | Where-Object { -not $_.draft } | Select-Object -First 1
Write-Host "  Target Xray Release: $($latestXray.tag_name) (Pre-release: $($latestXray.prerelease))"

$xrayAssetPattern = if ($Arch -eq 'arm64') { '*windows-arm64*' } else { 'Xray-windows-64.zip' }
$xrayAsset = $latestXray.assets | Where-Object { $_.name -like $xrayAssetPattern } | Select-Object -First 1
if (-not $xrayAsset) {
    throw "Xray asset matching '$xrayAssetPattern' not found in release $($latestXray.tag_name)"
}

Write-Host "  Downloading $($xrayAsset.name)..."
$xrayTemp = Join-Path ([System.IO.Path]::GetTempPath()) "xray-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $xrayTemp | Out-Null
$xrayZip = Join-Path $xrayTemp $xrayAsset.name
Invoke-WebRequest -Uri $xrayAsset.browser_download_url -OutFile $xrayZip -Headers $headers
Expand-Archive -Path $xrayZip -DestinationPath $xrayTemp -Force
$foundXray = Get-ChildItem -Path $xrayTemp -Filter "xray.exe" -Recurse | Select-Object -First 1
if (-not $foundXray) { throw "xray.exe not found in downloaded Xray package" }
Copy-Item $foundXray.FullName (Join-Path $OutputDir "runtime\xray\xray.exe") -Force
Remove-Item -Recurse -Force $xrayTemp -ErrorAction SilentlyContinue
Write-Host "  Xray core bundled successfully ($($latestXray.tag_name))" -ForegroundColor Green

# 5. Download and Bundle Latest GeoData (geoip.dat & geosite.dat)
Write-Host "`n[5/5] Downloading latest GeoData rules..." -ForegroundColor Yellow
$geoRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/Loyalsoldier/v2ray-rules-dat/releases/latest" -Headers $headers
$geoipAsset = $geoRelease.assets | Where-Object { $_.name -eq 'geoip.dat' } | Select-Object -First 1
$geositeAsset = $geoRelease.assets | Where-Object { $_.name -eq 'geosite.dat' } | Select-Object -First 1

if (-not $geoipAsset -or -not $geositeAsset) {
    throw "geoip.dat or geosite.dat not found in latest release"
}

Write-Host "  Downloading geoip.dat..."
Invoke-WebRequest -Uri $geoipAsset.browser_download_url -OutFile (Join-Path $OutputDir "runtime\xray\geoip.dat") -Headers $headers
Write-Host "  Downloading geosite.dat..."
Invoke-WebRequest -Uri $geositeAsset.browser_download_url -OutFile (Join-Path $OutputDir "runtime\xray\geosite.dat") -Headers $headers
Write-Host "  GeoData rules bundled successfully" -ForegroundColor Green

# Verification of all mandatory modules
Write-Host "`n--- Verification of Distribution Package Components ---" -ForegroundColor Cyan
$requiredFiles = @(
    "MQVPN.exe",
    "MqvpnUpgrade.exe",
    "runtime\mqvpn\mqvpn.exe",
    "runtime\mqvpn\wintun.dll",
    "runtime\xray\xray.exe",
    "runtime\xray\wintun.dll",
    "runtime\xray\geoip.dat",
    "runtime\xray\geosite.dat"
)

foreach ($rel in $requiredFiles) {
    $full = Join-Path $OutputDir $rel
    if (-not (Test-Path $full)) {
        throw "VERIFICATION FAILED: Mandatory component missing: $rel"
    }
    $info = Get-Item $full
    if ($info.Length -le 0) {
        throw "VERIFICATION FAILED: File is empty (0 bytes): $rel"
    }
    Write-Host ("  [OK] {0,-30} ({1,10:N0} bytes)" -f $rel, $info.Length) -ForegroundColor Green
}

# Compress to ZIP
if ($ZipOutput) {
    $zipParent = Split-Path -Parent $ZipOutput
    if ($zipParent -and -not (Test-Path $zipParent)) {
        New-Item -ItemType Directory -Force -Path $zipParent | Out-Null
    }
    if (Test-Path $ZipOutput) {
        Remove-Item -Force $ZipOutput
    }
    Write-Host "`nCompressing package to $ZipOutput..." -ForegroundColor Yellow
    Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipOutput -CompressionLevel Optimal
    $zipSize = (Get-Item $ZipOutput).Length
    Write-Host "================================================================================" -ForegroundColor Cyan
    Write-Host ("SUCCESS: Distribution ZIP created at {0} ({1:N0} bytes)" -f $ZipOutput, $zipSize) -ForegroundColor Green
    Write-Host "================================================================================" -ForegroundColor Cyan
}
