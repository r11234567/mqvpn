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
channel="${MQVPN_BUILD_CHANNEL:-}"
if [[ -n "$channel" && "$channel" != snapshot && "$channel" != release ]]; then
    echo "Invalid build channel: $channel" >&2
    exit 1
fi
if [[ "$channel" == release && "${GITHUB_REF:-}" != refs/tags/v* ]]; then
    echo "Release builds require a v* tag; select a tag when dispatching Release" >&2
    exit 1
fi
snapshot_code=""
if [[ "${GITHUB_REF:-}" == refs/tags/v* ]]; then
    if [[ "$channel" == snapshot ]]; then
        echo "Snapshot builds require a branch, not a release tag" >&2
        exit 1
    fi
    tag="${GITHUB_REF#refs/tags/v}"
    if [[ "${tag%%-*}" != "$base" ]]; then
        echo "Tag $tag does not match CMake version $base" >&2
        exit 1
    fi
    suffix="${tag#"$base"}"
    if [[ -n "$suffix" && ! "$suffix" =~ ^-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*$ ]]; then
        echo "Invalid prerelease tag: $tag" >&2
        exit 1
    fi
else
    sha="${GITHUB_SHA:-$(git rev-parse HEAD)}"
    sha="$(git rev-parse --verify "${sha}^{commit}")"
    epoch="$(git show -s --format=%ct "$sha")"
    # UTC commit time is stable across runners, reruns and architectures.
    # GNU date on Linux/Git Bash; BSD date on Darwin.
    if commit_date="$(date -u -d "@$epoch" +%Y%m%d-%H%M-UTC 2>/dev/null)"; then
        :
    else
        commit_date="$(date -u -r "$epoch" +%Y%m%d-%H%M-UTC)"
    fi
    identity="snapshot-${commit_date}"
    snapshot_code="$((epoch / 60))"
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
version="${identity:-${base}${suffix}}"
display="$version"
if [[ -n "$identity" ]]; then
    display="snapshot ${commit_date:0:4}-${commit_date:4:2}-${commit_date:6:2} ${commit_date:9:2}:${commit_date:11:2} UTC"
fi
printf 'VERSION=%s\nVERSION_DISPLAY=%s\nVERSION_SUFFIX=%s\nBUILD_VERSION_FILE=%s\nSNAPSHOT_VERSION_CODE=%s\n' \
    "$version" "$display" "$suffix" "$version_file" "$snapshot_code"
