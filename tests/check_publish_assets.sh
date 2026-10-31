#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
for version in snapshot-20010203-0405-UTC 0.19.0 0.19.0-rc.1; do
    dir="$WORK/$version"
    mkdir -p "$dir"
    for arch in amd64 arm64; do
        for suffix in "${arch}.deb" "${arch}.tar.gz" "darwin_${arch}.tar.gz" "windows_${arch}.zip"; do
            printf fixture > "$dir/mqvpn_${version}_${suffix}"
        done
    done
    for arch in x64 arm64; do
        printf fixture > "$dir/mqvpn_${version}_windows_gui_${arch}.zip"
    done
    printf fixture > "$dir/mqvpn_${version}_android.apk"
    # AAB and test evidence must not enter the user-facing checksum manifest.
    printf bundle > "$dir/mqvpn_${version}_android.aab"
    bash "$ROOT/scripts/prepare_publish_assets.sh" "$dir" "$version" > /dev/null
    [ "$(wc -l < "$dir/SHA256SUMS")" -eq 11 ]
    (cd "$dir" && sha256sum --check SHA256SUMS > /dev/null)
    for file in "$dir"/mqvpn_*; do
        [[ "$file" == *.aab ]] && continue
        mv "$file" "$file.missing"
        if bash "$ROOT/scripts/prepare_publish_assets.sh" "$dir" "$version" > "$WORK/error" 2>&1; then
            echo "Incomplete publication accepted: $file"; exit 1
        fi
        grep -F 'Required distribution asset missing or empty' "$WORK/error" > /dev/null
        mv "$file.missing" "$file"
    done
    : > "$dir/mqvpn_${version}_android.apk"
    if bash "$ROOT/scripts/prepare_publish_assets.sh" "$dir" "$version" > /dev/null 2>&1; then
        echo 'Empty APK accepted'; exit 1
    fi
done
echo 'PASS: snapshot/release/prerelease complete assets, checksums, each missing platform, empty APK and AAB exclusion'
