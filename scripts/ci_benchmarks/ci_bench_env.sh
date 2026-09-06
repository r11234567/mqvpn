#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# ci_bench_env.sh — Shared CI benchmark environment setup
#
# Source this file from CI benchmark scripts:
#   source "$(dirname "$0")/ci_bench_env.sh"
#
# CI-specific additions over manual benchmarks:
#   - Commit SHA in all JSON output
#   - Stale process/netns cleanup at start
#   - iperf3 killed inside netns only (safe for shared runners)
#   - Sanity check helper
#   - python3/tc dependency checks

CI_BENCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MQVPN="${MQVPN:-${CI_BENCH_DIR}/../../build/mqvpn}"
CI_BENCH_RESULTS="${CI_BENCH_RESULTS:-${CI_BENCH_DIR}/../../ci_bench_results}"

# Git commit SHA for JSON output
CI_BENCH_COMMIT="${CI_BENCH_COMMIT:-$(git -C "$CI_BENCH_DIR" rev-parse HEAD 2>/dev/null || echo unknown)}"

# Namespace and veth names
NS_SERVER="ci-bench-server"
NS_CLIENT="ci-bench-client"
VETH_A0="ci-a0"
VETH_A1="ci-a1"
VETH_B0="ci-b0"
VETH_B1="ci-b1"

# IP addressing
IP_A_CLIENT="10.100.0.2/24"
IP_A_SERVER="10.100.0.1/24"
IP_B_CLIENT="10.200.0.2/24"
IP_B_SERVER="10.200.0.1/24"
IP_A_SERVER_ADDR="10.100.0.1"
TUNNEL_SERVER_IP="10.0.0.1"
VPN_LISTEN_PORT="4433"
IPERF3_PORT="5201"
CI_BENCH_SCHEDULER="${CI_BENCH_SCHEDULER:-wlb}"
CI_BENCH_LOG_LEVEL="${CI_BENCH_LOG_LEVEL:-error}"

# WLB scheduler instrumentation. With this set to 1 both ends run at
# --log-level info and their output is captured, so the once-a-second
# |wlb_instr| line xquic emits (pins, packets and round count per path) can be
# read back after a measurement. Off by default: it raises the log level for
# the whole run.
#
# The counters are aggregates maintained in the scheduler, not per-packet log
# lines, so the capture itself does not move the throughput being measured --
# which matters, because the number under investigation IS the throughput.
#
# The SERVER log is the one that answers the question. Every measurement here
# is iperf3 in the DL direction, so the bulk data is scheduled by the server's
# WLB instance; the client's only schedules the returning ACKs. The first
# attempt read the client log and would have reported the ACK split as though
# it were the throughput split.
CI_BENCH_WLB_INSTR="${CI_BENCH_WLB_INSTR:-0}"
# Set by the start helpers when the above is on. The server log is NOT
# truncated per measurement -- one server serves solo-A, solo-B and multipath
# in turn -- so readers must take a windowed delta; ci_bench_mark_server_log
# records where the window starts.
CI_BENCH_SERVER_LOG=""
CI_BENCH_CLIENT_LOG=""
CI_BENCH_SERVER_LOG_MARK=0

# Extra config file handed to BOTH ends via --config. Used to vary one setting
# across arms of an A/B inside a single run (see CI_BENCH_REORDER in
# ci_bench_scenarios.sh) without a second workflow dispatch.
CI_BENCH_CONFIG_FILE="${CI_BENCH_CONFIG_FILE:-}"

# Byte offset of the server log at the start of the current measurement, so a
# reader can skip the earlier measurements' counters. No-op when the log is not
# being captured.
ci_bench_mark_server_log() {
    if [ -n "$CI_BENCH_SERVER_LOG" ] && [ -r "$CI_BENCH_SERVER_LOG" ]; then
        CI_BENCH_SERVER_LOG_MARK="$(wc -c <"$CI_BENCH_SERVER_LOG" 2>/dev/null || echo 0)"
    else
        CI_BENCH_SERVER_LOG_MARK=0
    fi
}

