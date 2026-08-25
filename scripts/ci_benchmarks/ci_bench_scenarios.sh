#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
#
# ci_bench_scenarios.sh — realistic-network scenario benchmarks.
#
#   sudo ./ci_bench_scenarios.sh percommit          [mqvpn]
#   sudo ./ci_bench_scenarios.sh classes            [mqvpn]
#   sudo ./ci_bench_scenarios.sh catalog <transit>  [mqvpn]
#   sudo ./ci_bench_scenarios.sh special            [mqvpn]
#
# Paths are built by ci_bench_netsim.sh as access-leg + transit-leg chains;
# see docs/network_emulation_matrix.md for why the matrix is sampled this way
# rather than enumerated.
#
# This reuses ci_bench_env.sh's VPN/iperf helpers verbatim by overriding the
# three variables they read for topology (NS_SERVER, NS_CLIENT,
# IP_A_SERVER_ADDR). Duplicating them would have been the larger change.
#
# Output: one JSON per mode into ci_bench_results/, with results[] keyed by
# `scenario` so the dashboard labels each series (docs section 7).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/ci_bench_env.sh"
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/ci_bench_netsim.sh"

MODE="${1:-percommit}"
case "$MODE" in
  catalog) CATALOG_TRANSIT="${2:-bgp_junk}"; MQVPN="${3:-${MQVPN:-./build/mqvpn}}" ;;
  *)       MQVPN="${2:-${MQVPN:-./build/mqvpn}}" ;;
esac

# Point the inherited helpers at the netsim topology.
NS_SERVER="$NETSIM_NS_SERVER"
NS_CLIENT="$NETSIM_NS_CLIENT"
IP_A_SERVER_ADDR="$NETSIM_SERVER_ADDR"

CTRL_PORT=9099
IPERF_SEC="${CI_BENCH_IPERF_SEC:-6}"
# Repeats per measurement. Two on the per-commit gate (it must not become the
# slowest thing in the push path), three where the number feeds a trend line.
REPEATS="${CI_BENCH_REPEATS:-2}"

CI_BENCH_RESULTS="${CI_BENCH_RESULTS:-${SCRIPT_DIR}/../../ci_bench_results}"
mkdir -p "$CI_BENCH_RESULTS"
ROWS="$(mktemp)"
trap 'rm -f "$ROWS"; netsim_spike_stop "${SPIKE_PID:-}"; ci_bench_stop_vpn 2>/dev/null || true; netsim_teardown' EXIT

# ── helpers ────────────────────────────────────────────────────────────────

netsim_query_control() {
    ip netns exec "$NS_SERVER" bash -c \
        "echo '{\"cmd\":\"$1\"}' | timeout 3 nc 127.0.0.1 ${CTRL_PORT}" 2>/dev/null || true
}

