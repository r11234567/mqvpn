#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Refuse a partial publication even when an upload step accidentally succeeds.
set -euo pipefail
cd "${1:?artifact directory required}"
version="${2:?distribution version required}"
files=()
for arch in amd64 arm64; do
    files+=("mqvpn_${version}_${arch}.deb" "mqvpn_${version}_${arch}.tar.gz"
            "mqvpn_${version}_darwin_${arch}.tar.gz"
            "mqvpn_${version}_windows_${arch}.zip")
done
for arch in x64 arm64; do
    files+=("mqvpn_${version}_windows_gui_${arch}.zip")
done
files+=("mqvpn_${version}_android.apk")
for file in "${files[@]}"; do
    if [[ ! -s "$file" ]]; then
        echo "::error::Required distribution asset missing or empty: $file" >&2
        exit 1
    fi
done
sha256sum "${files[@]}" > SHA256SUMS
cat SHA256SUMS