# Process PIDs
#
# _CB_CLIENT_PID alone is not enough. ci_bench_start_client is sometimes
# reached from inside a command substitution, and that subshell's copy of the
# variable dies with the subshell -- so the next call's "kill the previous
# client" guard sees nothing and starts a second client beside the first. Every
# pid also goes to a file, whose path is derived from $$ (the ORIGINAL shell's
# pid, unchanged inside a subshell) so every caller agrees on it.
_CB_CLIENT_PIDS="${TMPDIR:-/tmp}/mqvpn-cibench-clients.$$"
_CB_SERVER_PID=""
_CB_CLIENT_PID=""
_CB_WORK_DIR=""
_CB_PSK=""

# ── Dependency checks ──

ci_bench_check_deps() {
    if [ ! -f "$MQVPN" ]; then
        echo "error: mqvpn binary not found at $MQVPN"
        exit 1
    fi
    MQVPN="$(realpath "$MQVPN")"

    for cmd in iperf3 openssl python3 tc; do
        if ! command -v "$cmd" &>/dev/null; then
            echo "error: $cmd not found"
            exit 1
        fi
    done

    mkdir -p "$CI_BENCH_RESULTS"
}

# ── Stale state cleanup (run before setup) ──

ci_bench_cleanup_stale() {
    pkill -f "mqvpn.*ci-bench" 2>/dev/null || true
    ip netns exec "$NS_SERVER" pkill -f "iperf3" 2>/dev/null || true
    ip netns exec "$NS_CLIENT" pkill -f "iperf3" 2>/dev/null || true
    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true
}

# ── Network namespace setup ──

ci_bench_setup_netns() {
    echo "Setting up network namespaces..."

    ci_bench_cleanup_stale

    ip netns add "$NS_SERVER"
    ip netns add "$NS_CLIENT"

    # Path A: 10.100.0.0/24
    ip link add "$VETH_A0" type veth peer name "$VETH_A1"
    ip link set "$VETH_A0" netns "$NS_CLIENT"
    ip link set "$VETH_A1" netns "$NS_SERVER"
    ip netns exec "$NS_CLIENT" ip addr add "$IP_A_CLIENT" dev "$VETH_A0"
    ip netns exec "$NS_SERVER" ip addr add "$IP_A_SERVER" dev "$VETH_A1"
    ip netns exec "$NS_CLIENT" ip link set "$VETH_A0" up
    ip netns exec "$NS_SERVER" ip link set "$VETH_A1" up

    # Path B: 10.200.0.0/24
    ip link add "$VETH_B0" type veth peer name "$VETH_B1"
    ip link set "$VETH_B0" netns "$NS_CLIENT"
    ip link set "$VETH_B1" netns "$NS_SERVER"
    ip netns exec "$NS_CLIENT" ip addr add "$IP_B_CLIENT" dev "$VETH_B0"
    ip netns exec "$NS_SERVER" ip addr add "$IP_B_SERVER" dev "$VETH_B1"
    ip netns exec "$NS_CLIENT" ip link set "$VETH_B0" up
    ip netns exec "$NS_SERVER" ip link set "$VETH_B1" up

    # Loopback
    ip netns exec "$NS_CLIENT" ip link set lo up
    ip netns exec "$NS_SERVER" ip link set lo up

    # IP forwarding + relax rp_filter so Path B can deliver packets destined
    # to IP_A_SERVER_ADDR (which is owned by veth-a1) when they arrive on
    # veth-b1. Without rp_filter=0, strict reverse-path checking drops them.
    # Linux uses MAX(all, <iface>), so all/default alone don't lower the
    # value of already-existing interfaces (default only applies to NEW
    # interfaces). Set per-interface rp_filter=0 explicitly on every veth
    # in both namespaces.
    ip netns exec "$NS_SERVER" sysctl -w net.ipv4.ip_forward=1 >/dev/null
    for iface in all default lo "$VETH_A1" "$VETH_B1"; do
        ip netns exec "$NS_SERVER" \
            sysctl -w "net.ipv4.conf.${iface}.rp_filter=0" >/dev/null
    done
    for iface in all default lo "$VETH_A0" "$VETH_B0"; do
        ip netns exec "$NS_CLIENT" \
            sysctl -w "net.ipv4.conf.${iface}.rp_filter=0" >/dev/null
    done

    # Server address as /32 on lo, so the server accepts traffic for
    # IP_A_SERVER_ADDR no matter which veth it arrives on (Path A or Path B).
    # This is required for any scheduler that drives traffic out Path B
    # toward the same server address (WLB cross-path packets, backup_fec
    # repair symbols on STANDBY, failover after Path A loss).
    ip netns exec "$NS_SERVER" ip addr add "${IP_A_SERVER_ADDR}/32" dev lo

    # Backup route on the client so Path B can carry traffic to the server
    # address even when Path A is down. metric 200 keeps it as a fallback
    # under normal conditions.
    ip netns exec "$NS_CLIENT" ip route add 10.100.0.0/24 via 10.200.0.1 \
        dev "$VETH_B0" metric 200

    # Verify
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$IP_A_SERVER_ADDR" >/dev/null
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 10.200.0.1 >/dev/null

    echo "OK: netns created"
}

