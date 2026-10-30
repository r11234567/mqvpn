# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Arch,
      [Parameter(Mandatory)][string]$SourceSha,
      [Parameter(Mandatory)][string]$ZipPath,
      [Parameter(Mandatory)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$native = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if ($native -ne $Arch) { throw "Native $Arch runner required; current PowerShell is $native" }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$ZipPath = (Resolve-Path -LiteralPath $ZipPath).Path
New-Item -ItemType Directory -Force -Path $ReportDirectory | Out-Null
$work = Join-Path (Split-Path -Parent $PSScriptRoot) ("build/gui-verification/" + [Guid]::NewGuid().ToString('N'))
$unpacked = Join-Path $work "unpacked-$Arch"
Expand-Archive -LiteralPath $ZipPath -DestinationPath $unpacked
function Invoke-Offline([string]$Exe, [string[]]$Arguments, [string]$LogName) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $unpacked
    $info.Environment['DOTNET_ROOT'] = Join-Path $ReportDirectory 'empty-dotnet'
    $info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
    $info.Environment['DOTNET_ROOT_ARM64'] = $info.Environment['DOTNET_ROOT']
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $work "bundle-$Arch"
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw "$LogName timed out" }
    $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    $text | Set-Content -LiteralPath (Join-Path $ReportDirectory "$LogName.txt") -Encoding utf8NoBOM
    if ($process.ExitCode -ne 0) { throw "$LogName failed with exit code $($process.ExitCode): $text" }
    $process.Dispose()
    return $text
}
$report = Join-Path $ReportDirectory "gui-native-$Arch.json"
if (-not (Test-Path -LiteralPath (Join-Path $unpacked 'README-GUI.md'))) { throw 'GUI configuration guide missing from final ZIP' }
$clientExample = Get-Content -LiteralPath (Join-Path $unpacked 'examples/client.json') -Raw | ConvertFrom-Json
if ($clientExample.mode -ne 'client' -or $clientExample.insecure -ne $false -or $clientExample.manage_routes -ne $false -or $clientExample.paths.Count -ne 0) { throw 'GUI client example contract mismatch' }
Invoke-Offline (Join-Path $unpacked 'MQVPN.exe') @('--package-check',$unpacked,$Arch,$SourceSha,$report) 'gui-check' | Out-Null
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.sqliteRoundtrip -or -not $result.kernelUpdateRoundtrip -or $result.sourceSha -ne $SourceSha -or $result.architecture -ne $Arch) { throw 'Native GUI verification report failed' }
Invoke-Offline (Join-Path $unpacked 'MqvpnUpgrade.exe') @('--self-check') 'updater-check' | Out-Null
$applyReport = Join-Path $ReportDirectory "updater-apply-$Arch.json"
Invoke-Offline (Join-Path $unpacked 'MqvpnUpgrade.exe') @('--verify-apply',$ZipPath,(Join-Path $work 'installed'),$applyReport) 'updater-apply' | Out-Null
if (-not (Get-Content -LiteralPath $applyReport -Raw | ConvertFrom-Json).appliedAndVerified) { throw 'Updater did not verify the installed final package' }
foreach ($kernel in @('mqvpn','xray')) {
    $argument = if ($kernel -eq 'mqvpn') { '--version' } else { 'version' }
    $version = Invoke-Offline (Join-Path $unpacked "runtime/$kernel/$kernel.exe") @($argument) "$kernel-version"
    if ([string]::IsNullOrWhiteSpace($version)) { throw "$kernel did not report a version" }
}
Copy-Item -LiteralPath (Join-Path $unpacked 'package-manifest.json') -Destination (Join-Path $ReportDirectory "manifest-$Arch.json")
Write-Host "Verified final ZIP, source $SourceSha, native $Arch, self-contained SQLite, updater and core startup. No VPN or host routes were started."
