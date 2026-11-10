#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Start with a small link, before post-handshake PMTUD can help bootstrap.
# Usage: run_pmtud_small_mtu_test.sh [binary] [--ipv6] [--mixed-small-first]
#        [--mixed-large-first] [--reject-inner-ipv6]
set -euo pipefail
source "$(dirname "$0")/sanitizer_check.sh"
source "$(dirname "$0")/e2e_lib.sh"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MQVPN="$SCRIPT_DIR/../../build/mqvpn"
IPV6=0 MIXED=0 SMALL_FIRST=1 REJECT6=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --ipv6) IPV6=1 ;;
        --mixed-small-first) MIXED=1; SMALL_FIRST=1 ;;
        --mixed-large-first) MIXED=1; SMALL_FIRST=0 ;;
        --reject-inner-ipv6) REJECT6=1 ;;
        --*) echo "Unknown option $1"; exit 2 ;;
        *) MQVPN="$1" ;;
    esac
    shift
done
MQVPN="$(realpath "$MQVPN")"
for tool in ip ethtool tc nft python3 iperf3 openssl timeout nstat tcpdump; do
    command -v "$tool" >/dev/null || { echo "Missing tool $tool"; exit 1; }
done
check_receiver() {
    python3 - "$SCRIPT_DIR/../ci_benchmarks" "$1" "$2" <<'PY'
import json
from pathlib import Path
import sys
sys.path.insert(0, sys.argv[1])
from ci_bench_results import iperf_measurement, number
data = json.loads(Path(sys.argv[2]).read_text())
if sys.argv[3] == 'reverse-server':
    # iperf3's reverse server reports no remote receiver record. Its local
    # sender validates server execution. The client proves delivered goodput.
    assert not data.get('error'), data
    sent = data['end']['sum_sent']
    assert number(sent.get('seconds'), 'sender seconds') >= 3.92, sent
    assert number(sent.get('bytes'), 'sender bytes') >= 500000, sent
    sys.exit(0)
measurement = iperf_measurement(data, requested=4)
minimum_bytes = 1500000 if sys.argv[3] == 'udp' else 5000000
minimum_mbps = 1 if sys.argv[3] == 'udp' else 10
assert measurement['receiver_bytes'] >= minimum_bytes, measurement
assert measurement['receiver_mbps'] >= minimum_mbps, measurement
if sys.argv[3] == 'udp':
    summary = measurement['receiver']
    assert measurement['receiver_bytes'] >= 1500000, measurement
    assert number(summary.get('packets'), 'receiver packets') >= 2900, summary
    assert number(summary.get('lost_percent'), 'receiver loss') <= 1, summary
print(sys.argv[2], measurement)
PY
}
WORK_DIR="$(mktemp -d)"
NS_CLIENT="mq-pms-c-$$" NS_SERVER="mq-pms-s-$$"
CLIENT_PID='' SERVER_PID='' IPERF_PID=''
CAPTURE_PIDS=()
cleanup() {
    local rc=$?
    trap - EXIT
    if [ -n "$IPERF_PID" ]; then
        kill "$IPERF_PID" 2>/dev/null || true
        wait "$IPERF_PID" 2>/dev/null || true
    fi
    for pid in "${CAPTURE_PIDS[@]}"; do
        kill -INT "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
    stop_and_check_sanitizer "$CLIENT_PID" client "$WORK_DIR/client.log" || rc=1
    stop_and_check_sanitizer "$SERVER_PID" server "$WORK_DIR/server.log" || rc=1
    if grep -qE "$_SANITIZER_RE" "$WORK_DIR/client.log" "$WORK_DIR/server.log" \
        2>/dev/null; then rc=1; fi
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip netns del "$NS_SERVER" 2>/dev/null || true
    if [ -n "${MQVPN_E2E_LOG_DIR:-}" ]; then
        mkdir -p "$MQVPN_E2E_LOG_DIR"
        cp -a "$WORK_DIR/." "$MQVPN_E2E_LOG_DIR/"
    fi
    rm -rf "$WORK_DIR"
    exit "$rc"
}
trap cleanup EXIT
ip netns add "$NS_CLIENT"
ip netns add "$NS_SERVER"
ip -n "$NS_CLIENT" link set lo up
ip -n "$NS_SERVER" link set lo up
for index in 1 2; do
    [ "$index" = 1 ] || [ "$MIXED" = 1 ] || break
    cdev="psc${index}$$" sdev="pss${index}$$"
    ip link add "$cdev" type veth peer name "$sdev"
    ip link set "$cdev" netns "$NS_CLIENT"
    ip link set "$sdev" netns "$NS_SERVER"
    mtu=1280
    [ "$index" = 1 ] || mtu=1500
    ip -n "$NS_CLIENT" link set "$cdev" mtu "$mtu" up
    ip -n "$NS_SERVER" link set "$sdev" mtu "$mtu" up
    if [ "$IPV6" = 1 ]; then
        ip -n "$NS_CLIENT" -6 addr add "fd00:151:$index::2/64" dev "$cdev" nodad
        ip -n "$NS_SERVER" -6 addr add "fd00:151:$index::1/64" dev "$sdev" nodad
    else
        ip -n "$NS_CLIENT" addr add "10.151.$index.2/24" dev "$cdev"
        ip -n "$NS_SERVER" addr add "10.151.$index.1/24" dev "$sdev"
    fi
    for pair in "$NS_CLIENT $cdev" "$NS_SERVER $sdev"; do
        read -r ns dev <<<"$pair"
        ip netns exec "$ns" ethtool -K "$dev" tso off gso off gro off \
            tx-udp-segmentation off >"$WORK_DIR/$dev.offload.log" 2>&1
        ip netns exec "$ns" tc qdisc add dev "$dev" root netem delay 5ms rate 20mbit
        ip netns exec "$ns" tcpdump -U -n -i "$dev" \
            -w "$WORK_DIR/$dev.fragments.pcap" \
            'ip[6:2] & 0x3fff != 0 or ip6 protochain 44' \
            >"$WORK_DIR/$dev.capture.log" 2>&1 &
        CAPTURE_PIDS+=("$!")
        wait_for_log "$WORK_DIR/$dev.capture.log" 'listening on' 5
    done