# ── tc netem ──

ci_bench_apply_netem() {
    local netem_a="${1:-delay 10ms rate 300mbit}"
    local netem_b="${2:-delay 30ms rate 80mbit}"

    # Clear existing
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_A0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_A1" root 2>/dev/null || true
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_B0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_B1" root 2>/dev/null || true

    # Apply on both ends (RTT = 2 × delay)
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_A0" root netem ${netem_a}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_A1" root netem ${netem_a}
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_B0" root netem ${netem_b}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_B1" root netem ${netem_b}

    echo "OK: netem applied (A: ${netem_a}, B: ${netem_b})"
}

# ── VPN server ──

ci_bench_start_server() {
    local scheduler="${1:-$CI_BENCH_SCHEDULER}"
    # Optional extra CLI flags (e.g. "--control-port 9091"), mirroring
    # bench_start_vpn_server in benchmarks/bench_env_setup.sh. Existing callers
    # pass nothing and are unaffected.
    local extra="${2:-}"
    _CB_WORK_DIR="$(mktemp -d)"

    _CB_PSK=$("$MQVPN" --genkey 2>/dev/null)

    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
        -keyout "${_CB_WORK_DIR}/server.key" -out "${_CB_WORK_DIR}/server.crt" \
        -days 365 -nodes -subj "/CN=ci-bench" 2>/dev/null

    # Tier prefix, empty for every caller that does not set CI_BENCH_TIER.
    # Unquoted on purpose: it expands to several systemd-run arguments.
    local tier_prefix=""
    if [ -n "${CI_BENCH_TIER:-}" ]; then
        tier_prefix="$(ci_bench_tier_prefix "$CI_BENCH_TIER")" || return 1
    fi

    # Arm config (A/B), applied to both ends. Unquoted: expands to two words
    # or to nothing.
    local cfg_arg=""
    if [ -n "$CI_BENCH_CONFIG_FILE" ]; then
        cfg_arg="--config $CI_BENCH_CONFIG_FILE"
    fi

    local level="$CI_BENCH_LOG_LEVEL"
    CI_BENCH_SERVER_LOG=""
    CI_BENCH_SERVER_LOG_MARK=0
    if [ "$CI_BENCH_WLB_INSTR" = "1" ]; then
        level=info
        CI_BENCH_SERVER_LOG="${_CB_WORK_DIR}/server-instr.log"
        : >"$CI_BENCH_SERVER_LOG"
    fi

    if [ -n "$CI_BENCH_SERVER_LOG" ]; then
        ${tier_prefix} ip netns exec "$NS_SERVER" "$MQVPN" \
            --mode server \
            --listen "0.0.0.0:${VPN_LISTEN_PORT}" \
            --subnet 10.0.0.0/24 \
            --cert "${_CB_WORK_DIR}/server.crt" \
            --key "${_CB_WORK_DIR}/server.key" \
            --auth-key "$_CB_PSK" \
            --scheduler "$scheduler" \
            ${cfg_arg} \
            ${extra} \
            --log-level "$level" >>"$CI_BENCH_SERVER_LOG" 2>&1 &
    else
        ${tier_prefix} ip netns exec "$NS_SERVER" "$MQVPN" \
            --mode server \
            --listen "0.0.0.0:${VPN_LISTEN_PORT}" \
            --subnet 10.0.0.0/24 \
            --cert "${_CB_WORK_DIR}/server.crt" \
            --key "${_CB_WORK_DIR}/server.key" \
            --auth-key "$_CB_PSK" \
            --scheduler "$scheduler" \
            ${cfg_arg} \
            ${extra} \
            --log-level "$level" &
    fi
    _CB_SERVER_PID=$!
    sleep 2

    if ! kill -0 "$_CB_SERVER_PID" 2>/dev/null; then
        echo "ERROR: VPN server died"
        return 1
    fi
    echo "VPN server running (PID $_CB_SERVER_PID, scheduler=$scheduler)"
}

