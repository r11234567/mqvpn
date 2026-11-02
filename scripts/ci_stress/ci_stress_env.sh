#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# ci_stress_env.sh — Shared CI stress test environment setup
#
# Source this file from CI stress test scripts:
#   source "$(dirname "$0")/ci_stress_env.sh"
#
# Provides:
#   - Namespace/veth setup (same topology as ci_bench but different names)
#   - VPN server/client lifecycle
#   - RSS/fd monitoring (background loop)
#   - Resource leak detection
#   - Result helpers

set -euo pipefail

CI_STRESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MQVPN="${MQVPN:-${CI_STRESS_DIR}/../../build/mqvpn}"
CI_STRESS_RESULTS="${CI_STRESS_RESULTS:-${CI_STRESS_DIR}/../../ci_stress_results}"

# Git commit SHA for JSON output
CI_STRESS_COMMIT="${CI_STRESS_COMMIT:-$(git -C "$CI_STRESS_DIR" rev-parse HEAD 2>/dev/null || echo unknown)}"

# Namespace and veth names (different from ci-bench to avoid conflicts)
CI_STRESS_NAMESPACE_PREFIX="${CI_STRESS_NAMESPACE_PREFIX:-ci-stress}"
NS_SERVER="${CI_STRESS_NAMESPACE_PREFIX}-server"
NS_CLIENT="${CI_STRESS_NAMESPACE_PREFIX}-client"
VETH_A0="ci-s-a0"
VETH_A1="ci-s-a1"
VETH_B0="ci-s-b0"
VETH_B1="ci-s-b1"

# IP addressing
IP_A_CLIENT="10.100.0.2/24"
IP_A_SERVER="10.100.0.1/24"
IP_B_CLIENT="10.200.0.2/24"
IP_B_SERVER="10.200.0.1/24"
IP_A_SERVER_ADDR="10.100.0.1"
# The client always dials IP_A_SERVER_ADDR, which sits on Path A's subnet.
# Path B therefore needs an explicit route to reach it — see
# ci_stress_add_path_b_route(). Same name as ci_bench_env.sh uses.
IP_A_SUBNET="10.100.0.0/24"
IP_B_SERVER_ADDR="10.200.0.1"
TUNNEL_SERVER_IP="10.0.0.1"
VPN_LISTEN_PORT="4433"
CI_STRESS_LOG_LEVEL="${CI_STRESS_LOG_LEVEL:-warn}"
CI_STRESS_RAW_DIR="${CI_STRESS_RAW_DIR:-${CI_STRESS_RESULTS}/raw/$(date -u +%Y%m%d_%H%M%S)-$$}"
CI_STRESS_MONITOR_INTERVAL="${CI_STRESS_MONITOR_INTERVAL:-10}"

# Optional log capture. When a caller sets these before ci_stress_start_server
# / ci_stress_start_client, that VPN process's stdout+stderr goes to the named
# file instead of being inherited. Only scripts that assert on log content opt
# in (ci_stress_failover.sh); the others keep today's inline console output.
CI_STRESS_SERVER_LOG="${CI_STRESS_SERVER_LOG:-${CI_STRESS_RAW_DIR}/vpn-server.log}"
CI_STRESS_CLIENT_LOG="${CI_STRESS_CLIENT_LOG:-${CI_STRESS_RAW_DIR}/vpn-client.log}"

# Default netem
NETEM_A="${NETEM_A:-delay 10ms rate 300mbit}"
NETEM_B="${NETEM_B:-delay 30ms rate 80mbit}"

# Process PIDs
_CS_SERVER_PID=""
_CS_CLIENT_PID=""
_CS_WORK_DIR=""
_CS_PSK=""
_CS_MONITOR_PIDS=()

# ── Dependency checks ──

ci_stress_check_deps() {
    [[ "$CI_STRESS_NAMESPACE_PREFIX" =~ ^[a-zA-Z0-9_-]+$ ]] || { echo 'invalid namespace prefix' >&2; exit 2; }
    [[ "$CI_STRESS_MONITOR_INTERVAL" =~ ^[1-9][0-9]*$ ]] || { echo 'invalid monitor interval' >&2; exit 2; }
    [[ "${CI_STRESS_STOP_GRACE:-10}" =~ ^[1-9][0-9]*$ ]] || { echo 'invalid shutdown grace' >&2; exit 2; }
    if [ ! -f "$MQVPN" ]; then
        echo "error: mqvpn binary not found at $MQVPN"
        exit 1
    fi
    MQVPN="$(realpath "$MQVPN")"

    for cmd in iperf3 openssl python3 tc timeout; do
        if ! command -v "$cmd" &>/dev/null; then
            echo "error: $cmd not found"
            exit 1
        fi
    done

    mkdir -p "$CI_STRESS_RESULTS" "$CI_STRESS_RAW_DIR"
}

