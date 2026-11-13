#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# run_throughput_floor_test.sh — multipath throughput regression smoke.
#
# Runs the WLB scheduler over a 2-path netem topology and asserts:
#   1. single-path throughput is sane    (catches gross tunnel breakage)
#   2. multipath >= 0.8 × single-path    (catches the class of bugs where
#                                          multi tanks below single — e.g.
#                                          the get_interest stability-timer
#                                          extend bug that pinned the
#                                          libevent tick 30 seconds out)
#
# Topology:
#   Path A:  300 Mbps / 10 ms one-way
#   Path B:   80 Mbps / 30 ms one-way
#   Sum cap ≈ 380 Mbps; healthy multi gain ~+5..+15% over single.
#   Regression signature: multi at ~0.3 × single (3-4x throughput drop).
#
# The ratio assertion is build-type invariant: ASan/UBSan slow single and
# multi uniformly, so the regression ratio is preserved. The absolute
# floor (60 Mbps) is generous enough that even a sanitizer build clears
# it on a healthy binary, while a regressed build (~25 Mbps multi under
# ASan) trips it. CI nevertheless runs this only in the release netns-tests
# job to keep the assertion margins wide.
#
# Runtime: ~35 seconds (netns/cert setup + one 10s iperf3 single + one
# 10s iperf3 multi + teardown).
#
# Usage: sudo ./run_throughput_floor_test.sh [path-to-mqvpn-binary] [--log-level LEVEL]

set -e

source "$(dirname "$0")/sanitizer_check.sh"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MQVPN=""
LOG_LEVEL="error"  # data-plane bench: keep logging minimal

while [ $# -gt 0 ]; do
    case "$1" in
        --log-level) LOG_LEVEL="$2"; shift 2 ;;
        *) [ -z "$MQVPN" ] && MQVPN="$1"; shift ;;
    esac
done

MQVPN="${MQVPN:-${SCRIPT_DIR}/../../build/mqvpn}"

if [ ! -f "$MQVPN" ]; then
    echo "error: mqvpn binary not found at $MQVPN"
    echo "Build first: mkdir build && cd build && cmake .. && make"
    exit 1
fi

for cmd in iperf3 openssl python3 tc timeout; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "error: required dependency $cmd not on PATH"
        exit 2
    fi
done

MQVPN="$(realpath "$MQVPN")"
WORK_DIR="$(mktemp -d)"
RESULTS_DIR="${THROUGHPUT_RESULTS_DIR:-${PWD}/throughput-floor-results}"
mkdir -p "$RESULTS_DIR"
RESULTS_DIR="$(realpath "$RESULTS_DIR")"
CURRENT_PHASE="single"

# Unique names so this test can run alongside other e2e tests that use
# `veth-a0` / `vpn-server` etc.
NS_SERVER="vpn-server-tput"
NS_CLIENT="vpn-client-tput"
VETH_A0="veth-a0-tput"
VETH_A1="veth-a1-tput"
VETH_B0="veth-b0-tput"
VETH_B1="veth-b1-tput"

IP_A_CLIENT="10.100.0.2/24"
IP_A_SERVER="10.100.0.1/24"
IP_B_CLIENT="10.200.0.2/24"
IP_B_SERVER="10.200.0.1/24"
SERVER_ADDR="10.100.0.1"
TUNNEL_IP="10.0.0.1"
VPN_PORT="4433"
CONTROL_PORT="4434"

NETEM_A="delay 10ms rate 300mbit"
NETEM_B="delay 30ms rate 80mbit"

IPERF_DURATION=10
# 16, not 4. On this A=300/10ms + B=80/30ms topology the multipath number is
# only a stable quantity once there are enough concurrent flows to keep the
# slow, high-RTT path busy; below that it is dominated by head-of-line timing
# that varies run to run. Measured across seven CI runs of the aggregate
# benchmark, the coefficient of variation on multipath gain falls monotonically
# with stream count:
#
#   streams=1   gain  +2.3%   CV 316%
#   streams=4   gain  +9.2%   CV 225%
#   streams=16  gain +22.6%   CV  21%
#   streams=64  gain +26.6%   CV 9.2%
#
# At 4 streams the gain is statistically indistinguishable from zero, so a
# ratio gate there fires on noise: this test read 0.75 on a build whose
# aggregate benchmark read 1.09 for the same configuration minutes earlier.
# 16 keeps the healthy/regressed gap just as wide (healthy ~1.22, the guarded
# regression ~0.32) while cutting the noise on the numerator roughly fourfold
# (multipath CV 3.4% vs 14%). The denominator was never the problem —
# single-path throughput is stable to CV 0.1% at every stream count.
IPERF_STREAMS=16

