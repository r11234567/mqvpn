# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors

#requires -Version 7.2
$ErrorActionPreference = 'Stop'
# The official page publishes the current signed archive and its SHA-256 together.
$page = Invoke-WebRequest -Uri 'https://www.wintun.net/' -UserAgent 'MQVPN-Packaging'
$download = [regex]::Match($page.Content,
    'href="(?<path>builds/wintun-(?<version>[0-9.]+)\.zip)"[\s\S]*?SHA2-256:\s*<code>(?<hash>[0-9a-fA-F]{64})</code>')
if (-not $download.Success) { throw 'Official Wintun download or SHA-256 was not found' }
@{ version = $download.Groups['version'].Value
   url = "https://www.wintun.net/$($download.Groups['path'].Value)"
   sha256 = $download.Groups['hash'].Value.ToLowerInvariant() }
