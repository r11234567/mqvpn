#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
base="$(sed -n 's/^project(mqvpn VERSION \([0-9.][0-9.]*\).*/\1/p' CMakeLists.txt)"
test -n "$base"
suffix=""
if [[ "${GITHUB_REF:-}" == refs/tags/v* ]]; then
    tag="${GITHUB_REF#refs/tags/v}"
    if [[ "$tag" != *-* && "$tag" != "$base" ]]; then
        echo "Tag $tag does not match CMake version $base" >&2
        exit 1
    fi
else
    sha="${GITHUB_SHA:-$(git rev-parse HEAD)}"
    commit_date="$(git show -s --format=%cd --date=format:%Y%m%d "$sha")"
    suffix="+snapshot.${commit_date}.g${sha:0:12}"
fi
printf 'VERSION=%s%s\nVERSION_SUFFIX=%s\n' "$base" "$suffix" "$suffix"
