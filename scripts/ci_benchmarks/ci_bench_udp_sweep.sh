#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Receiver goodput and loss, UL/DL, small packets and bulk. Keep every raw run.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# Keep full application diagnostics for the intermittent control-channel error.
# No packet-level trace logging is enabled during throughput measurements.
CI_BENCH_LOG_LEVEL="${CI_BENCH_LOG_LEVEL:-info}"
source "${SCRIPT_DIR}/ci_bench_env.sh"
MQVPN="${1:-${MQVPN}}"
DURATION="${CI_BENCH_UDP_SEC:-10}"
REPEATS="${CI_BENCH_UDP_REPEATS:-3}"
[[ "$REPEATS" =~ ^[0-9]+$ ]] && [ "$REPEATS" -ge 3 ] || exit 2
export CI_BENCH_UDP_REPEATS="$REPEATS"
read -r -a DIRECTIONS <<< "${CI_BENCH_UDP_DIRECTIONS:-DL UL}"
read -r -a SIZES <<< "${CI_BENCH_UDP_SIZES:-1100 50}"
trap ci_bench_cleanup EXIT
ci_bench_check_deps
FAILED=0
for dir in "${DIRECTIONS[@]}"; do
    [[ "$dir" = DL || "$dir" = UL ]] || exit 2
    for size in "${SIZES[@]}"; do
        [[ "$size" =~ ^[1-9][0-9]*$ ]] || exit 2
        if [ "$size" -le 128 ]; then
            read -r -a RATES <<< "${CI_BENCH_UDP_SMALL_RATES:-0.2 0.8 2 5 10}"
        else
            read -r -a RATES <<< "${CI_BENCH_UDP_RATES:-10 50 100 200 250 300 350 380}"
        fi
        ROWS="${CI_BENCH_RAW_DIR}/udp-${dir}-${size}.jsonl"
        : >"$ROWS"
        for cond in single wlb minrtt; do
            ci_bench_setup_netns
            ci_bench_apply_netem
            sched="$cond"; paths="--path $VETH_A0 --path $VETH_B0"
            if [ "$cond" = single ]; then sched=wlb; paths="--path $VETH_A0"; fi
            ci_bench_start_server "$sched"
            ci_bench_start_client "$paths" "$sched"
            ci_bench_wait_tunnel 30
            for rate in "${RATES[@]}"; do
              for ((sample=1; sample<=REPEATS; sample++)); do
                jf="$(CI_BENCH_IPERF_LEN="$size" ci_bench_run_iperf UDP "$dir" "$DURATION" 1 "${rate}M")"
                point="$(python3 "${SCRIPT_DIR}/ci_bench_metrics.py" udp "$jf" --rate "$rate")" || FAILED=1
                python3 - "$cond" "$point" "$sample" >>"$ROWS" <<'PY'
import json, sys
p = json.loads(sys.argv[2]); p['condition'] = sys.argv[1]
p['repeat'] = int(sys.argv[3])
print(json.dumps(p))
PY
                rm -f "$jf"
              done
            done
            ci_bench_stop_vpn
            ci_bench_cleanup_stale
        done
        output="${CI_BENCH_RESULTS}/udp_sweep_${dir}_${size}_$(date -u +%Y%m%d_%H%M%S).json"
        PYTHONPATH="$SCRIPT_DIR" python3 - "$ROWS" "$output" "$dir" "$size" "$DURATION" "${RATES[*]}" <<'PY'
import datetime, json, os, sys
from ci_bench_metrics import saturation
rows, output, direction, size, duration, rates = sys.argv[1:]
points = [json.loads(s) for s in open(rows)]
doc = {'test': 'udp_sweep', 'measurement_version': 3, 'repeats': int(os.environ['CI_BENCH_UDP_REPEATS']), 'commit': os.environ.get('CI_BENCH_COMMIT'),
       'timestamp': datetime.datetime.now(datetime.timezone.utc).isoformat(),
       'direction': direction, 'packet_size_bytes': int(size), 'duration_sec': int(duration),
       'sweep_rates_mbps': [float(r) for r in rates.split()], 'results': {},
       'scope': os.environ.get('CI_BENCH_TEST_SCOPE','udp_sweep'),
       'measurement': 'receiver bytes/time; all repeated samples retained; any incomplete repeat fails'}
for cond in ('single', 'wlb', 'minrtt'):
    pts = [p for p in points if p['condition'] == cond]
    doc['results'][cond] = {'points': pts, **saturation(pts)}
expected = {(c, float(rate), i) for c in ('single', 'wlb', 'minrtt')
            for rate in rates.split() for i in range(1, int(os.environ['CI_BENCH_UDP_REPEATS'])+1)}
actual = [(p['condition'], p['rate'], p['repeat']) for p in points]
doc['measurement_complete'] = (len(actual) == len(expected) and set(actual) == expected
                               and all(p['status'] == 'ok' for p in points))
with open(output, 'w') as f: json.dump(doc, f, indent=2, allow_nan=False)
PY
        ci_bench_sanity_check "$output" 'UDP receiver sweep' || FAILED=1
    done
done
exit "$FAILED"
