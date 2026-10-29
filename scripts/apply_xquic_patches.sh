#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
xquic_dir="$repo_root/third_party/xquic"
patch_dir="$repo_root/patches/xquic"

if [[ ! -e "$xquic_dir/.git" ]]; then
    echo "xquic submodule is not initialized: $xquic_dir" >&2
    exit 1
fi

if [[ ! -d "$patch_dir" ]]; then
    echo "no xquic patch directory: $patch_dir"
    exit 0
fi

shopt -s nullglob
patches=("$patch_dir"/*.patch)
if [[ ${#patches[@]} -eq 0 ]]; then
    echo "no patches to apply"
    exit 0
fi

for patch_file in "${patches[@]}"; do
    if git -C "$xquic_dir" apply --reverse --check "$patch_file" >/dev/null 2>&1; then
        echo "xquic patch already applied: ${patch_file##*/}"
        continue
    fi

    if git -C "$xquic_dir" apply --check "$patch_file" >/dev/null 2>&1; then
        git -C "$xquic_dir" apply "$patch_file"
        echo "applied xquic patch: ${patch_file##*/}"
    else
        echo "skipping patch that cannot be applied cleanly: ${patch_file##*/}"
    fi
done
