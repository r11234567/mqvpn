#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Continuous upload with receiver completion, progress and resource evidence.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "${SCRIPT_DIR}/ci_stress_env.sh"
MQVPN="${1:-$MQVPN}"
DURATION="${CI_STRESS_LONG_DURATION:-900}"
IPERF_PARALLEL="${CI_STRESS_IPERF_PARALLEL:-4}"
MAX_STALL="${CI_STRESS_MAX_STALL_SEC:-3}"
DIRECTION="${CI_STRESS_DIRECTION:-UL}"
[[ "$DIRECTION" = UL || "$DIRECTION" = DL ]] || { echo 'invalid direction' >&2; exit 2; }
REVERSE=()
[ "$DIRECTION" != DL ] || REVERSE=(-R)
trap ci_stress_cleanup EXIT
ci_stress_check_deps
[[ "$IPERF_PARALLEL" =~ ^[1-9][0-9]*$ ]] && [[ "$MAX_STALL" =~ ^[1-9][0-9]*$ ]] || {
    echo 'parallel count and progress deadline must be positive integers' >&2; exit 2;
}
[[ "$DURATION" =~ ^[1-9][0-9]*$ ]] && [ "$DURATION" -gt $((2 * CI_STRESS_MONITOR_INTERVAL)) ] || {
    echo 'duration must provide at least three monitor samples' >&2; exit 2;
}
ci_stress_setup_netns
ci_stress_apply_netem
ci_stress_start_server wlb
ci_stress_start_client "--path $VETH_A0 --path $VETH_B0" wlb
ci_stress_wait_tunnel 30

IPERF_JSON="${CI_STRESS_RAW_DIR}/iperf.json"
SERVER_MON_LOG="${CI_STRESS_RAW_DIR}/server-monitor.log"
CLIENT_MON_LOG="${CI_STRESS_RAW_DIR}/client-monitor.log"
PROGRESS_LOG="${CI_STRESS_RAW_DIR}/receiver-progress.log"
ip netns exec "$NS_SERVER" iperf3 -s -1 -B "$TUNNEL_SERVER_IP" >"${CI_STRESS_RAW_DIR}/iperf-server.log" 2>&1 &
IPERF_SERVER_PID=$!
sleep 1
ci_stress_monitor_start "$_CS_SERVER_PID" "$SERVER_MON_LOG"
ci_stress_monitor_start "$_CS_CLIENT_PID" "$CLIENT_MON_LOG"

echo "Continuous transfer: requested ${DURATION}s, ${IPERF_PARALLEL} TCP streams"
ip netns exec "$NS_CLIENT" timeout --kill-after=5 $((DURATION + 30)) \
    iperf3 -c "$TUNNEL_SERVER_IP" -t "$DURATION" -P "$IPERF_PARALLEL" "${REVERSE[@]}" --json \
    >"$IPERF_JSON" 2>&1 &
IPERF_CLIENT_PID=$!
STARTED=$SECONDS
LAST_PROGRESS=$SECONDS
LAST_BYTES=0
MAX_GAP=0
BUSINESS_FAILED=0
while kill -0 "$IPERF_CLIENT_PID" 2>/dev/null; do
    if ! kill -0 "$_CS_SERVER_PID" 2>/dev/null || ! kill -0 "$_CS_CLIENT_PID" 2>/dev/null; then
        echo 'FAIL: VPN process exited during transfer'
        BUSINESS_FAILED=1
        break
    fi
    if ! RECEIVED="$(ci_stress_tun_rx_bytes "$DIRECTION")" || ! [[ "$RECEIVED" =~ ^[0-9]+$ ]]; then
        echo 'FAIL: receiver progress unavailable'
        BUSINESS_FAILED=1
        break
    fi
    if [ "$RECEIVED" -gt "$LAST_BYTES" ]; then
        LAST_PROGRESS=$SECONDS
        LAST_BYTES="$RECEIVED"
    fi
    GAP=$((SECONDS - LAST_PROGRESS))
    if [ "$GAP" -gt "$MAX_GAP" ]; then MAX_GAP="$GAP"; fi
    printf '%s %s\n' "$((SECONDS - STARTED))" "$RECEIVED" >>"$PROGRESS_LOG"
    if [ "$GAP" -gt "$MAX_STALL" ]; then
        echo "FAIL: no inner receiver progress for ${GAP}s"
        BUSINESS_FAILED=1
        break
    fi
    sleep 1