# ── Stale state cleanup ──

# A network namespace does not isolate /proc or pkill. Enumerate its actual
# members, optionally matching comm, so another harness cannot be killed.
ci_stress_kill_ns_processes() {
    local ns="$1" comm="${2:-}" pid actual
    for pid in $(ip netns pids "$ns" 2>/dev/null); do
        actual="$(cat "/proc/$pid/comm" 2>/dev/null)" || continue
        [ -z "$comm" ] || [ "$actual" = "$comm" ] || continue
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
}

ci_stress_cleanup_stale() {
    ci_stress_kill_ns_processes "$NS_SERVER"
    ci_stress_kill_ns_processes "$NS_CLIENT"
    ci_stress_kill_ns_processes "$NS_SERVER" iperf3
    ci_stress_kill_ns_processes "$NS_CLIENT" iperf3
    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true
}

# ── Path B route to the server ──

# The server (IP_A_SERVER_ADDR) lives on Path A's subnet, so Path B reaches it
# only through this via-route. Without it a SO_BINDTODEVICE socket on B still
# sends (the kernel assumes the destination is on-link and the veth peer
# answers the ARP for its Path A address, default arp_ignore=0), so Path B
# comes up and validates and the missing route stays invisible.
#
# It stops being invisible the moment Path B has to be REBUILT. The re-add
# gate (iface_has_route_to_server, src/platform/linux/route_check.c) queries
# RTM_F_FIB_MATCH precisely because the on-link fallback makes a plain lookup
# useless, so it sees EHOSTUNREACH and correctly refuses to rebuild a path
# with no route — logging "has a usable address but no route to the server".
# That is what left ci_stress_failover.sh single-pathed for the rest of the
# run after its first Path B fault, and every later Path A fault then tore
# down the whole connection ("abandon the only active path").
#
# Same route as the ci_e2e dual-path suites (run_route_gate_test.sh). metric
# 200 keeps Path A's connected route (metric 0) preferred for unbound
# traffic. Any script that faults Path B must call this again on recovery.
#
# `replace`, not `add`: whether the route survived the fault depends on how
# the path was broken. An admin down flushes every route through the link,
# but a carrier loss (peer down) keeps them and only flags them linkdown --
# so `add` would succeed in one case and fail with EEXIST in the other.
# `replace` states the intent ("this route must exist now") for both, which
# is what lets callers drop the `|| true` that would otherwise hide a real
# failure such as a missing namespace.
ci_stress_add_path_b_route() {
    ip netns exec "$NS_CLIENT" ip route replace "$IP_A_SUBNET" via "$IP_B_SERVER_ADDR" \
        dev "$VETH_B0" metric 200
}

# ── Network namespace setup ──

ci_stress_setup_netns() {
    echo "Setting up network namespaces..."

    ci_stress_cleanup_stale

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

    # IP forwarding
    ip netns exec "$NS_SERVER" sysctl -w net.ipv4.ip_forward=1 >/dev/null

    ci_stress_add_path_b_route

    # Verify
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$IP_A_SERVER_ADDR" >/dev/null
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$IP_B_SERVER_ADDR" >/dev/null

    # Verify Path B the way the re-add gate does, not with a ping. A
    # device-bound ping through VETH_B0 succeeds even with no route: the
    # oif-scoped lookup falls back to "assume on link" and the server answers
    # the ARP for its Path A address (default arp_ignore=0), so connectivity
    # tells us nothing about the FIB. `fibmatch` is the same query
    # iface_has_route_to_server() issues (RTM_F_FIB_MATCH) and returns
    # EHOSTUNREACH when only the fallback exists — so this, and only this,
    # catches the topology silently losing Path B recovery again.
    if ! ip netns exec "$NS_CLIENT" ip route get "$IP_A_SERVER_ADDR" \
            oif "$VETH_B0" fibmatch >/dev/null 2>&1; then
        echo "ERROR: no FIB route to $IP_A_SERVER_ADDR via $VETH_B0 —" \
             "mqvpn will refuse to re-add Path B after a fault"
        return 1
    fi

    echo "OK: netns created"
}

