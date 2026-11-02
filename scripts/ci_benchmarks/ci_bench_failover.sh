#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "${SCRIPT_DIR}/ci_bench_env.sh"
MQVPN="${1:-${MQVPN}}"
trap ci_bench_cleanup EXIT
ci_bench_check_deps
FAILED=0
for sched in wlb minrtt; do
    ci_bench_setup_netns
    ci_bench_apply_netem 'delay 10ms rate 150mbit' 'delay 30ms rate 150mbit'
    ci_bench_start_server "$sched"
    ci_bench_start_client "--path $VETH_A0 --path $VETH_B0" "$sched"
    ci_bench_wait_tunnel 30
    python3 "${SCRIPT_DIR}/ci_bench_failover_measure.py" run \
        --target "$TUNNEL_SERVER_IP" --output "${CI_BENCH_RAW_DIR}/failover-$sched" \
        --client-ns "$NS_CLIENT" --server-ns "$NS_SERVER" \
        --dev-a-client "$VETH_A0" --dev-a-server "$VETH_A1" \
        --dev-b-client "$VETH_B0" --dev-b-server "$VETH_B1" \
        --underlay-subnet "$IP_A_SUBNET" --gateway-b "$IP_B_SERVER_ADDR" || FAILED=1
    ci_bench_stop_vpn
    ci_bench_cleanup_stale
done
OUTPUT="${CI_BENCH_RESULTS}/failover_$(date -u +%Y%m%d_%H%M%S).json"
python3 - "$CI_BENCH_RAW_DIR" "$OUTPUT" "$CI_BENCH_COMMIT" <<'PY'
import datetime, json, pathlib, sys
raw, output, commit = sys.argv[1:]
results = {s:json.loads((pathlib.Path(raw)/('failover-'+s)/'measurement.json').read_text()) for s in ('wlb','minrtt')}
doc = {'test':'failover', 'measurement_version':2, 'commit':commit,
       'timestamp':datetime.datetime.now(datetime.timezone.utc).isoformat(), 'results':results,
       'ttf_definition':'first 1.5s sustained receiver rate >=75 Mbps after actual fault',
       'ttr_definition':'first 1.5s sustained receiver rate >=90% prefault after actual recovery',
       'measurement_complete':all(r.get('measurement_complete',False) for r in results.values()),
       'performance_health':'inconclusive' if any(not r.get('measurement_complete') for r in results.values())
          else 'regression' if any(r.get('performance_health')=='regression' for r in results.values()) else 'pass'}
pathlib.Path(output).write_text(json.dumps(doc, indent=2, allow_nan=False)+'\n')
PY
ci_bench_sanity_check "$OUTPUT" 'receiver failover' || FAILED=1
exit "$FAILED"
