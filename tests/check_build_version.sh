#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Exercise the release resolver, generated CMake cache and actual library
# version function together, without downloading or building dependencies.
set -euo pipefail
unset MQVPN_BUILD_CHANNEL
ROOT="$(cd -- "$(dirname -- "$0")/.." && pwd)"
WORK="$(mktemp -d)"
cleanup() {
    local result=$?
    set +e
    if [ "$result" -ne 0 ]; then
        echo "Build identity verification failed (exit $result)"
        uname -a
        cmake --version
        for log in config.log build.log reject.log tag.log; do
            if [ -f "$WORK/$log" ]; then
                printf '\n--- %s ---\n' "$log"
                cat "$WORK/$log"
            fi
        done
        if [ -f "$WORK/snapshot/CMakeCache.txt" ]; then
            grep -E '^(MQVPN_|CMAKE_MAKE_PROGRAM:|CMAKE_GENERATOR:)' "$WORK/snapshot/CMakeCache.txt" || true
            cat "$WORK/snapshot/metadata"
        fi
    fi
    rm -rf "$WORK"
    exit "$result"
}
trap cleanup EXIT
mkdir -p "$WORK/src/scripts" "$WORK/src/include"
cp "$ROOT/scripts/resolve_build_version.sh" "$WORK/src/scripts/"
cp "$ROOT/cmake/BuildVersion.cmake" "$WORK/src/"
cp "$ROOT/src/mqvpn_util.c" "$WORK/src/"
cp "$ROOT/include/libmqvpn.h" "$WORK/src/include/"
cat > "$WORK/src/probe.c" <<'C'
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors
#include "libmqvpn.h"
#include <stdio.h>
int main(void) { puts(mqvpn_version_string()); return 0; }
C
cat > "$WORK/src/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.10)
project(mqvpn VERSION 0.19.0 LANGUAGES C)
include(BuildVersion.cmake)
add_executable(probe probe.c mqvpn_util.c)
target_include_directories(probe PRIVATE include)
file(WRITE "${CMAKE_BINARY_DIR}/metadata" "${MQVPN_FULL_VERSION}\n${MQVPN_PACKAGE_VERSION}\n${PROJECT_VERSION}\n")
install(TARGETS probe DESTINATION bin)
set(CPACK_PACKAGE_VERSION "${MQVPN_PACKAGE_VERSION}")
set(CPACK_DEBIAN_FILE_NAME "mqvpn_${MQVPN_ARTIFACT_VERSION}_amd64.deb")
set(CPACK_DEBIAN_PACKAGE_MAINTAINER "mqvpn")
include(CPack)
CMAKE
cd "$WORK/src"
git init -q
git add CMakeLists.txt
GIT_AUTHOR_DATE='2001-02-03T04:05:06+0000' GIT_COMMITTER_DATE='2001-02-03T04:05:06+0000' \
    git -c user.name=Test -c user.email=test@example.invalid commit -qm fixture
sha="$(git rev-parse HEAD)"
expected="snapshot-20010203-0405-UTC"
display="s.20010203.0405UTC"

configure() {
    cmake -S . -B "$WORK/$1" -DCMAKE_BUILD_TYPE=Debug \
        -DCMAKE_C_FLAGS='-Wall -Wextra -Werror' "${@:2}" > "$WORK/config.log" 2>&1 || {
        cat "$WORK/config.log"; return 1;
    }
}
check_binary() {
    # Each identity compiles fresh, just like isolated release jobs. Apple's
    # Make 3.81 ignores subsecond mtimes and can otherwise reuse snapshot
    # objects when flags.make changes in the same second. Cache/metadata
    # assertions below independently verify same-directory reconfiguration.
    cmake --build "$WORK/$1" --clean-first --verbose > "$WORK/build.log" 2>&1 || { cat "$WORK/build.log"; return 1; }
    actual="$("$WORK/$1/probe")"
    [ "$actual" = "$2" ] || { echo "Wrong library identity: $actual, expected $2"; exit 1; }
}
reject() {
    if cmake -S . -B "$WORK/rejected-$1" "${@:2}" > "$WORK/reject.log" 2>&1; then
        echo "Unexpected acceptance: $1"; exit 1
    fi
    grep -Eq 'Invalid|cannot also' "$WORK/reject.log"
}

GITHUB_REF=refs/heads/main GITHUB_SHA="$sha" TZ=Pacific/Kiritimati \
    bash scripts/resolve_build_version.sh > "$WORK/snapshot.env"