# ── VPN client ──

ci_bench_start_client() {
    local paths="$1"
    local scheduler="${2:-$CI_BENCH_SCHEDULER}"

    # Kill every previous client, including one a subshell lost track of.
    ci_bench_stop_client

    # Arm config (A/B): the same file the server got, so both ends agree.
    # Unquoted: expands to two words or to nothing.
    local cfg_arg=""
    if [ -n "$CI_BENCH_CONFIG_FILE" ]; then
        cfg_arg="--config $CI_BENCH_CONFIG_FILE"
    fi

    local level="$CI_BENCH_LOG_LEVEL"
    if [ "$CI_BENCH_WLB_INSTR" = "1" ] && [ -n "$_CB_WORK_DIR" ]; then
        level=info
        CI_BENCH_CLIENT_LOG="${_CB_WORK_DIR}/client-instr.log"
        : > "$CI_BENCH_CLIENT_LOG"
    fi

    if [ -n "$CI_BENCH_CLIENT_LOG" ]; then
        ip netns exec "$NS_CLIENT" "$MQVPN" \
            --mode client \
            --server "${IP_A_SERVER_ADDR}:${VPN_LISTEN_PORT}" \
            ${paths} \
            --auth-key "$_CB_PSK" \
            --scheduler "$scheduler" \
            --insecure \
            ${cfg_arg} \
            --log-level "$level" >>"$CI_BENCH_CLIENT_LOG" 2>&1 &
    else
        ip netns exec "$NS_CLIENT" "$MQVPN" \
            --mode client \
            --server "${IP_A_SERVER_ADDR}:${VPN_LISTEN_PORT}" \
            ${paths} \
            --auth-key "$_CB_PSK" \
            --scheduler "$scheduler" \
            --insecure \
            ${cfg_arg} \
            --log-level "$level" &
    fi
    _CB_CLIENT_PID=$!
    echo "$_CB_CLIENT_PID" >> "$_CB_CLIENT_PIDS" 2>/dev/null || true
    sleep 3

    if ! kill -0 "$_CB_CLIENT_PID" 2>/dev/null; then
        echo "ERROR: VPN client died"
        return 1
    fi
    echo "VPN client running (PID $_CB_CLIENT_PID)"
}

# ── Tunnel wait ──

ci_bench_wait_tunnel() {
    local timeout="${1:-15}"
    local elapsed=0

    while [ "$elapsed" -lt "$timeout" ]; do
        if ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$TUNNEL_SERVER_IP" >/dev/null 2>&1; then
            echo "OK: tunnel up (${elapsed}s)"
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done

    echo "ERROR: tunnel not reachable after ${timeout}s"
    return 1
}

# ── Stop VPN ──

# True only while `pid` is still the mqvpn we recorded. A pid in the file may
# have exited already and had its number handed to something unrelated, so
# `kill -0` on its own is not enough to justify killing it. Prefer /proc/exe
# (exact, and readable because these scripts run as root); comm is the fallback
# and is compared on its first 15 bytes because that is all the kernel keeps.
_cb_pid_is_client() {
    local exe comm want
    exe="$(readlink "/proc/$1/exe" 2>/dev/null)" || exe=""
    if [ -n "$exe" ]; then
        [ "${exe% (deleted)}" = "$MQVPN" ]
        return
    fi
    comm="$(cat "/proc/$1/comm" 2>/dev/null)" || return 1
    want="$(basename "$MQVPN")"
    [ "$comm" = "${want:0:15}" ]
}