# ── tc netem ──

ci_stress_apply_netem() {
    local netem_a="${1:-$NETEM_A}"
    local netem_b="${2:-$NETEM_B}"

    # Clear existing
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_A0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_A1" root 2>/dev/null || true
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_B0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_B1" root 2>/dev/null || true

    # Apply on both ends (RTT = 2 x delay)
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_A0" root netem ${netem_a}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_A1" root netem ${netem_a}
    ip netns exec "$NS_CLIENT" tc qdisc add dev "$VETH_B0" root netem ${netem_b}
    ip netns exec "$NS_SERVER" tc qdisc add dev "$VETH_B1" root netem ${netem_b}

    echo "OK: netem applied (A: ${netem_a}, B: ${netem_b})"
}

# ── VPN server ──

ci_stress_start_server() {
    local scheduler="${1:-wlb}"
    _CS_WORK_DIR="$(mktemp -d)"

    _CS_PSK=$("$MQVPN" --genkey 2>/dev/null)

    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
        -keyout "${_CS_WORK_DIR}/server.key" -out "${_CS_WORK_DIR}/server.crt" \
        -days 365 -nodes -subj "/CN=ci-stress" 2>/dev/null

    if [ -n "$CI_STRESS_SERVER_LOG" ]; then exec 3>>"$CI_STRESS_SERVER_LOG"; else exec 3>&1; fi
    ip netns exec "$NS_SERVER" "$MQVPN" \
        --mode server \
        --listen "0.0.0.0:${VPN_LISTEN_PORT}" \
        --subnet 10.0.0.0/24 \
        --cert "${_CS_WORK_DIR}/server.crt" \
        --key "${_CS_WORK_DIR}/server.key" \
        --auth-key "$_CS_PSK" \
        --scheduler "$scheduler" \
        --log-level "$CI_STRESS_LOG_LEVEL" >&3 2>&1 &
    _CS_SERVER_PID=$!
    exec 3>&-
    sleep 2

    if ! kill -0 "$_CS_SERVER_PID" 2>/dev/null; then
        echo "ERROR: VPN server died"
        return 1
    fi
    echo "VPN server running (PID $_CS_SERVER_PID, scheduler=$scheduler)"
}

# ── VPN client ──

ci_stress_start_client() {
    local paths="$1"
    local scheduler="${2:-wlb}"
    local server_addr="${3:-$IP_A_SERVER_ADDR}"

    ci_stress_stop_client

    if [ -n "$CI_STRESS_CLIENT_LOG" ]; then exec 3>>"$CI_STRESS_CLIENT_LOG"; else exec 3>&1; fi
    ip netns exec "$NS_CLIENT" "$MQVPN" \
        --mode client \
        --server "${server_addr}:${VPN_LISTEN_PORT}" \
        ${paths} \
        --auth-key "$_CS_PSK" \
        --scheduler "$scheduler" \
        --insecure \
        --log-level "$CI_STRESS_LOG_LEVEL" >&3 2>&1 &
    _CS_CLIENT_PID=$!
    exec 3>&-
    sleep 3

    if ! kill -0 "$_CS_CLIENT_PID" 2>/dev/null; then
        echo "ERROR: VPN client died"
        return 1
    fi
    echo "VPN client running (PID $_CS_CLIENT_PID)"
}

# ── Tunnel wait ──