# median of the numeric args (bash has no float sort worth writing)
med() { python3 -c "
import sys
v=sorted(float(x) for x in sys.argv[1:] if x)
print('0.0' if not v else f'{(v[len(v)//2] if len(v)%2 else (v[len(v)//2-1]+v[len(v)//2])/2):.2f}')" "$@"; }

cv_pct() { python3 -c "
import sys, statistics as s
v=[float(x) for x in sys.argv[1:] if x]
print('0.0' if len(v)<2 or s.fmean(v)==0 else f'{s.stdev(v)/abs(s.fmean(v))*100:.1f}')" "$@"; }

# measure_pathset "<--path args>" -> echoes "mbps_median cv_pct"
# Restarts the client so only the requested paths exist, then measures.
measure_pathset() {
    local paths="$1"
    ci_bench_start_client "$paths" >/dev/null 2>&1 || { echo "0.0 0.0"; return; }
    if ! ci_bench_wait_tunnel 25 >/dev/null 2>&1; then echo "0.0 0.0"; return; fi

    local samples=() i jf
    for (( i=0; i<REPEATS; i++ )); do
        jf="$(ci_bench_run_iperf TCP DL "$IPERF_SEC" 1)"
        samples+=("$(ci_bench_parse_throughput "$jf")")
        rm -f "$jf"
    done
    echo "$(med "${samples[@]}") $(cv_pct "${samples[@]}")"
}

# Scrape the control API for everything that is not throughput. Echoes a JSON
# fragment (no braces) so the row builder can splice it in.
collect_stats() {
    local status stats
    status="$(netsim_query_control get_status)"
    stats="$(netsim_query_control get_stats)"
    python3 -c "
import json, sys
def load(s):
    try: return json.loads(s)
    except Exception: return {}
st, gs = load(sys.argv[1]), load(sys.argv[2])
out = {}
cl = (st.get('clients') or [{}])[0]
paths = cl.get('paths') or []
if paths:
    srtt = [p.get('srtt_ms', 0) for p in paths]
    minr = [p.get('min_rtt_ms', 0) for p in paths]
    load_ = [p.get('bytes_tx', 0) + p.get('bytes_rx', 0) for p in paths]
    out['srtt_ms'] = max(srtt)
    out['min_rtt_ms'] = max(minr)
    # Bufferbloat readout: how far the working RTT sits above the floor.
    out['rtt_inflation'] = round(max(srtt) / max(minr), 3) if max(minr) else 0
    out['pkt_lost'] = sum(p.get('pkt_lost', 0) for p in paths)
    hi, lo = max(load_), min(load_)
    # 0 means one path carried nothing at all -- the failure this catches.
    out['path_minshare'] = round(lo / hi, 3) if hi else 0
for k in ('dgram_lost', 'dgram_sent', 'bytes_tx', 'bytes_rx'):
    if k in gs: out[k] = gs[k]
# Batching factors: the readout that makes carrier_qos actionable, since a
# packet-rate cap makes goodput scale with bytes-per-packet.
if gs.get('udp_tx_sends'):
    out['gso_factor'] = round(gs['udp_tx_datagrams'] / gs['udp_tx_sends'], 2)
if gs.get('udp_rx_receives'):
    out['gro_factor'] = round(gs['udp_rx_datagrams'] / gs['udp_rx_receives'], 2)
print(','.join(json.dumps(k) + ':' + json.dumps(v) for k, v in out.items()))
" "$status" "$stats"
}

server_rss_kb() {
    [ -n "${_CB_SERVER_PID:-}" ] || { echo 0; return; }
    awk '/VmHWM/{print $2}' "/proc/${_CB_SERVER_PID}/status" 2>/dev/null || echo 0
}

start_server_with_ctrl() {
    ci_bench_start_server "${1:-$CI_BENCH_SCHEDULER}" "--control-port ${CTRL_PORT}"
}

# ── scenario: one heterogeneity class (solo A, solo B, both) ───────────────
run_class() {
    local class="$1" sched="${2:-$CI_BENCH_SCHEDULER}"
    local spec="${NETSIM_CLASS[$class]:-}"
    [ -n "$spec" ] || { echo "unknown class $class" >&2; return 1; }

    echo ""
    echo "── class ${class} (scheduler=${sched}) ──"
    # Defensive: an earlier scenario that returned early may have left a
    # server running. Deleting a namespace does not kill what runs inside it,
    # so without this the orphans accumulate across the loop.
    ci_bench_stop_vpn 2>/dev/null || true
    netsim_setup 2 >/dev/null || return 1
    netsim_apply_class "$class" 4242 || return 1

    # A flapping path needs its storm running for the whole measurement,
    # otherwise the class degenerates into its stable base profile.
    SPIKE_PID=""
    local t0 t1
    t0="$(netsim_class_transit "$class" 0)"; t1="$(netsim_class_transit "$class" 1)"
    if [ -n "${NETSIM_SPIKE[$t1]:-}" ]; then
        SPIKE_PID="$(netsim_spike_start 1 "$t1" 8 20 35)"
    elif [ -n "${NETSIM_SPIKE[$t0]:-}" ]; then
        SPIKE_PID="$(netsim_spike_start 0 "$t0" 8 20 35)"
    fi

    start_server_with_ctrl "$sched" >/dev/null || return 1

    local dev0 dev1 a b mp
    dev0="$(netsim_veth_cli 0)"; dev1="$(netsim_veth_cli 1)"
    read -r a _   < <(measure_pathset "--path $dev0")
    read -r b _   < <(measure_pathset "--path $dev1")
    read -r mp cv < <(measure_pathset "--path $dev0 --path $dev1")

    local stats rss
    stats="$(collect_stats)"; rss="$(server_rss_kb)"
    netsim_spike_stop "${SPIKE_PID:-}"; SPIKE_PID=""
    ci_bench_stop_vpn

    # Ratios are the gate-able numbers: they divide out the runner's speed,
    # which absolute Mbps on a shared vCPU cannot.
    python3 -c "
import json,sys
a,b,mp = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3])
row = {
  'scenario': sys.argv[4], 'scheduler': sys.argv[5],
  'path_a': sys.argv[6], 'path_b': sys.argv[7],
  'solo_a_mbps': a, 'solo_b_mbps': b, 'multipath_mbps': mp,
  'aggregation_efficiency': round(mp/(a+b), 3) if a+b else 0,
  'vs_best_single': round(mp/max(a,b), 3) if max(a,b) else 0,
  'multipath_cv_pct': float(sys.argv[8]),
  'server_rss_peak_kb': int(sys.argv[9]),
}
extra = sys.argv[10]
if extra: row.update(json.loads('{' + extra + '}'))
print(json.dumps(row))" \
        "$a" "$b" "$mp" "$class" "$sched" \
        "${spec%%|*}" "${spec##*|}" "$cv" "$rss" "$stats" >> "$ROWS"

    echo "   solo_a=${a} solo_b=${b} mp=${mp} Mbps  (cv ${cv}%)"
    netsim_teardown
}

# ── scenario: one transit profile across every access leg, single path ────
run_catalog() {
    local transit="$1" leg
    for leg in eth wifi_good wifi_busy 5g_full 5g_half 5g_edge 5g_throttled starlink geo_sat tether_otg; do
        echo ""
        echo "── catalog ${transit} + ${leg} ──"
        ci_bench_stop_vpn 2>/dev/null || true   # see run_class
        netsim_setup 1 >/dev/null || continue
        netsim_apply_path 0 "$leg" "$transit" 4242 || { netsim_teardown; continue; }
        start_server_with_ctrl >/dev/null || { netsim_teardown; continue; }

        local mbps cv stats rss
        read -r mbps cv < <(measure_pathset "--path $(netsim_veth_cli 0)")
        stats="$(collect_stats)"; rss="$(server_rss_kb)"
        ci_bench_stop_vpn

        python3 -c "
import json,sys
row={'scenario':sys.argv[1],'access':sys.argv[2],'transit':sys.argv[3],
     'single_path_mbps':float(sys.argv[4]),'cv_pct':float(sys.argv[5]),
     'server_rss_peak_kb':int(sys.argv[6])}
extra=sys.argv[7]
if extra: row.update(json.loads('{'+extra+'}'))
print(json.dumps(row))" \
            "${transit}+${leg}" "$leg" "$transit" "$mbps" "$cv" "$rss" "$stats" >> "$ROWS"

        echo "   ${mbps} Mbps (cv ${cv}%)"
        netsim_teardown
    done
}

# ── scenario: special conditions, under saturating load ───────────────────
# All of these already have functional coverage under scripts/ci_e2e/. What
# was missing is running them while traffic is in flight, which is where the
# timing and buffering bugs actually live.
run_special() {
    local hop dev_up

    # 1. NAT state aging. conntrack's UDP timeout defaults to 30 s, so the
    #    "silent killer" is the default -- this just has to idle past it.
    echo ""
    echo "── special: nat_aging (idle 35s behind NAT, mid-session) ──"
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 5g_half bgp_plain 4242; then
        hop="$(netsim_hop_ns 0)"
        # Rule first, sysctl second: the nf_conntrack_* knobs only exist once
        # the module has been pulled in, which adding the NAT rule does.
        ip netns exec "$hop" iptables -t nat -A POSTROUTING \
            -o "$(netsim_veth_hop_srv 0)" -j MASQUERADE 2>/dev/null || true
        ip netns exec "$hop" sysctl -qw net.netfilter.nf_conntrack_udp_timeout=30 2>/dev/null || true

        if start_server_with_ctrl >/dev/null \
           && ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1 \
           && ci_bench_wait_tunnel 25 >/dev/null 2>&1; then
            local before after jf
            jf="$(ci_bench_run_iperf TCP DL 4 1)"; before="$(ci_bench_parse_throughput "$jf")"; rm -f "$jf"
            sleep 35
            jf="$(ci_bench_run_iperf TCP DL 4 1)"; after="$(ci_bench_parse_throughput "$jf")"; rm -f "$jf"
            python3 -c "
import json,sys
b,a=float(sys.argv[1]),float(sys.argv[2])
print(json.dumps({'scenario':'nat_aging','before_idle_mbps':b,'after_idle_mbps':a,
 'survived_ratio':round(a/b,3) if b else 0,'recovered':1 if a>0.5 else 0}))" \
                "$before" "$after" >> "$ROWS"
            echo "   before=${before} after=${after} Mbps"
        fi
        ci_bench_stop_vpn || true
        netsim_teardown
    fi

    # 2. Corrupt / reorder / duplicate: radio-grade damage under load.
    echo ""
    echo "── special: corrupt_reorder (under load) ──"
    NETSIM_ACCESS[_damaged]="delay 30ms 10ms distribution normal corrupt 0.1% reorder 25% 50% duplicate 0.5% rate 100mbit"
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 _damaged bgp_plain 4242 \
       && start_server_with_ctrl >/dev/null; then
        local mbps cv stats
        read -r mbps cv < <(measure_pathset "--path $(netsim_veth_cli 0)")
        stats="$(collect_stats)"
        python3 -c "
import json,sys
row={'scenario':'corrupt_reorder','single_path_mbps':float(sys.argv[1]),
     'cv_pct':float(sys.argv[2]),'survived':1 if float(sys.argv[1])>0 else 0}
extra=sys.argv[3]
if extra: row.update(json.loads('{'+extra+'}'))
print(json.dumps(row))" "$mbps" "$cv" "$stats" >> "$ROWS"
        echo "   ${mbps} Mbps"
        ci_bench_stop_vpn || true
    fi
    netsim_teardown

    # 3. ACK starvation: saturate a deep uplink queue and see whether the
    #    downlink survives it. Asymmetric with a bloated up-queue is the
    #    classic consumer-broadband shape, and the thing that makes it a real
    #    test is that the ACKs for the downlink have to share that queue.
    echo ""
    echo "── special: ack_starvation (asymmetric + bloated uplink) ──"
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 eth bgp_plain 4242; then
        hop="$(netsim_hop_ns 0)"; dev_up="$(netsim_veth_hop_srv 0)"
        ip netns exec "$hop" tc qdisc replace dev "$dev_up" root \
            netem delay 30ms rate 5mbit limit 12000 || true
        if start_server_with_ctrl >/dev/null \
           && ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1 \
           && ci_bench_wait_tunnel 25 >/dev/null 2>&1; then
            local dl_idle dl_busy ulpid jf2
            jf2="$(ci_bench_run_iperf TCP DL 5 1)"; dl_idle="$(ci_bench_parse_throughput "$jf2")"; rm -f "$jf2"
            ip netns exec "$NS_SERVER" iperf3 -s -B "$TUNNEL_SERVER_IP" -p 5202 -1 &>/dev/null &
            sleep 1
            ip netns exec "$NS_CLIENT" iperf3 -c "$TUNNEL_SERVER_IP" -p 5202 -t 12 &>/dev/null &
            ulpid=$!
            sleep 3
            jf2="$(ci_bench_run_iperf TCP DL 5 1)"; dl_busy="$(ci_bench_parse_throughput "$jf2")"; rm -f "$jf2"
            kill "$ulpid" 2>/dev/null || true; wait "$ulpid" 2>/dev/null || true
            python3 -c "
import json,sys
i,b=float(sys.argv[1]),float(sys.argv[2])
print(json.dumps({'scenario':'ack_starvation','dl_idle_mbps':i,'dl_under_ul_load_mbps':b,
 'dl_retention':round(b/i,3) if i else 0}))" "$dl_idle" "$dl_busy" >> "$ROWS"
            echo "   dl_idle=${dl_idle} dl_under_load=${dl_busy} Mbps"
        fi
        ci_bench_stop_vpn || true
        netsim_teardown
    fi
}

# ── main ───────────────────────────────────────────────────────────────────
echo "════════════════════════════════════════════════════════"
echo "  netsim scenarios — mode=${MODE}"
echo "════════════════════════════════════════════════════════"
ci_bench_check_deps
netsim_detect_caps

case "$MODE" in
  percommit)
    # One bad network, the highest-signal one: a scheduler regression shows up
    # in hetero_extreme before anywhere else.
    TEST_NAME="netsim_percommit"
    run_class hetero_extreme
    ;;
  classes)
    TEST_NAME="netsim_classes"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    for c in homo_good homo_bad hetero_extreme asym_capacity asym_latency one_flapping carrier_pair; do
        run_class "$c" || echo "  (class $c failed, continuing)"
    done
    ;;
  catalog)
    TEST_NAME="netsim_catalog_${CATALOG_TRANSIT}"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    run_catalog "$CATALOG_TRANSIT"
    ;;
  special)
    TEST_NAME="netsim_special"
    run_special
    ;;
  *) echo "unknown mode '$MODE'" >&2; exit 2 ;;
esac

OUT="${CI_BENCH_RESULTS}/${TEST_NAME}_$(date -u '+%Y%m%d_%H%M%S').json"
python3 -c "
import json, sys, os
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
doc = {
  'test': sys.argv[2],
  'commit': os.environ.get('CI_BENCH_COMMIT', 'unknown'),
  'timestamp': sys.argv[3],
  'mode': sys.argv[4],
  'iperf_sec': int(sys.argv[5]),
  'repeats': int(sys.argv[6]),
  'caps': {'netem_seed': int(sys.argv[7]), 'pps_police': int(sys.argv[8])},
  'results': rows,
}
json.dump(doc, open(sys.argv[9], 'w'), indent=2)
print('scenarios recorded: %d' % len(rows))" \
    "$ROWS" "$TEST_NAME" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$MODE" \
    "$IPERF_SEC" "$REPEATS" "$NETSIM_HAVE_SEED" "$NETSIM_HAVE_PPS" "$OUT"

echo ""
echo "Result: $OUT"
