#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
xquic_dir="$repo_root/third_party/xquic"
expected="$(git -C "$repo_root" ls-tree HEAD third_party/xquic | awk '{print $3}')"
actual="$(git -C "$xquic_dir" rev-parse HEAD)"
if [[ -z "$expected" || "$actual" != "$expected" ]]; then
    echo "xquic checkout differs from the committed gitlink" >&2
    exit 1
fi
# CMake generates this one tracked header. No source overlays are allowed.
git -C "$xquic_dir" diff --exit-code HEAD -- . \
    ':(exclude)include/xquic/xqc_configure.h'
if [[ -n "$(git -C "$xquic_dir" ls-files --others --exclude-standard)" ]]; then
    echo "xquic has untracked source overlays" >&2
    exit 1
fi
bssl_expected="$(git -C "$xquic_dir" ls-tree HEAD third_party/boringssl | awk '{print $3}')"
bssl_actual="$(git -C "$xquic_dir/third_party/boringssl" rev-parse HEAD)"
if [[ -z "$bssl_expected" || "$bssl_actual" != "$bssl_expected" ]]; then
    echo "BoringSSL checkout differs from the xquic gitlink" >&2
    exit 1
fi
git -C "$xquic_dir/third_party/boringssl" diff --exit-code HEAD
if [[ -n "$(git -C "$xquic_dir/third_party/boringssl" ls-files --others --exclude-standard)" ]]; then
    echo "BoringSSL has untracked source overlays" >&2
    exit 1
fi
echo "Verified xquic=$actual BoringSSL=$bssl_actual; no source overlays"
