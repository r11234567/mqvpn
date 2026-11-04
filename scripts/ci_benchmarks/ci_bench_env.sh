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
IP_A_SUBNET="10.100.0.0/24"
IP_B_SERVER_ADDR="10.200.0.1"
TUNNEL_SERVER_IP="10.0.0.1"
VPN_LISTEN_PORT="4433"
IPERF3_PORT="5201"
CI_BENCH_SCHEDULER="${CI_BENCH_SCHEDULER:-wlb}"
CI_BENCH_LOG_LEVEL="${CI_BENCH_LOG_LEVEL:-error}"
# Stable across command substitutions; every sample retains its raw evidence.
CI_BENCH_RAW_DIR="${CI_BENCH_RAW_DIR:-${CI_BENCH_RESULTS}/raw/$(date -u +%Y%m%d_%H%M%S)-$$}"
CI_BENCH_RESULT_TOOL="${CI_BENCH_DIR}/ci_bench_results.py"
export CI_BENCH_COMMIT CI_BENCH_RAW_DIR CI_BENCH_SCHEDULER

# WLB scheduler instrumentation. With this set to 1 both ends run at
# --log-level info, so the once-a-second
# |wlb_instr| line xquic emits (pins, packets and round count per path) can be
# read back after a measurement. Off by default: it raises the log level for
# the whole run.
#
# The counters are aggregated rather than logged per packet. Logging can still
# perturb a shared runner; compare matching log settings and measure the cost.
#
# The SERVER log is the one that answers the question. Every measurement here
# is iperf3 in the DL direction, so the bulk data is scheduled by the server's
# WLB instance; the client's only schedules the returning ACKs. The first
# attempt read the client log and would have reported the ACK split as though
# it were the throughput split.
CI_BENCH_WLB_INSTR="${CI_BENCH_WLB_INSTR:-0}"
# Logs are always retained; instrumentation only changes their level.
# Set by the start helpers. The server log is NOT
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

ci_bench_config_bool() {
    case "$1" in
        on|true) printf 'true\n' ;;
        off|false) printf 'false\n' ;;
        *) echo "invalid boolean arm: $1" >&2; return 2 ;;
    esac
}

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
_CB_SERVER_EVIDENCE=""
_CB_CLIENT_EVIDENCE=""

# Keep every process instance, including clients which never form a tunnel.
# Do not record argv/auth keys or the private key in these public artifacts.
ci_bench_vpn_evidence() {
    local role="$1" scheduler="$2" paths="${3:-}"
    mkdir -p "$CI_BENCH_RAW_DIR/vpn"
    _CB_LAST_EVIDENCE="$(mktemp -d "$CI_BENCH_RAW_DIR/vpn/${role}.XXXXXXXX")"
    python3 - "$_CB_LAST_EVIDENCE/context.json" "$role" "$scheduler" "$paths" \
        "${FUNCNAME[*]}" "$_CB_SERVER_EVIDENCE" "${class:-${CI_BENCH_SCENARIO:-unknown}}" <<'PY'
import datetime, json, os, sys
from pathlib import Path
output, role, scheduler, paths, stack, server, scenario = sys.argv[1:]
Path(output).write_text(json.dumps(dict(role=role, scheduler=scheduler, paths=paths,
    scenario=scenario, caller_stack=stack, server_evidence=server if role=='client' else None,
    started_at=datetime.datetime.now(datetime.timezone.utc).isoformat(),
    arm=os.environ.get('CI_BENCH_ARM','default'),
    direction=os.environ.get('CI_BENCH_DIRECTION','DL'),
    pmtud=os.environ.get('CI_BENCH_PMTUD','default_off'))) + '\n')
PY
}

ci_bench_record_vpn_exit() {
    local role="$1" pid="$2" rc="$3" evidence="$4"
    [ -n "$evidence" ] || return 0
    python3 - "$evidence/exit.json" "$role" "$pid" "$rc" <<'PY'
import datetime, json, sys
from pathlib import Path
p, role, pid, rc = sys.argv[1:]
Path(p).write_text(json.dumps(dict(role=role,pid=int(pid),exit_code=int(rc),
    exit_observed=int(rc)!=127,
    recorded_at=datetime.datetime.now(datetime.timezone.utc).isoformat())) + '\n')
PY
}

# ── Dependency checks ──

ci_bench_check_deps() {
    if [ ! -f "$MQVPN" ]; then
        echo "error: mqvpn binary not found at $MQVPN"
        exit 1
    fi
    MQVPN="$(realpath "$MQVPN")"

    for cmd in iperf3 openssl python3 tc timeout ss; do
        if ! command -v "$cmd" &>/dev/null; then
            echo "error: $cmd not found"
            exit 1
        fi
    done

    mkdir -p "$CI_BENCH_RESULTS" "$CI_BENCH_RAW_DIR"
}

