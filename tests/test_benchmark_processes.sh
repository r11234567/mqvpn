#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail
source "$(dirname "$0")/../scripts/ci_stress/ci_stress_env.sh"
work="$(mktemp -d)"
CI_STRESS_RAW_DIR="$work"
CI_STRESS_STOP_PAUSE=0
CI_STRESS_STOP_GRACE=1
trap 'rm -rf "$work"' EXIT
bash -c 'exit 23' &
_CS_CLIENT_PID=$!
sleep 0.1
ci_stress_stop_client
[ "$_CS_CLIENT_SANITIZER_FAIL" -eq 1 ]
echo 'PASS already-exited child failure is collected'
_CS_CLIENT_SANITIZER_FAIL=0
sleep 30 &
_CS_CLIENT_PID=$!
sleep 0.1
ci_stress_stop_client
[ "$_CS_CLIENT_SANITIZER_FAIL" -eq 0 ]
echo 'PASS ordinary SIGTERM is accepted'
bash -c 'trap "" TERM; while :; do sleep 1; done' &
_CS_CLIENT_PID=$!
sleep 0.1
ci_stress_stop_client
[ "$_CS_CLIENT_SANITIZER_FAIL" -eq 1 ]
echo 'PASS forced shutdown is reported as failure'
printf 'ERROR: AddressSanitizer: fixture\n' >"$work/asan.log"
_CS_CLIENT_SANITIZER_FAIL=0
if ci_stress_check_sanitizer; then echo 'missed sanitizer log' >&2; exit 1; fi
echo 'PASS sanitizer log diagnostics are independently checked'
MQVPN=/bin/true
ASAN_OPTIONS=detect_leaks=1
if ci_stress_is_asan_build; then echo 'ASAN_OPTIONS falsely identified ordinary binary' >&2; exit 1; fi
echo 'PASS readable binary determines instrumentation'
source "$(dirname "$0")/../scripts/ci_benchmarks/ci_bench_host.sh"
bash -c 'sleep 60 & echo $! >"$1"; wait' _ "$work/worker.pid" &
owned=$!
sleep 60 &
unrelated=$!
sleep .2
worker="$(cat "$work/worker.pid")"
_CB_HOST_PIDS="$owned"
ci_bench_host_stop
kill -0 "$unrelated"
if [ -r "/proc/$worker/status" ] && ! grep -q '^State:.*Z' "/proc/$worker/status"; then
    echo 'owned host worker survived stop' >&2; exit 1
fi
kill "$unrelated"
wait "$unrelated" 2>/dev/null || true
if ci_bench_host_start nonexistent; then echo 'unknown host state accepted' >&2; exit 1; fi
echo 'PASS host cleanup stops owned descendants and preserves unrelated work'
