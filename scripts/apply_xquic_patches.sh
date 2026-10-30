#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
xquic_dir="$repo_root/third_party/xquic"
patch_dir="$repo_root/patches/xquic"

if [[ ! -e "$xquic_dir/.git" ]]; then
    echo "xquic submodule is not initialized: $xquic_dir" >&2
    exit 1
fi

for patch_file in "$patch_dir"/*.patch; do
    if git -C "$xquic_dir" apply --reverse --check "$patch_file" >/dev/null 2>&1; then
        echo "xquic patch already applied: ${patch_file##*/}"
        continue
    fi

    git -C "$xquic_dir" apply --check "$patch_file"
    git -C "$xquic_dir" apply "$patch_file"
    echo "applied xquic patch: ${patch_file##*/}"
done