grep -Fx "VERSION=$expected" "$WORK/snapshot.env"
grep -Fx "VERSION_DISPLAY=$display" "$WORK/snapshot.env"
version_file="$(sed -n 's/^BUILD_VERSION_FILE=//p' "$WORK/snapshot.env")"
[ -f "$version_file" ]
# The resolver must not pre-create build/ (the Windows release uses mkdir).
[ ! -d build ]
GITHUB_REF=refs/heads/main GITHUB_SHA="$sha" TZ=America/Los_Angeles \
    bash scripts/resolve_build_version.sh > "$WORK/repeat.env"
cmp "$WORK/snapshot.env" "$WORK/repeat.env"
configure snapshot -C "$version_file"
check_binary snapshot "$display"
printf '%s\n' "$display" "0~$expected" '0.19.0' > "$WORK/expected.metadata"
cmp "$WORK/expected.metadata" "$WORK/snapshot/metadata"

# A tag in the same workspace clears any cached snapshot identity.
GITHUB_REF=refs/tags/v0.19.0 bash scripts/resolve_build_version.sh > "$WORK/tag.env"
grep -Fx 'VERSION=0.19.0' "$WORK/tag.env"
configure snapshot -C "$version_file"
grep -Fx 'MQVPN_BUILD_VERSION:STRING=' "$WORK/snapshot/CMakeCache.txt"
grep -Fx 'MQVPN_VERSION_SUFFIX:STRING=' "$WORK/snapshot/CMakeCache.txt"
printf '%s\n' '0.19.0' '0.19.0' '0.19.0' > "$WORK/expected.metadata"
cmp "$WORK/expected.metadata" "$WORK/snapshot/metadata"
check_binary snapshot 0.19.0
configure default
check_binary default 0.19.0
configure legacy -DMQVPN_VERSION_SUFFIX=+downstream.1
check_binary legacy 0.19.0+downstream.1
if GITHUB_REF=refs/tags/v0.18.0 bash scripts/resolve_build_version.sh > "$WORK/tag.log" 2>&1; then
    echo 'Mismatched tag accepted'; exit 1
fi
grep -F 'does not match CMake version' "$WORK/tag.log"
GITHUB_REF=refs/tags/v0.19.0-rc.1 bash scripts/resolve_build_version.sh > "$WORK/tag.env"
grep -Fx 'VERSION=0.19.0-rc.1' "$WORK/tag.env"
configure prerelease -C "$version_file"
check_binary prerelease 0.19.0-rc.1
printf '%s\n' '0.19.0-rc.1' '0.19.0~rc.1' '0.19.0' > "$WORK/expected.metadata"
cmp "$WORK/expected.metadata" "$WORK/prerelease/metadata"
if GITHUB_REF=refs/tags/v0.18.0-rc.1 bash scripts/resolve_build_version.sh > "$WORK/tag.log" 2>&1; then
    echo 'Mismatched prerelease tag accepted'; exit 1
fi
if MQVPN_BUILD_CHANNEL=release GITHUB_REF=refs/heads/main bash scripts/resolve_build_version.sh > "$WORK/tag.log" 2>&1; then
    echo 'Branch accepted as a release'; exit 1
fi
if MQVPN_BUILD_CHANNEL=snapshot GITHUB_REF=refs/tags/v0.19.0 bash scripts/resolve_build_version.sh > "$WORK/tag.log" 2>&1; then
    echo 'Tag accepted as a snapshot'; exit 1
fi
reject malformed -DMQVPN_BUILD_VERSION=snapshot+invalid
reject bad-width -DMQVPN_BUILD_VERSION=snapshot-20010203-04050-UTC
reject mixed -DMQVPN_BUILD_VERSION="$expected" -DMQVPN_VERSION_SUFFIX=+legacy
reject bad-suffix -DMQVPN_VERSION_SUFFIX=invalid

if command -v dpkg-deb > /dev/null 2>&1 && command -v dpkg > /dev/null 2>&1; then
    GITHUB_REF=refs/heads/main GITHUB_SHA="$sha" bash scripts/resolve_build_version.sh > /dev/null
    configure snapshot -C "$version_file"
    grep -Fx "MQVPN_BUILD_VERSION:STRING=$expected" "$WORK/snapshot/CMakeCache.txt"
    check_binary snapshot "$display"
    (cd "$WORK/snapshot" && cpack -G DEB > "$WORK/package.log" 2>&1) || {
        cat "$WORK/package.log"; exit 1;
    }
    package_version="$(dpkg-deb -f "$WORK/snapshot/"*.deb Version)"
    [ -f "$WORK/snapshot/mqvpn_${expected}_amd64.deb" ]
    [ "$package_version" = "0~$expected" ]
    dpkg --validate-version "$package_version"
    dpkg --compare-versions "$package_version" lt 0.19.0
fi
echo 'PASS: snapshot/tag identities, UTC determinism, library version, cache reset, validation and Debian metadata'