ci_bench_stop_client() {
    local pid live=0
    # `wait` only works on our own children, and a pid from the file may be a
    # subshell's child -- hence the `|| true` and the settle sleep below.
    if [ -f "$_CB_CLIENT_PIDS" ]; then
        while read -r pid; do
            [ -n "$pid" ] || continue
            kill -0 "$pid" 2>/dev/null || continue
            _cb_pid_is_client "$pid" || continue
            live=$((live + 1))
            kill "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
        done < "$_CB_CLIENT_PIDS"
        : > "$_CB_CLIENT_PIDS"
    fi
    if [ -n "$_CB_CLIENT_PID" ] && kill -0 "$_CB_CLIENT_PID" 2>/dev/null \
       && _cb_pid_is_client "$_CB_CLIENT_PID"; then
        kill "$_CB_CLIENT_PID" 2>/dev/null || true
        wait "$_CB_CLIENT_PID" 2>/dev/null || true
        live=$((live + 1))
    fi
    _CB_CLIENT_PID=""

    # Two live clients at once is never intended and is not cosmetic: the first
    # one still owns the tunnel address and the TUN, so a measurement aimed at
    # another path set silently travels over the first client's paths instead.
    # Report it rather than tidying it away.
    if [ "$live" -gt 1 ]; then
        echo "WARNING: reaped ${live} concurrent VPN clients -- an earlier" \
             "measurement leaked one, and its paths carried the traffic"
    fi
    [ "$live" -eq 0 ] || sleep 1
}

ci_bench_stop_vpn() {
    ci_bench_stop_client
    if [ -n "$_CB_SERVER_PID" ]; then
        kill "$_CB_SERVER_PID" 2>/dev/null || true
        wait "$_CB_SERVER_PID" 2>/dev/null || true
        _CB_SERVER_PID=""
        sleep 1
    fi
    # A tiered server runs inside a transient scope, and the pid above is
    # systemd-run's rather than the server's, so the kill can return with the
    # server still holding its listen port.
    if [ -n "${CI_BENCH_TIER:-}" ] && declare -F ci_bench_tier_cleanup >/dev/null; then
        ci_bench_tier_cleanup
    fi
}

# ── iperf3 helpers ──