done
if [ "$BUSINESS_FAILED" -ne 0 ]; then kill "$IPERF_CLIENT_PID" 2>/dev/null || true; fi
IPERF_EXIT=0
wait "$IPERF_CLIENT_PID" || IPERF_EXIT=$?
kill "$IPERF_SERVER_PID" 2>/dev/null || true
wait "$IPERF_SERVER_PID" 2>/dev/null || true
cp "$IPERF_JSON" "${IPERF_JSON}.client.stdout.log"
python3 "${SCRIPT_DIR}/../ci_benchmarks/ci_bench_results.py" record "$IPERF_JSON" \
    --duration "$DURATION" --exit-code "$IPERF_EXIT" \
    --server-log "${CI_STRESS_RAW_DIR}/iperf-server.log"
THROUGHPUT="$(python3 "${SCRIPT_DIR}/../ci_benchmarks/ci_bench_results.py" throughput "$IPERF_JSON")"
if [ "$THROUGHPUT" = NA ] || ! awk -v r="$THROUGHPUT" 'BEGIN {exit !(r+0>0)}'; then
    BUSINESS_FAILED=1
fi
ci_stress_monitor_stop
RESOURCE_FAILED=0
ci_stress_check_resources "$SERVER_MON_LOG" server || RESOURCE_FAILED=1
ci_stress_check_resources "$CLIENT_MON_LOG" client || RESOURCE_FAILED=1
ci_stress_stop_vpn
ci_stress_check_sanitizer || RESOURCE_FAILED=1

OUTPUT_FILE="${CI_STRESS_RESULTS}/long_running_${DIRECTION}_$(date -u +%Y%m%d_%H%M%S).json"
PYTHONPATH="${SCRIPT_DIR}/../ci_benchmarks" python3 - "$IPERF_JSON" "$SERVER_MON_LOG" "$CLIENT_MON_LOG" "$OUTPUT_FILE" \
    "$CI_STRESS_COMMIT" "$BUSINESS_FAILED" "$RESOURCE_FAILED" "$MAX_GAP" "$DIRECTION" <<'PY'
import datetime, json, pathlib, sys
from ci_bench_health import tcp_health
raw, server, client, output, commit, business, resource, gap, direction = sys.argv[1:]
sample = json.load(open(raw))['_mqvpn']
health = tcp_health(json.load(open(raw)))
def resources(path):
    return json.load(open(path + '.resources.json'))
result = {
    'test': 'long_running', 'commit': commit, 'direction': direction,
    'timestamp': datetime.datetime.now(datetime.timezone.utc).isoformat(timespec='seconds'),
    'duration_sec': sample['requested_duration_s'],
    'requested_duration_sec': sample['requested_duration_s'],
    'observed_duration_sec': sample.get('observed_duration_s'),
    'throughput_mbps': sample.get('receiver_mbps'),
    'receiver_bytes': sample.get('receiver_bytes'),
    'iperf_exit_code': sample['exit_code'],
    'max_no_progress_sec': int(gap),
    'measurement_complete': bool(sample['measurement_complete'] and not int(business)),
    'measurement_error': sample.get('failure_reason'),
    'server_rss': resources(server), 'client_rss': resources(client),
    'raw_artifact': raw,
    'performance_health': health,
    'status': 'fail' if int(business) or int(resource) or health['status'] != 'pass' else 'pass'
}
pathlib.Path(output).write_text(json.dumps(result, indent=2) + '\n')
print(json.dumps(result, indent=2))
sys.exit(result['status'] != 'pass')
PY
[ "$BUSINESS_FAILED" -eq 0 ] && [ "$RESOURCE_FAILED" -eq 0 ]