done
if [ "$IPV6" = 1 ]; then
    ENDPOINT='[fd00:151:1::1]:4433'
    LISTEN='[::]:4433'
    if [ "$MIXED" = 1 ]; then
        ip -n "$NS_CLIENT" -6 route add fd00:151:1::1/128 dev "psc2$$" metric 200
    fi
else
    ENDPOINT='10.151.1.1:4433' LISTEN='0.0.0.0:4433'
    if [ "$MIXED" = 1 ]; then
        ip -n "$NS_CLIENT" route add 10.151.1.1/32 dev "psc2$$" metric 200
    fi
fi
for ns in "$NS_CLIENT" "$NS_SERVER"; do
    ip netns exec "$ns" nstat -asz IpFragCreates Ip6FragCreates \
        >"$WORK_DIR/$ns-fragments-before.txt"
    ip netns exec "$ns" nft -f - <<'NFT'
table inet pmtu_test {
    chain output {
        type filter hook output priority 0; policy accept;
        icmp type destination-unreachable drop
        icmpv6 type packet-too-big drop
    }
}
NFT
done
printf '[Interface]\nPMTUD = true\n' >"$WORK_DIR/config.ini"
if [ "$REJECT6" = 1 ]; then
    printf 'PathMTU = 1280\n' >>"$WORK_DIR/config.ini"
fi
printf '[Advanced]\nUdpGso = false\nUdpGro = false\nTunOffload = false\nTunGro = false\n' \
    >>"$WORK_DIR/config.ini"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
    -keyout "$WORK_DIR/server.key" -out "$WORK_DIR/server.crt" -days 1 \
    -nodes -subj /CN=pmtu-test >"$WORK_DIR/openssl.log" 2>&1
PSK="$("$MQVPN" --genkey)"
EXTRA=()
[ "$REJECT6" = 0 ] || EXTRA=(--subnet6 fd00:abcd::/112)
ip netns exec "$NS_SERVER" "$MQVPN" --mode server --listen "$LISTEN" \
    --subnet 10.0.0.0/24 --cert "$WORK_DIR/server.crt" \
    --key "$WORK_DIR/server.key" --auth-key "$PSK" \
    --config "$WORK_DIR/config.ini" --log-level info "${EXTRA[@]}" \
    >"$WORK_DIR/server.log" 2>&1 &
SERVER_PID=$!
if [ "$REJECT6" = 1 ]; then
    for _ in $(seq 50); do
        kill -0 "$SERVER_PID" 2>/dev/null || break
        sleep .1
    done
    if kill -0 "$SERVER_PID" 2>/dev/null; then
        echo 'FAIL IPv6 configuration with insufficient inner MTU accepted'
        exit 1
    fi
    rc=0
    wait "$SERVER_PID" || rc=$?
    SERVER_PID=''
    [ "$rc" = 1 ]
    grep -qF 'IPv6 requires an inner MTU of at least 1280' "$WORK_DIR/server.log"
    ! grep -qE "$_SANITIZER_RE" "$WORK_DIR/server.log"
    echo 'PASS IPv6 minimum-MTU rejection'
    exit 0
fi
sleep .5
kill -0 "$SERVER_PID"
PATHS=(--path "psc1$$")
if [ "$MIXED" = 1 ]; then
    if [ "$SMALL_FIRST" = 1 ]; then
        PATHS+=(--path "psc2$$")
    else
        PATHS=(--path "psc2$$" --path "psc1$$")
    fi