# Run iperf3 and return JSON file path.
# Usage: ci_bench_run_iperf TCP DL 10 4
#        ci_bench_run_iperf UDP UL 10 4 500M
#
# Optional environment knobs, all unset by default so existing callers are
# byte-for-byte unaffected:
#   CI_BENCH_IPERF_LEN       payload size (-l), for small-packet profiles
#   CI_BENCH_IPERF_INTERVAL  per-second samples (-i), for shape analysis
#   CI_BENCH_IPERF_TARGET    bind/connect address; defaults to the tunnel IP.
#                            Set to IP_A_SERVER_ADDR to measure the BARE path
#                            for a baseline -- the difference between the two is
#                            what the tunnel costs, and being a within-run
#                            difference it survives a noise floor that makes
#                            absolute Mbps unreadable across runs.
ci_bench_run_iperf() {
    local proto="$1"    # TCP or UDP
    local dir="$2"      # DL or UL
    local duration="$3"
    local parallel="$4"
    local target_bw="${5:-}"

    # Server bind and client connect must be the same address, or the client
    # reaches a listener that is not there.
    local _cb_iperf_target="${CI_BENCH_IPERF_TARGET:-$TUNNEL_SERVER_IP}"

    local json_file
    json_file="$(mktemp)"

    # iperf3 server always in NS_SERVER (bound to the target address).
    # iperf3 client always in NS_CLIENT.
    # Direction controlled by -R flag:
    #   DL (server→client): -R (reverse)
    #   UL (client→server): no flag (default iperf3 direction)
    # Wait for the port to come free, then for the new server to own it. A
    # scenario issues many samples back to back on this one port, and the
    # previous sample's one-shot server can still be releasing its listener:
    # bind then loses the race and dies into &>/dev/null, the client reaches
    # the closing socket instead, and the sample comes back as a mid-transfer
    # "Broken pipe" that looks like the emulated path failed.
    local i
    for (( i=0; i<20; i++ )); do
        ip netns exec "$NS_SERVER" ss -ltn 2>/dev/null \
            | grep -q ":${IPERF3_PORT} " || break
        sleep 0.5
    done

    ip netns exec "$NS_SERVER" iperf3 -s -B "$_cb_iperf_target" -1 &>/dev/null &
    local iperf_srv_pid=$!

    for (( i=0; i<20; i++ )); do
        ip netns exec "$NS_SERVER" ss -ltn 2>/dev/null \
            | grep -q ":${IPERF3_PORT} " && break
        sleep 0.5
    done

    local args="-c $_cb_iperf_target -t $duration -P $parallel --json"
    [ "$proto" = "UDP" ] && args="$args -u"
    [ -n "$target_bw" ] && args="$args -b $target_bw"
    [ "$dir" = "DL" ] && args="$args -R"
    # Payload size, for the small-packet profiles. Left unset by every bulk
    # caller so iperf3's own default applies and their numbers do not move.
    [ -n "${CI_BENCH_IPERF_LEN:-}" ] && args="$args -l ${CI_BENCH_IPERF_LEN}"
    # Per-second interval samples, so a caller that needs the shape of the
    # transfer can read intervals[] the way ci_bench_failover.sh:180 does.
    [ -n "${CI_BENCH_IPERF_INTERVAL:-}" ] && args="$args -i ${CI_BENCH_IPERF_INTERVAL}"

    # Two hang guards, both mandatory once the emulated path is allowed to be
    # genuinely broken (see tests/test_e2e_hybrid_h2.sh, which documents the
    # same pair):
    #
    #  - `timeout` on the client: over a lossy, high-RTT tunnel iperf3 can sit
    #    on its control connection well past the test duration, and with no
    #    bound the caller waits forever.
    #  - kill the server BEFORE waiting on it: `iperf3 -s -1` blocks until its
    #    first connection, so if the client never got through, a bare
    #    `wait` never returns.
    #
    # Without these, one unreachable path hangs the whole run until the job
    # timeout — which is exactly how the weekly netsim `classes` job burned 60
    # minutes while its siblings finished in six.
    ip netns exec "$NS_CLIENT" timeout $((duration + 20)) \
        iperf3 $args > "$json_file" 2>&1 || true

    kill "$iperf_srv_pid" 2>/dev/null || true
    wait "$iperf_srv_pid" 2>/dev/null || true

    echo "$json_file"
}

# Extract UDP delivery quality from iperf3 JSON.
#
# Echoes: "<lost_pct> <jitter_ms> <out_of_order> <packets> <mbps>", or
# "NA NA NA NA NA" when the document carries no UDP summary -- the sentinel,
# never zeros, because a failed run and a clean one must not read alike.
#
# Why iperf3 rather than the tunnel's own counters: iperf3 stamps sequence
# numbers into the UDP payload, so out_of_order and lost_packets are measured
# END TO END and independently of whether mqvpn's reorder engine is enabled.
# The engine is OFF by default (config.c:707), so a reorder figure taken from
# get_reorder_stats would read zero because the engine never ran -- which is
# indistinguishable from a genuinely in-order stream. That is the same trap as
# the send-supply headroom column, which came back 0.000 on all 46 rows of run
# 34026833126 because it restated the predicate that produced it.
#
# The server's summary is the authoritative one for loss and reorder: only the
# receiver can know what failed to arrive.
ci_bench_parse_udp_quality() {
    local json_file="$1"
    python3 -c "
import json
try:
    with open('${json_file}') as f:
        data = json.load(f)
except Exception:
    print('NA NA NA NA NA')
    raise SystemExit(0)

end = data.get('end') or {}
# -R (DL) puts the receiving side in sum_received / the server's summary; a UDP
# run reports its delivery stats under 'sum' on the sender and
# 'sum_received' on the receiver depending on direction and version, so try
# each in the order that prefers a receiver's view.
cand = []
for key in ('sum_received', 'sum', 'sum_sent'):
    v = end.get(key)
    if isinstance(v, dict):
        cand.append(v)
srv = end.get('sum_sent_receiver') or {}
if isinstance(srv, dict) and srv:
    cand.insert(0, srv)

pkts = lost_pct = jitter = ooo = mbps = None
for c in cand:
    if pkts is None and isinstance(c.get('packets'), (int, float)):
        pkts = c['packets']
    if lost_pct is None and isinstance(c.get('lost_percent'), (int, float)):
        lost_pct = c['lost_percent']
    if jitter is None and isinstance(c.get('jitter_ms'), (int, float)):
        jitter = c['jitter_ms']
    if ooo is None and isinstance(c.get('out_of_order'), (int, float)):
        ooo = c['out_of_order']
    if mbps is None and isinstance(c.get('bits_per_second'), (int, float)):
        mbps = c['bits_per_second'] / 1e6

def f(v, spec='%.3f'):
    return 'NA' if v is None else (spec % v)

print('%s %s %s %s %s' % (f(lost_pct), f(jitter),
                          f(ooo, '%d') if ooo is not None else 'NA',
                          f(pkts, '%d') if pkts is not None else 'NA',
                          f(mbps, '%.3f')))
"
}

