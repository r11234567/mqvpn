#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Real iperf3 JSON/schema integration, independent of a VPN build. Needs root.
set -euo pipefail
source "$(dirname "$0")/../scripts/ci_benchmarks/ci_bench_env.sh"
[ "$(ci_bench_config_bool on)" = true ]
[ "$(ci_bench_config_bool off)" = false ]
if ci_bench_config_bool invalid >/dev/null 2>&1; then exit 1; fi
echo 'PASS arm booleans use the actual config grammar'
[ "$(python3 -c 'import os; print(os.environ["CI_BENCH_SCHEDULER"])')" = "$CI_BENCH_SCHEDULER" ]
echo 'PASS child metadata sees the configured scheduler'
work="$(mktemp -d)"
NS_SERVER="bench-harness-$$"
NS_CLIENT="$NS_SERVER"
CI_BENCH_RAW_DIR="$work/raw"
mkdir -p "$CI_BENCH_RAW_DIR"
trap 'ip netns pids "$NS_SERVER" | xargs -r kill 2>/dev/null || true; ip netns del "$NS_SERVER"; rm -rf "$work"' EXIT
ip netns add "$NS_SERVER"
ip -n "$NS_SERVER" link set lo up
CI_BENCH_IPERF_TARGET=127.0.0.1
for proto in TCP UDP; do
    for direction in UL DL; do
        sample="$(ci_bench_run_iperf "$proto" "$direction" 2 1 1000000)"
        rate="$(ci_bench_parse_throughput "$sample")"
        [ "$rate" != NA ] || { cat "$sample"; exit 1; }
        python3 - "$sample" "$proto" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d['_mqvpn']['measurement_complete'], d
assert d['_mqvpn']['receiver_bytes'] > 0, d
assert d['_mqvpn']['observed_duration_s'] >= 1.8, d
PY
        if [ "$proto" = UDP ]; then
            quality="$(ci_bench_parse_udp_quality "$sample")"
            [ "${quality%% *}" != NA ] || { cat "$sample"; exit 1; }
        fi
        echo "PASS real $proto $direction receiver=$rate Mbps"
        rm -f "$sample"
    done
done
[ "$(find "$CI_BENCH_RAW_DIR" -name '*.json' | wc -l)" -eq 4 ]
echo 'PASS raw evidence survives caller cleanup'