ci_stress_wait_tunnel() {
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

# Check before injecting another fault. One dead process invalidates subsequent
# cycles; record the first cause rather than manufacture repeated failures.
ci_stress_check_workloads() {
    _CS_DEAD_PROCESS=""
    local label pid
    while [ "$#" -ge 2 ]; do
        label="$1"; pid="$2"; shift 2
        if ! kill -0 "$pid" 2>/dev/null ||
           ! awk '/^State:/ {found=1; if ($2 == "Z") exit 1} END {if (!found) exit 1}' "/proc/$pid/status" 2>/dev/null; then
            _CS_DEAD_PROCESS="$label"
            return 1
        fi
    done
}

# Sanitizer failure flags (set to 1 if any VPN process exits with ASan/UBSan error)
_CS_CLIENT_SANITIZER_FAIL=0
_CS_SERVER_SANITIZER_FAIL=0

# Always reap owned children, including a child which died before cleanup.
# A watchdog bounds a wedged graceful shutdown; SIGKILL is a test failure.
ci_stress_reap_pid() {
    local pid="$1" label="${2:-process}" watcher done_file rc=0
    done_file="$(mktemp "${CI_STRESS_RAW_DIR}/stop.XXXXXX")"
    if kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null || true; fi
    # Cancel through a marker rather than SIGTERM: killing a background shell
    # before it execs its watchdog can run the caller's inherited EXIT trap.
    python3 -c '
import os, pathlib, signal, sys, time
pid, grace, done = int(sys.argv[1]), float(sys.argv[2]), pathlib.Path(sys.argv[3])
deadline = time.monotonic() + grace
while done.stat().st_size == 0:
    if time.monotonic() >= deadline:
        try: os.kill(pid, signal.SIGKILL)
        except ProcessLookupError: pass
        break
    time.sleep(0.05)
' "$pid" "${CI_STRESS_STOP_GRACE:-10}" "$done_file" 2>/dev/null &
    watcher=$!
    wait "$pid" 2>/dev/null || rc=$?
    printf 'done\n' >"$done_file"
    wait "$watcher" 2>/dev/null || true
    rm -f "$done_file"
    _CS_LAST_EXIT="$rc"
    printf '%s %s %s\n' "$label" "$pid" "$rc" >> "${CI_STRESS_RAW_DIR}/process-exits.log"
    if [ "$rc" -ne 0 ] && [ "$rc" -ne 143 ]; then
        echo "WARN: $label exited $rc (including forced shutdown)"
    fi
}

ci_stress_stop_client() {
    if [ -n "$_CS_CLIENT_PID" ]; then
        ci_stress_reap_pid "$_CS_CLIENT_PID" client
        if [ "$_CS_LAST_EXIT" -ne 0 ] && [ "$_CS_LAST_EXIT" -ne 143 ]; then
            _CS_CLIENT_SANITIZER_FAIL=1
        fi
        _CS_CLIENT_PID=""
        sleep "${CI_STRESS_STOP_PAUSE:-1}"
    fi
}

ci_stress_stop_vpn() {
    ci_stress_stop_client
    if [ -n "$_CS_SERVER_PID" ]; then
        ci_stress_reap_pid "$_CS_SERVER_PID" server
        if [ "$_CS_LAST_EXIT" -ne 0 ] && [ "$_CS_LAST_EXIT" -ne 143 ]; then
            _CS_SERVER_SANITIZER_FAIL=1
        fi
        _CS_SERVER_PID=""
    fi
}

# Check if ASan/UBSan reported errors (accumulated across all stop_client/stop_vpn calls).
# Call after ci_stress_stop_vpn.
ci_stress_check_sanitizer() {
    local failed=0
    if [ "$_CS_SERVER_SANITIZER_FAIL" -ne 0 ]; then
        echo "  FAIL: VPN server reported ASan/UBSan error"
        failed=1
    fi
    if [ "$_CS_CLIENT_SANITIZER_FAIL" -ne 0 ]; then
        echo "  FAIL: VPN client reported ASan/UBSan error"
        failed=1
    fi
    if grep -rE 'ERROR: (AddressSanitizer|LeakSanitizer)|SUMMARY: (AddressSanitizer|UndefinedBehaviorSanitizer)|runtime error:' \
            "$CI_STRESS_RAW_DIR" --include='*.log' >/dev/null 2>&1; then
        echo "  FAIL: sanitizer diagnostic in captured logs"
        failed=1
    fi
    if [ "$failed" -eq 0 ]; then
        echo "  OK: collected process exits and logs show no sanitizer error"
    fi
    return $failed
}

# ── Log assertions (for captured VPN logs) ──

# Wait until <pattern> (extended regex) appears in <log> after line
# <start_line>. Returns 0 on match, 1 on timeout.
#
# G19: deliberately awk, not `tail -n +N | grep -q`. These scripts run under
# `set -o pipefail`, where a grep that exits at its first match can SIGPIPE
# the writer and turn a match into a spurious timeout -- the exact hazard
# scripts/ci_e2e/e2e_lib.sh documents but does not have to handle, because no
# consumer of that library sets pipefail. awk reads to EOF and uses no pipe
# at all. The pattern travels through the environment so awk does not
# reprocess its backslashes the way -v would.
ci_stress_wait_log_after() {
    local log="$1" pattern="$2" start_line="$3" timeout="${4:-15}"
    local elapsed=0
    while [ "$elapsed" -lt "$timeout" ]; do
        if CS_LOG_PAT="$pattern" awk -v start="$start_line" \
                'NR > start && $0 ~ ENVIRON["CS_LOG_PAT"] { found = 1 }
                 END { exit !found }' "$log" 2>/dev/null; then
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    return 1
}

# Echo the last <max> log lines a failing step produced, indented, so the
# console explains the failure instead of only naming it.
ci_stress_dump_log_since() {
    local log="$1" start_line="$2" max="${3:-12}"
    echo "    ── log since the fault (last ${max} lines) ──"
    awk -v start="$start_line" 'NR > start' "$log" 2>/dev/null \
        | tail -n "$max" | sed 's/^/    /'
}

# ── RSS/fd monitoring ──

# Start background monitoring of a process's RSS and fd count.
# Usage: ci_stress_monitor_start PID LOGFILE
# LOGFILE format: timestamp_epoch rss_kb fd_count
ci_stress_monitor_start() {
    local pid="$1"
    local logfile="$2"

    : > "$logfile"

    (
        trap - EXIT TERM INT
        while kill -0 "$pid" 2>/dev/null; do
            local ts rss_kb fd_count
            ts=$(date +%s)
            rss_kb=$(awk '/^VmRSS:/ {print $2}' "/proc/$pid/status" 2>/dev/null || echo 0)
            fd_count=$(ls "/proc/$pid/fd" 2>/dev/null | wc -l)
            echo "$ts $rss_kb $fd_count" >> "$logfile"
            sleep "$CI_STRESS_MONITOR_INTERVAL"
        done
    ) &
    _CS_MONITOR_PIDS+=($!)
}

# Stop all running monitors.
ci_stress_monitor_stop() {
    if [ ${#_CS_MONITOR_PIDS[@]} -eq 0 ]; then return 0; fi
    for mpid in "${_CS_MONITOR_PIDS[@]}"; do
        kill "$mpid" 2>/dev/null || true
        wait "$mpid" 2>/dev/null || true
    done
    _CS_MONITOR_PIDS=()
}

# ASan quarantine and allocator effects make RSS growth inconclusive.
# A readable binary, rather than ASAN_OPTIONS, identifies instrumentation.
ci_stress_is_asan_build() {
    if [ -r "$MQVPN" ]; then
        LC_ALL=C grep -qa "__asan_init" "$MQVPN"
    else
        [ -n "${ASAN_OPTIONS:-}" ]
    fi
}

ci_stress_check_resources() {
    local logfile="$1" label="${2:-process}" asan=0
    ci_stress_is_asan_build && asan=1
    echo "Resource evidence: $label"
    python3 "${CI_STRESS_DIR}/ci_stress_results.py" "$logfile" \
        --asan "$asan" --interval "$CI_STRESS_MONITOR_INTERVAL" \
        --warmup "${CI_STRESS_WARMUP_SEC:-30}"
}

# Kernel-delivered inner bytes at the receiving TUN, independent of iperf's
# control socket and sender-side buffering. Missing counters are invalid.
ci_stress_tun_rx_bytes() {
    local receiver="$NS_SERVER"
    [ "${1:-UL}" != DL ] || receiver="$NS_CLIENT"
    ip netns exec "$receiver" cat /sys/class/net/mqvpn0/statistics/rx_bytes 2>/dev/null
}

# ── Cleanup ──

ci_stress_cleanup() {
    echo ""
    echo "Cleaning up..."

    ci_stress_monitor_stop

    ci_stress_stop_vpn

    ci_stress_kill_ns_processes "$NS_SERVER" iperf3
    ci_stress_kill_ns_processes "$NS_CLIENT" iperf3
    sleep 1

    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_A0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_A1" root 2>/dev/null || true
    ip netns exec "$NS_CLIENT" tc qdisc del dev "$VETH_B0" root 2>/dev/null || true
    ip netns exec "$NS_SERVER" tc qdisc del dev "$VETH_B1" root 2>/dev/null || true

    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true

    [ -n "$_CS_WORK_DIR" ] && rm -rf "$_CS_WORK_DIR" || true
}