# Extract per-second latency-proxy percentiles from an iperf3 UDP document.
#
# iperf3 reports jitter, not a latency distribution, so a true per-packet p99
# is not available from it. What IS available per interval is jitter and loss,
# and the p99 of the interval jitter series is the closest honest proxy. The
# field is named for what it is (jitter p99, not latency p99) so no reader
# mistakes it for an RTT percentile.
ci_bench_parse_udp_jitter_p99() {
    local json_file="$1"
    python3 -c "
import json
try:
    with open('${json_file}') as f:
        data = json.load(f)
except Exception:
    print('NA NA')
    raise SystemExit(0)

vals = []
for iv in data.get('intervals') or []:
    s = iv.get('sum') or {}
    j = s.get('jitter_ms')
    if isinstance(j, (int, float)):
        vals.append(j)
if not vals:
    print('NA NA')
else:
    vals.sort()
    p99 = vals[min(len(vals) - 1, int(0.99 * (len(vals) - 1)))]
    print('%.3f %.3f' % (p99, max(vals)))
"
}

# Extract throughput (Mbps) from iperf3 JSON
ci_bench_parse_throughput() {
    local json_file="$1"
    python3 -c "
import json
try:
    with open('${json_file}') as f:
        data = json.load(f)
    end = data.get('end', {})
    if 'sum_received' in end:
        print(f\"{end['sum_received']['bits_per_second'] / 1e6:.1f}\")
    elif 'sum' in end:
        print(f\"{end['sum']['bits_per_second'] / 1e6:.1f}\")
    else:
        print('0.0')
except Exception:
    print('0.0')
"
}

# ── Sanity check ──

ci_bench_sanity_check() {
    local json_file="$1"
    local desc="${2:-benchmark}"

    local has_nonzero
    has_nonzero=$(python3 -c "
import json
with open('${json_file}') as f:
    data = json.load(f)

def check(obj):
    if isinstance(obj, dict):
        for v in obj.values():
            if check(v): return True
    elif isinstance(obj, list):
        for v in obj:
            if check(v): return True
    elif isinstance(obj, (int, float)):
        if obj > 0: return True
    return False

results = data.get('results', data)
print('1' if check(results) else '0')
")

    if [ "$has_nonzero" = "0" ]; then
        echo "ERROR: $desc — all results are zero (iperf3 likely failed)"
        exit 1
    fi
}

# ── Cleanup ──

ci_bench_cleanup() {
    echo ""
    echo "Cleaning up..."

    [ -n "$_CB_CLIENT_PID" ] && kill "$_CB_CLIENT_PID" 2>/dev/null || true
    [ -n "$_CB_SERVER_PID" ] && kill "$_CB_SERVER_PID" 2>/dev/null || true
    _CB_CLIENT_PID=""
    _CB_SERVER_PID=""

    ip netns exec "$NS_SERVER" pkill -f "iperf3" 2>/dev/null || true
    ip netns exec "$NS_CLIENT" pkill -f "iperf3" 2>/dev/null || true
    sleep 1

    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_A0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_A1" root 2>/dev/null || true
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_B0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_B1" root 2>/dev/null || true

    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true

    [ -n "$_CB_WORK_DIR" ] && rm -rf "$_CB_WORK_DIR" || true
}
