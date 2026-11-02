#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Actual namespaces plus error injection while the caller disables errexit.
# shellcheck disable=SC2218 # ip is deliberately overridden only in the final fault injection.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")/../scripts/ci_benchmarks" && pwd)"
source "$SCRIPT_DIR/ci_bench_env.sh"
source "$SCRIPT_DIR/ci_bench_netsim.sh"
work="$(mktemp -d)"
CI_BENCH_RAW_DIR="$work"
trap 'netsim_teardown; rm -rf "$work"' EXIT
netsim_detect_caps
netsim_setup 1
netsim_apply_path 0 'eth:iplc:public' 4242
ip netns exec "$NETSIM_NS_CLIENT" ping -c 1 -W 2 "$NETSIM_SERVER_ADDR" >/dev/null
echo 'PASS routed path and installed qdisc evidence'
netsim_set_pmtud_blackhole 0 1280
ip netns exec "$NETSIM_NS_CLIENT" ping -M do -s 1100 -c 1 -W 2 "$NETSIM_SERVER_ADDR" >/dev/null
if ip netns exec "$NETSIM_NS_CLIENT" ping -M do -s 1372 -c 1 -W 1 "$NETSIM_SERVER_ADDR" >/dev/null; then
    echo 'missing actual MTU blackhole' >&2; exit 1
fi
ip netns exec "$(netsim_hop_ns 0)" iptables -C OUTPUT -p icmp --icmp-type fragmentation-needed -j DROP
echo 'PASS real asymmetric MTU and ICMP suppression'
NETSIM_HAVE_NAT=0
if netsim_apply_nat 0 port_restricted; then echo 'NAT silently degraded' >&2; exit 1; fi
NETSIM_HAVE_PPS=0
if netsim_apply_pps_cap 0 1000; then echo 'PPS silently degraded' >&2; exit 1; fi
NETSIM_HAVE_ICMP_DROP=0
if netsim_set_pmtud_blackhole 0 1280; then echo 'blackhole silently degraded' >&2; exit 1; fi
echo 'PASS requested unsupported emulation fails'
# Test functions as conditions; Bash ignores set -e in their entire bodies.
ip() { return 42; }
if netsim_set_mtu 0 1300; then echo 'MTU command error swallowed' >&2; exit 1; fi
if netsim_setup 1; then echo 'setup command error swallowed' >&2; exit 1; fi
unset -f ip
echo 'PASS conditional setup propagates command failure'
netsim_teardown
NS_SERVER="bench-cleanup-a-$$"
NS_CLIENT="bench-cleanup-b-$$"
for ns in "$NS_SERVER" "$NS_CLIENT"; do
    ip netns add "$ns"
    ip netns exec "$ns" ip link set lo up
done
trap 'ci_bench_kill_ns_processes "$NS_SERVER"; ci_bench_kill_ns_processes "$NS_CLIENT"; ip netns del "$NS_SERVER"; ip netns del "$NS_CLIENT"; rm -rf "$work"' EXIT
ip netns exec "$NS_SERVER" iperf3 -s -p 5499 >"$work/isolated-a.log" 2>&1 &
first=$!
ip netns exec "$NS_CLIENT" iperf3 -s -p 5499 >"$work/isolated-b.log" 2>&1 &
second=$!
sleep .3
ci_bench_kill_ns_processes "$NS_SERVER" iperf3
kill -0 "$second" # cleaning the first namespace must preserve the second
if kill -0 "$first" 2>/dev/null; then echo 'first workload survived cleanup' >&2; exit 1; fi
echo 'PASS cleanup cannot kill a second namespace workload'