fi
ip netns exec "$NS_CLIENT" "$MQVPN" --mode client --server "$ENDPOINT" \
    "${PATHS[@]}" --auth-key "$PSK" --insecure --config "$WORK_DIR/config.ini" \
    --log-level info >"$WORK_DIR/client.log" 2>&1 &
CLIENT_PID=$!
connected=0
for _ in $(seq 30); do
    if ip netns exec "$NS_CLIENT" ping -c 1 -W .2 10.0.0.1 \
        >"$WORK_DIR/startup-ping.log" 2>&1; then connected=1; break; fi
    kill -0 "$CLIENT_PID"
    sleep .1
done
if [ "$connected" != 1 ]; then
    echo 'FAIL small-MTU tunnel did not become usable within the startup budget'
    exit 1
fi
ip netns exec "$NS_CLIENT" ping -c 3 -W 1 10.0.0.1 >"$WORK_DIR/ping.log"
grep -q ' 0% packet loss' "$WORK_DIR/ping.log"
if [ "$MIXED" = 1 ]; then
    for dev in "psc1$$" "psc2$$"; do
        wait_for_log "$WORK_DIR/client.log" \
            "activated:.*iface=${dev}.*state=ACTIVE|path [0-9]+ \\(${dev}\\) -> active" 15
    done
fi

for direction in up down; do
    timeout 20 ip netns exec "$NS_SERVER" iperf3 -s -1 -B 10.0.0.1 -p 5201 -J \
        >"$WORK_DIR/server-$direction.json" 2>"$WORK_DIR/server-$direction.err" &
    IPERF_PID=$!
    for _ in $(seq 30); do
        ip netns exec "$NS_SERVER" ss -ltn 'sport = :5201' | grep -q LISTEN && break
        sleep .1
    done
    REVERSE=()
    [ "$direction" = up ] || REVERSE=(-R)
    timeout 20 ip netns exec "$NS_CLIENT" iperf3 -c 10.0.0.1 -p 5201 \
        -t 4 -J "${REVERSE[@]}" >"$WORK_DIR/client-$direction.json" \
        2>"$WORK_DIR/client-$direction.err"
    wait "$IPERF_PID"
    IPERF_PID=''
    for side in client server; do
        role=tcp
        if [ "$side" = server ] && [ "$direction" = down ]; then
            role=reverse-server
        fi
        check_receiver "$WORK_DIR/$side-$direction.json" "$role"
    done
done
# UDP receiver evidence catches a tunnel that only sustains tiny control
# packets. Multiple flows also exercise both WLB paths in the mixed case.
timeout 20 ip netns exec "$NS_SERVER" iperf3 -s -1 -B 10.0.0.1 -p 5201 -J \
    >"$WORK_DIR/server-udp.json" 2>"$WORK_DIR/server-udp.err" &
IPERF_PID=$!
for _ in $(seq 30); do
    ip netns exec "$NS_SERVER" ss -ltn 'sport = :5201' | grep -q LISTEN && break
    sleep .1
done
timeout 20 ip netns exec "$NS_CLIENT" iperf3 -c 10.0.0.1 -p 5201 \
    -u -P 4 -b 1M -l 512 -t 4 -J >"$WORK_DIR/client-udp.json" \
    2>"$WORK_DIR/client-udp.err"
wait "$IPERF_PID"
IPERF_PID=''
for side in client server; do
    check_receiver "$WORK_DIR/$side-udp.json" udp
done
# Full-sized inner DF packets must work once discovery converges.
sleep 3
mtu="$(ip -n "$NS_CLIENT" link show mqvpn0 | sed -n 's/.* mtu \([0-9]*\) .*/\1/p')"
[ "$mtu" -ge 1100 ] && [ "$mtu" -lt 1280 ]
ip netns exec "$NS_CLIENT" ping -M do -s "$((mtu - 28))" -c 10 -i .1 -W 1 \
    10.0.0.1 >"$WORK_DIR/df-ping.log"
grep -q ' 0% packet loss' "$WORK_DIR/df-ping.log"
kill -0 "$CLIENT_PID"
kill -0 "$SERVER_PID"
for ns in "$NS_CLIENT" "$NS_SERVER"; do
    ip netns exec "$ns" nstat -asz IpFragCreates Ip6FragCreates \
        >"$WORK_DIR/$ns-fragments-after.txt"
done
# Namespace counters include inner packets on the TUN. The physical-device
# captures distinguish forbidden outer fragmentation from inner IP behavior.
for pid in "${CAPTURE_PIDS[@]}"; do
    kill -INT "$pid"
    wait "$pid"
done
CAPTURE_PIDS=()
for capture in "$WORK_DIR/"*.fragments.pcap; do
    [ "$(stat -c %s "$capture")" = 24 ]
    grep -q '^0 packets dropped by kernel$' "${capture%.fragments.pcap}.capture.log"
done
echo "PASS small-MTU startup and bidirectional transfer (TUN MTU $mtu)"