# Floor thresholds. Healthy at 16 streams sees single ~270 Mbps and multi
# ~330 Mbps. The regression we are guarding against produced multi ~84 Mbps
# (single unchanged). The ratio assertion is what actually catches the
# regression class (healthy ~1.22 vs regressed ~0.32 — wide margin against
# either floor). Keep the absolute sanity floors and relative gate fixed.
FLOOR_SINGLE_MBPS=60
FLOOR_MULTI_MBPS=60
FLOOR_MULTI_OVER_SINGLE_RATIO=0.8

SERVER_PID=""
CLIENT_PID=""
SANITIZER_FAIL=0

stop_client() {
    stop_and_check_sanitizer "$CLIENT_PID" "client" \
        "${WORK_DIR}/client-${CURRENT_PHASE}.log" || SANITIZER_FAIL=1
    CLIENT_PID=""
    sleep 1
}

stop_all() {
    stop_client
    stop_and_check_sanitizer "$SERVER_PID" "server" \
        "${WORK_DIR}/server.log" || SANITIZER_FAIL=1
    SERVER_PID=""
    sleep 1
}

cleanup() {
    local result=$?
    echo ""
    echo "Cleaning up..."
    stop_all
    if [ "$SANITIZER_FAIL" -ne 0 ]; then
        result=1
    fi
    ip netns exec "$NS_SERVER" pkill -f iperf3 2>/dev/null || true
    ip netns exec "$NS_CLIENT" pkill -f iperf3 2>/dev/null || true
    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true
    # Copy only endpoint logs. Certificate/private-key material stays temporary.
    cp "${WORK_DIR}"/*.log "$RESULTS_DIR/" 2>/dev/null || true
    printf '%s\n' "$result" >"${RESULTS_DIR}/script-exit.txt"
    chmod -R a+rX "$RESULTS_DIR"
    rm -rf "$WORK_DIR"
    if [ "$SANITIZER_FAIL" -ne 0 ]; then
        echo "FAIL: sanitizer errors detected"
    fi
    exit "$result"
}
trap cleanup EXIT

# Clean leftovers from a prior failed run
ip netns del "$NS_SERVER" 2>/dev/null || true
ip netns del "$NS_CLIENT" 2>/dev/null || true
ip link del "$VETH_A0" 2>/dev/null || true
ip link del "$VETH_B0" 2>/dev/null || true

PSK=$("$MQVPN" --genkey 2>/dev/null)
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
    -keyout "${WORK_DIR}/server.key" -out "${WORK_DIR}/server.crt" \
    -days 365 -nodes -subj "/CN=mqvpn-throughput-floor-test" 2>/dev/null

setup_topology() {
    ip netns add "$NS_SERVER"
    ip netns add "$NS_CLIENT"

    ip link add "$VETH_A0" type veth peer name "$VETH_A1"
    ip link set "$VETH_A0" netns "$NS_CLIENT"
    ip link set "$VETH_A1" netns "$NS_SERVER"
    ip netns exec "$NS_CLIENT" ip addr add "$IP_A_CLIENT" dev "$VETH_A0"
    ip netns exec "$NS_SERVER" ip addr add "$IP_A_SERVER" dev "$VETH_A1"
    ip netns exec "$NS_CLIENT" ip link set "$VETH_A0" up
    ip netns exec "$NS_SERVER" ip link set "$VETH_A1" up

    ip link add "$VETH_B0" type veth peer name "$VETH_B1"
    ip link set "$VETH_B0" netns "$NS_CLIENT"
    ip link set "$VETH_B1" netns "$NS_SERVER"
    ip netns exec "$NS_CLIENT" ip addr add "$IP_B_CLIENT" dev "$VETH_B0"
    ip netns exec "$NS_SERVER" ip addr add "$IP_B_SERVER" dev "$VETH_B1"
    ip netns exec "$NS_CLIENT" ip link set "$VETH_B0" up
    ip netns exec "$NS_SERVER" ip link set "$VETH_B1" up

    ip netns exec "$NS_CLIENT" ip link set lo up
    ip netns exec "$NS_SERVER" ip link set lo up

    # Same rp_filter relaxation + lo /32 pattern as ci_bench_aggregate.sh, so
    # Path B traffic destined to SERVER_ADDR (owned by veth-a1) is accepted
    # when it arrives on veth-b1. See scripts/ci_benchmarks/ci_bench_env.sh
    # for the long-form rationale.
    ip netns exec "$NS_SERVER" sysctl -w net.ipv4.ip_forward=1 >/dev/null
    for iface in all default lo "$VETH_A1" "$VETH_B1"; do
        ip netns exec "$NS_SERVER" \
            sysctl -w "net.ipv4.conf.${iface}.rp_filter=0" >/dev/null
    done
    for iface in all default lo "$VETH_A0" "$VETH_B0"; do
        ip netns exec "$NS_CLIENT" \
            sysctl -w "net.ipv4.conf.${iface}.rp_filter=0" >/dev/null
    done
    ip netns exec "$NS_SERVER" ip addr add "${SERVER_ADDR}/32" dev lo
    ip netns exec "$NS_CLIENT" ip route add 10.100.0.0/24 via 10.200.0.1 \
        dev "$VETH_B0" metric 200

    # netem on both ends of each pair (RTT = 2 × delay)
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_A0" root netem ${NETEM_A}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_A1" root netem ${NETEM_A}
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_B0" root netem ${NETEM_B}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_B1" root netem ${NETEM_B}

    ip netns exec "$NS_CLIENT" ping -c 1 -W 2 "$SERVER_ADDR" >/dev/null
    ip netns exec "$NS_CLIENT" ping -c 1 -W 2 10.200.0.1 >/dev/null
}

start_server() {
    ip netns exec "$NS_SERVER" "$MQVPN" \
        --mode server \
        --listen "0.0.0.0:${VPN_PORT}" \
        --subnet 10.0.0.0/24 \
        --cert "${WORK_DIR}/server.crt" \
        --key "${WORK_DIR}/server.key" \
        --auth-key "$PSK" \
        --control-port "$CONTROL_PORT" \
        --scheduler wlb \
        --log-level "$LOG_LEVEL" >"${WORK_DIR}/server.log" 2>&1 &
    SERVER_PID=$!
    sleep 2
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "  server died"
        cat "${WORK_DIR}/server.log"
        return 1
    fi
}

# $1: space-separated --path args (e.g. "$VETH_A0" or "$VETH_A0 $VETH_B0")
start_client() {
    local paths_args=""
    for iface in $1; do
        paths_args="$paths_args --path $iface"
    done
    ip netns exec "$NS_CLIENT" "$MQVPN" \
        --mode client \
        --server "${SERVER_ADDR}:${VPN_PORT}" \
        ${paths_args} \
        --auth-key "$PSK" \
        --insecure \
        --scheduler wlb \
        --log-level "$LOG_LEVEL" >"${WORK_DIR}/client-${CURRENT_PHASE}.log" 2>&1 &
    CLIENT_PID=$!
    sleep 3
    if ! kill -0 "$CLIENT_PID" 2>/dev/null; then
        echo "  client died"
        cat "${WORK_DIR}/client-${CURRENT_PHASE}.log"
        return 1
    fi
}

wait_tunnel() {
    local timeout="${1:-15}"
    local elapsed=0
    while [ "$elapsed" -lt "$timeout" ]; do
        if ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$TUNNEL_IP" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    return 1
}

snapshot_network() {
    local phase="$1" ns
    for ns in "$NS_SERVER" "$NS_CLIENT"; do
        {
            ip netns exec "$ns" ip -s link
            ip netns exec "$ns" tc -s qdisc show
            ip netns exec "$ns" ss -tin
            ip netns exec "$ns" cat /proc/net/snmp
        } >"${RESULTS_DIR}/${phase}-${ns}.network.log" 2>&1 || true
    done
    ip netns exec "$NS_SERVER" "$MQVPN" --status --control-port "$CONTROL_PORT" \
        >"${RESULTS_DIR}/${phase}-server-status.txt" \
        2>"${RESULTS_DIR}/${phase}-server-status.stderr" || true
}

# Print only Mbps to stdout so callers can capture it. Keep both endpoint
# reports, process exits and network observations even when validation fails.
run_iperf3_mbps() {
    local phase="$1" client_rc=0 server_rc=0
    local json_file="${RESULTS_DIR}/${phase}-client.json"
    local server_file="${RESULTS_DIR}/${phase}-server.json"

    snapshot_network "${phase}-before"
    ip netns exec "$NS_SERVER" timeout --foreground --kill-after=2s 30s \
        iperf3 -s -B "$TUNNEL_IP" -1 -J >"$server_file" \
        2>"${RESULTS_DIR}/${phase}-server.stderr" &
    local srv_pid=$!
    sleep 1

    ip netns exec "$NS_CLIENT" timeout --foreground --kill-after=2s 30s iperf3 \
        -c "$TUNNEL_IP" \
        -t "$IPERF_DURATION" \
        -P "$IPERF_STREAMS" \
        -R \
        --json >"$json_file" 2>"${RESULTS_DIR}/${phase}-client.stderr" || client_rc=$?

    wait "$srv_pid" || server_rc=$?
    printf 'client=%s server=%s\n' "$client_rc" "$server_rc" \
        >"${RESULTS_DIR}/${phase}-exits.log"
    snapshot_network "${phase}-after"

    PYTHONPATH="${SCRIPT_DIR}/../ci_benchmarks" python3 - \
        "$json_file" "$server_file" "$IPERF_DURATION" "$client_rc" "$server_rc" <<'PY'
import json, sys
from pathlib import Path
from ci_bench_results import InvalidMeasurement, iperf_measurement, number
client_path, server_path, duration, client_rc, server_rc = sys.argv[1:]
for path in (client_path, server_path):
    if any(line.startswith('iperf3: error')
           for line in Path(path).with_suffix('.stderr').read_text().splitlines()):
        raise InvalidMeasurement('iperf stderr reported an error')
result = iperf_measurement(json.load(open(client_path)), requested=float(duration),
                           exit_code=int(client_rc))
server = json.load(open(server_path))
if int(server_rc) != 0 or server.get('error'):
    raise InvalidMeasurement('iperf server failed: ' + str(server.get('error', server_rc)))
sent = (server.get('end') or {}).get('sum_sent') or {}
if (number(sent.get('bytes'), 'server sent bytes', positive=True)
        < result['receiver_bytes']
        or number(sent.get('seconds'), 'server send duration', positive=True)
        < float(duration) * 0.98):
    raise InvalidMeasurement('incomplete server sender evidence')
print(f"{result['receiver_mbps']:.9f}")
PY
}

# ----- Run -----

echo "================================================================"
echo "  mqvpn throughput-floor regression smoke"
echo "  Binary:   $MQVPN"
echo "  Evidence: $RESULTS_DIR"
echo "  Topology: A=300mbit/10ms, B=80mbit/30ms, theoretical sum=380mbit"
echo "  Probe:    WLB, $IPERF_STREAMS streams, ${IPERF_DURATION}s iperf3 TCP DL"
echo "  Coverage: WLB only. Stability/Recovery timer is scheduler-agnostic,"
echo "            but a future scheduler-specific pacing bug could slip past"
echo "            this test. Extend to minrtt if that ever happens."
echo "================================================================"

setup_topology
echo "OK: netns + netem configured"
start_server
echo "OK: VPN server up"

# Single-path probe (Path A only) — fast lane, regression-insensitive
echo ""
echo "[1/2] single-path (Path A only)..."
start_client "$VETH_A0"
if ! wait_tunnel 15; then
    echo "FAIL: single-path tunnel did not come up within 15s"
    exit 1
fi
SINGLE_MBPS="$(run_iperf3_mbps single)"
echo "      single-path throughput: ${SINGLE_MBPS} Mbps"
stop_client  # only the single-path client; server stays up for the multi probe
sleep 2      # match ci_bench_aggregate.sh's inter-iteration grace period

# Multipath probe (Path A + Path B) — the cell that catches the regression
echo ""
echo "[2/2] multipath (Path A + Path B)..."
CURRENT_PHASE="multi"
start_client "$VETH_A0 $VETH_B0"
if ! wait_tunnel 15; then
    echo "FAIL: multipath tunnel did not come up within 15s"
    exit 1
fi
MULTI_MBPS="$(run_iperf3_mbps multi)"
echo "      multipath throughput:   ${MULTI_MBPS} Mbps"

# ----- Assert -----

python3 - <<EOF
import sys

single = float("$SINGLE_MBPS")
multi  = float("$MULTI_MBPS")
floor_single = float("$FLOOR_SINGLE_MBPS")
floor_multi  = float("$FLOOR_MULTI_MBPS")
ratio_floor  = float("$FLOOR_MULTI_OVER_SINGLE_RATIO")

ratio = (multi / single) if single > 0 else 0.0

print()
print("Throughput floor check:")
print(f"  single = {single:.1f} Mbps  (floor = {floor_single:.0f})")
print(f"  multi  = {multi:.1f} Mbps  (floor = {floor_multi:.0f})")
print(f"  multi / single = {ratio:.2f}  (floor = {ratio_floor:.2f})")

failures = []
if single < floor_single:
    failures.append(f"single-path throughput {single:.1f} < {floor_single:.0f} Mbps")
if multi < floor_multi:
    failures.append(f"multipath throughput {multi:.1f} < {floor_multi:.0f} Mbps")
if single > 0 and ratio < ratio_floor:
    failures.append(
        f"multi/single ratio {ratio:.2f} < {ratio_floor:.2f} "
        "(multipath is below the same-run single-path baseline)"
    )

if failures:
    print()
    print("FAIL:")
    for f in failures:
        print(f"  - {f}")
    sys.exit(1)

print()
print("PASS: throughput within floors")
EOF

echo ""
echo "================================================================"
echo "  Result: PASS"
echo "================================================================"
