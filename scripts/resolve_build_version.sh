#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
base="$(sed -n 's/^project(mqvpn VERSION \([0-9.][0-9.]*\).*/\1/p' CMakeLists.txt)"
test -n "$base"
suffix=""
identity=""
if [[ "${GITHUB_REF:-}" == refs/tags/v* ]]; then
    tag="${GITHUB_REF#refs/tags/v}"
    if [[ "$tag" != *-* && "$tag" != "$base" ]]; then
        echo "Tag $tag does not match CMake version $base" >&2
        exit 1
    fi
else
    sha="${GITHUB_SHA:-$(git rev-parse HEAD)}"
    sha="$(git rev-parse --verify "${sha}^{commit}")"
    epoch="$(git show -s --format=%ct "$sha")"
    # UTC commit time is stable across runners, reruns and architectures.
    # GNU date on Linux/Git Bash; BSD date on Darwin.
    if commit_date="$(date -u -d "@$epoch" +%Y%m%dT%H%M%SZ 2>/dev/null)"; then
        :
    else
        commit_date="$(date -u -r "$epoch" +%Y%m%dT%H%M%SZ)"
    fi
    identity="snapshot+${commit_date}.g${sha:0:12}"
fi

# Generate an initial-cache CMake file in ignored build output. Never rewrite
# the tracked project/ABI version or push generated version commits to main.
version_file="${1:-build-version/build-version.cmake}"
mkdir -p "$(dirname -- "$version_file")"
printf 'set(MQVPN_BUILD_VERSION "%s" CACHE STRING "Build identity" FORCE)\nset(MQVPN_VERSION_SUFFIX "%s" CACHE STRING "Legacy suffix" FORCE)\n' \
    "$identity" "$suffix" > "$version_file"
version_file="$(cd -- "$(dirname -- "$version_file")" && pwd)/$(basename -- "$version_file")"
if command -v cygpath >/dev/null 2>&1; then
    version_file="$(cygpath -m "$version_file")"
fi
printf 'VERSION=%s\nVERSION_SUFFIX=%s\nBUILD_VERSION_FILE=%s\n' "${identity:-$base}" "$suffix" "$version_file"