# ── Stale state cleanup (run before setup) ──

# A network namespace does not isolate /proc or pkill. Enumerate its actual
# members, optionally matching comm, so another harness cannot be killed.
ci_bench_kill_ns_processes() {
    local ns="$1" comm="${2:-}" pid actual
    for pid in $(ip netns pids "$ns" 2>/dev/null); do
        actual="$(cat "/proc/$pid/comm" 2>/dev/null)" || continue
        [ -z "$comm" ] || [ "$actual" = "$comm" ] || continue
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done
}

ci_bench_cleanup_stale() {
    ci_bench_kill_ns_processes "$NS_SERVER"
    ci_bench_kill_ns_processes "$NS_CLIENT"
    ci_bench_kill_ns_processes "$NS_SERVER" iperf3
    ci_bench_kill_ns_processes "$NS_CLIENT" iperf3
    ip netns del "$NS_SERVER" 2>/dev/null || true
    ip netns del "$NS_CLIENT" 2>/dev/null || true
    ip link del "$VETH_A0" 2>/dev/null || true
    ip link del "$VETH_B0" 2>/dev/null || true
}

# ── Path B route to the server ──

# Backup route on the client so Path B can carry traffic to the server
# address even when Path A is down. metric 200 keeps it as a fallback under
# normal conditions. It is also what the client's path re-add gate
# (route_check.c, RTM_F_FIB_MATCH) requires: without a FIB route via B the
# gate defers the re-add forever, even though a SO_BINDTODEVICE socket would
# still send through the kernel's on-link fallback. A benchmark that faults
# Path B must call this again on recovery.
#
# `replace`, not `add`: an admin down flushes every route through the link
# while a carrier loss keeps them and only flags them linkdown, so `add`
# would succeed in one case and fail with EEXIST in the other. `replace`
# states the intent ("this route must exist now") for both, which is what
# lets callers drop the `|| true` that would otherwise hide a real failure.
ci_bench_add_path_b_route() {
    ip netns exec "$NS_CLIENT" ip route replace "$IP_A_SUBNET" via "$IP_B_SERVER_ADDR" \
        dev "$VETH_B0" metric 200
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

    ci_bench_add_path_b_route

    # Verify
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$IP_A_SERVER_ADDR" >/dev/null
    ip netns exec "$NS_CLIENT" ping -c 1 -W 1 "$IP_B_SERVER_ADDR" >/dev/null
    # The exact FIB query the client's path re-add gate performs
    # (src/platform/linux/route_check.c). A ping via B cannot tell the
    # difference (the kernel's on-link fallback also answers), this can.
    ip netns exec "$NS_CLIENT" ip route get "$IP_A_SERVER_ADDR" oif "$VETH_B0" fibmatch >/dev/null

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
    CI_BENCH_SERVER_LOG_MARK=0
    ci_bench_vpn_evidence server "$scheduler"
    _CB_SERVER_EVIDENCE="$_CB_LAST_EVIDENCE"
    CI_BENCH_SERVER_LOG="$_CB_SERVER_EVIDENCE/vpn.log"
    if [ "$CI_BENCH_WLB_INSTR" = "1" ]; then
        level=info
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
    ci_bench_vpn_evidence client "$scheduler" "$paths"
    _CB_CLIENT_EVIDENCE="$_CB_LAST_EVIDENCE"
    CI_BENCH_CLIENT_LOG="$_CB_CLIENT_EVIDENCE/vpn.log"
    if [ "$CI_BENCH_WLB_INSTR" = "1" ] && [ -n "$_CB_WORK_DIR" ]; then
        level=info
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
    local pid live=0 rc=0
    # Reap the directly owned child even if it already exited. Earlier logs
    # remain independent when the next client starts.
    if [ -n "$_CB_CLIENT_PID" ]; then
        if kill -0 "$_CB_CLIENT_PID" 2>/dev/null && _cb_pid_is_client "$_CB_CLIENT_PID"; then
            kill "$_CB_CLIENT_PID" 2>/dev/null || true
            live=$((live + 1))
        fi
        wait "$_CB_CLIENT_PID" 2>/dev/null || rc=$?
        ci_bench_record_vpn_exit client "$_CB_CLIENT_PID" "$rc" "$_CB_CLIENT_EVIDENCE"
    fi
    # `wait` only works on our own children, and a pid from the file may be a
    # subshell's child -- hence the `|| true` and the settle sleep below.
    if [ -f "$_CB_CLIENT_PIDS" ]; then
        while read -r pid; do
            [ -n "$pid" ] || continue
            [ "$pid" != "$_CB_CLIENT_PID" ] || continue
            kill -0 "$pid" 2>/dev/null || continue
            _cb_pid_is_client "$pid" || continue
            live=$((live + 1))
            kill "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
        done < "$_CB_CLIENT_PIDS"
        : > "$_CB_CLIENT_PIDS"
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
    local rc=0
    ci_bench_stop_client
    if [ -n "$_CB_SERVER_PID" ]; then
        kill "$_CB_SERVER_PID" 2>/dev/null || true
        wait "$_CB_SERVER_PID" 2>/dev/null || rc=$?
        ci_bench_record_vpn_exit server "$_CB_SERVER_PID" "$rc" "$_CB_SERVER_EVIDENCE"
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

ci_bench_control_capture_start() {
    local ns="$1" prefix="$2" i
    _CB_CAPTURE_PID=""
    if ! command -v tcpdump >/dev/null; then
        printf 'CAPTURE UNAVAILABLE: tcpdump not installed\n' >"${prefix}.capture.log"
        return 0
    fi
    # -U flushes the output writer only. Immediate mode also releases short
    # control exchanges from libpcap before a fast failure stops the capture.
    ip netns exec "$ns" tcpdump --immediate-mode -n -i any -s 0 -U -B 8192 \
        -w "${prefix}.control.pcap" "tcp port ${IPERF3_PORT} or icmp or icmp6 or (udp port ${IPERF3_PORT} and udp[4:2] = 12)" \
        >"${prefix}.capture.log" 2>&1 &
    _CB_CAPTURE_PID=$!
    for ((i=0; i<50; i++)); do
        grep -q 'listening on' "${prefix}.capture.log" && return 0
        kill -0 "$_CB_CAPTURE_PID" 2>/dev/null || break
        sleep .05
    done
    printf '\nCAPTURE NOT READY before client start\n' >>"${prefix}.capture.log"
}

ci_bench_control_capture_stop() {
    local pid="$1" prefix="$2" rc=0
    [ -n "$pid" ] || return 0
    kill -INT "$pid" 2>/dev/null || true
    python3 "${CI_BENCH_DIR}/ci_bench_process.py" "$pid" "${prefix}.capture-process.json" --grace 1 --stop-grace 1
    wait "$pid" 2>/dev/null || rc=$?
    printf '%s\n' "$rc" >"${prefix}.capture-exit.txt"
}

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

    local iperf_server_ns="$NS_SERVER" iperf_client_ns="$NS_CLIENT"
    # iperf3 3.16 reverse UDP can deliver data before UDP_CONNECT_REPLY and
    # exhaust the client's bounded startup reader. Swap application roles
    # for DL instead: the ordinary UDP client completes its handshake before
    # it sends data. The measured direction is still VPN server -> client.
    # Route-selected source also handles the direct (non-tunnel) baseline.
    if [ "$proto" = UDP ] && [ "$dir" = DL ]; then
        _cb_iperf_target="$(ip netns exec "$NS_CLIENT" ip -j route get "$_cb_iperf_target" |
            python3 -c 'import json,sys; r=json.load(sys.stdin)[0]; print(r.get("prefsrc") or r.get("src") or "")')"
        [ -n "$_cb_iperf_target" ] || { echo 'receiver address unavailable' >&2; return 2; }
        iperf_server_ns="$NS_CLIENT"
        iperf_client_ns="$NS_SERVER"
    fi

    local json_file
    json_file="$(mktemp --suffix=.json)"

    # The application receiver owns the listener (roles swap for UDP DL).
    # TCP retains the conventional client/server roles.
    # TCP DL uses -R; UDP DL swaps application roles to avoid reverse startup.
    # UL retains the ordinary forward application roles for both protocols.
    # Wait for the port to come free, then for the new server to own it. A
    # scenario issues many samples back to back on this one port, and the
    # previous sample's one-shot server can still be releasing its listener:
    # bind then loses the race and dies into &>/dev/null, the client reaches
    # the closing socket instead, and the sample comes back as a mid-transfer
    # "Broken pipe" that looks like the emulated path failed.
    local i
    for (( i=0; i<20; i++ )); do
        ip netns exec "$iperf_server_ns" ss -ltn 2>/dev/null \
            | grep -q ":${IPERF3_PORT} " || break
        sleep 0.5
    done

    local server_log="${json_file}.server.log" server_observation="${json_file}.server-process.json"
    ip netns exec "$iperf_server_ns" iperf3 -s -p "$IPERF3_PORT" -B "$_cb_iperf_target" -1 --forceflush \
        >"$server_log" 2>&1 &
    local iperf_srv_pid=$!

    for (( i=0; i<20; i++ )); do
        ip netns exec "$iperf_server_ns" ss -ltn 2>/dev/null \
            | grep -q ":${IPERF3_PORT} " && break
        sleep 0.5
    done

    if ! kill -0 "$iperf_srv_pid" 2>/dev/null || [ "$i" -eq 20 ]; then
        printf 'iperf3: error - listener did not become ready\n' >>"$server_log"
    fi

    local args="-c $_cb_iperf_target -p $IPERF3_PORT -t $duration -P $parallel --json"
    [ "$proto" = "UDP" ] && args="$args -u"
    [ -n "$target_bw" ] && args="$args -b $target_bw"
    [ "$dir" = "DL" ] && [ "$proto" != UDP ] && args="$args -R"
    # Payload size, for the small-packet profiles. Left unset by every bulk
    # caller so iperf3's own default applies and their numbers do not move.
    [ -n "${CI_BENCH_IPERF_LEN:-}" ] && args="$args -l ${CI_BENCH_IPERF_LEN}"
    # Per-second interval samples, so a caller that needs the shape of the
    # transfer can read intervals[] the way ci_bench_failover.sh:180 does.
    [ -n "${CI_BENCH_IPERF_INTERVAL:-}" ] && args="$args -i ${CI_BENCH_IPERF_INTERVAL}"
    local evidence_tool="${CI_BENCH_DIR}/ci_bench_iperf_evidence.py" capture_client="" capture_server=""
    python3 "$evidence_tool" context "$json_file" --client "$iperf_client_ns" --server "$iperf_server_ns" \
        --target "$_cb_iperf_target" --proto "$proto" --direction "$dir" --client-args="$args" \
        --scenario "${class:-${CI_BENCH_SCENARIO:-unknown}}"
    if [ "$proto" = UDP ]; then
        python3 "$evidence_tool" snapshot "$json_file" --phase before --client "$iperf_client_ns" --server "$iperf_server_ns" --target "$_cb_iperf_target"
        ci_bench_control_capture_start "$iperf_client_ns" "${json_file}.client"; capture_client="$_CB_CAPTURE_PID"
        ci_bench_control_capture_start "$iperf_server_ns" "${json_file}.server"; capture_server="$_CB_CAPTURE_PID"
    fi

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
    local iperf_exit=0
    ip netns exec "$iperf_client_ns" timeout --kill-after=5 $((duration + 20)) \
        iperf3 $args > "$json_file" 2>"${json_file}.client.stderr.log" || iperf_exit=$?
    cp "$json_file" "${json_file}.client.stdout.log"
    if [ "$proto" = UDP ]; then
        python3 "$evidence_tool" snapshot "$json_file" --phase after-client --client "$iperf_client_ns" --server "$iperf_server_ns" --target "$_cb_iperf_target"
    fi

    # Allow the one-shot server to finish exchanging results before cleanup.
    # Preserve a bounded forced-stop decision and its actual exit code.
    local server_grace="${CI_BENCH_IPERF_SERVER_GRACE:-30}"
    [ "$iperf_exit" -eq 0 ] || server_grace=2
    python3 "${CI_BENCH_DIR}/ci_bench_process.py" "$iperf_srv_pid" "$server_observation" --grace "$server_grace"
    local server_exit=0
    wait "$iperf_srv_pid" 2>/dev/null || server_exit=$?
    ci_bench_control_capture_stop "$capture_client" "${json_file}.client"
    ci_bench_control_capture_stop "$capture_server" "${json_file}.server"

    python3 "$CI_BENCH_RESULT_TOOL" record "$json_file" \
        --duration "$duration" --exit-code "$iperf_exit" --server-exit-code "$server_exit" \
        --server-observation "$server_observation"
    python3 "$evidence_tool" finalize "$json_file" --archive "$CI_BENCH_RAW_DIR" \
        --vpn-client "$CI_BENCH_CLIENT_LOG" --vpn-server "$CI_BENCH_SERVER_LOG"
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
    python3 "$CI_BENCH_RESULT_TOOL" udp "$json_file"
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
    python3 "$CI_BENCH_RESULT_TOOL" throughput "$json_file"
}

# ── Sanity check ──

ci_bench_sanity_check() {
    local json_file="$1"
    local desc="${2:-benchmark}"

    if ! python3 "$CI_BENCH_RESULT_TOOL" core "$json_file"; then
        echo "ERROR: $desc has missing, invalid or incomplete measurements" >&2
        return 1
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

    ci_bench_kill_ns_processes "$NS_SERVER" iperf3
    ci_bench_kill_ns_processes "$NS_CLIENT" iperf3
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
