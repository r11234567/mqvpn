#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
#
# ci_bench_scenarios.sh — realistic-network scenario benchmarks.
#
#   sudo ./ci_bench_scenarios.sh percommit          [mqvpn]
#   sudo ./ci_bench_scenarios.sh classes            [mqvpn]
#   sudo ./ci_bench_scenarios.sh combo              [mqvpn]
#   sudo ./ci_bench_scenarios.sh catalog <transit>  [mqvpn]
#   sudo ./ci_bench_scenarios.sh special            [mqvpn]
#   sudo ./ci_bench_scenarios.sh quic               [mqvpn]
#   sudo ./ci_bench_scenarios.sh game               [mqvpn]
#   sudo ./ci_bench_scenarios.sh vps                [mqvpn]
#
# The last three carry INNER traffic that is not TCP. Every other mode measures
# iperf3 TCP through the tunnel, which for a QUIC proxy leaves the protocol most
# of its traffic actually is unmeasured -- and leaves wlb and wlb_udp_pin
# indistinguishable, since they differ only on inner UDP.
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
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/ci_bench_host.sh"
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/ci_bench_sampler.sh"
# shellcheck source=/dev/null
source "${SCRIPT_DIR}/ci_bench_quic.sh"

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
IPERF_SEC="${CI_BENCH_IPERF_SEC:-10}"

# ── A/B arm ────────────────────────────────────────────────────────────────
#
# CI_BENCH_REORDER = on | off | "" (leave the built-in default alone).
#
# One workflow dispatch runs the same modes under several arms in parallel
# matrix jobs, so an A/B is one run rather than two. That matters beyond
# convenience: the netsim numbers move several percent between runs on shared
# runners, and two arms measured in the same run against the same code are
# comparable in a way that two dispatches are not.
#
# The arm is written into every row as `arm`, so the comparison is mechanical
# rather than a matter of remembering which run was which.
# Exported: the row builders below read them from the environment rather than
# taking two more positional arguments through an already long argv.
export CI_BENCH_ARM="${CI_BENCH_ARM:-default}"
export CI_BENCH_REORDER="${CI_BENCH_REORDER:-}"

if [ -n "$CI_BENCH_REORDER" ]; then
    case "$CI_BENCH_REORDER" in
      on|off) ;;
      *) echo "CI_BENCH_REORDER must be 'on' or 'off', got '$CI_BENCH_REORDER'" >&2
         exit 2 ;;
    esac
    # Handed to both ends via --config (ci_bench_env.sh). Written once, before
    # any server starts, and left in place for the whole mode.
    _CB_ARM_DIR="$(mktemp -d)"
    CI_BENCH_CONFIG_FILE="${_CB_ARM_DIR}/arm.conf"
    printf '[Reorder]\nEnabled = %s\n' "$CI_BENCH_REORDER" >"$CI_BENCH_CONFIG_FILE"
    export CI_BENCH_CONFIG_FILE
    echo "arm '${CI_BENCH_ARM}': [Reorder] Enabled = ${CI_BENCH_REORDER}"
fi

# Append an [Advanced] block to whatever config both ends are already getting,
# creating the file if the arm did not.
#
# Why a mode needs this: UdpGso defaults to true, and it does not merely batch
# syscalls -- it also sets xquic's defer_send_flush (one predicate,
# mqvpn_tx_batch_enabled(), drives both), so the sender holds packets back
# until a batch is worth sending. Run 34043133862 measured the consequence:
# gso_factor was 2.00 on every 2000 pps game row, meaning two datagrams left
# per syscall, and samp_tx_pps read ~958 against 2000 offered. A game protocol
# that infers loss from arrival gaps sees a coalesced batch as a stall, which
# is the failure this mode exists to measure -- so measuring it with batching
# on measures the wrong thing.
#
# Appended rather than written: the reorder arm above owns the same file, and
# clobbering it would silently disable the A/B.
ci_bench_config_append() {
    if [ -z "${CI_BENCH_CONFIG_FILE:-}" ]; then
        _CB_ARM_DIR="${_CB_ARM_DIR:-$(mktemp -d)}"
        CI_BENCH_CONFIG_FILE="${_CB_ARM_DIR}/arm.conf"
        : >"$CI_BENCH_CONFIG_FILE"
        export CI_BENCH_CONFIG_FILE
    fi
    printf '%s\n' "$@" >>"$CI_BENCH_CONFIG_FILE"
}

# Streams per sample. One stream cannot fill a high-BDP path: bgp_plain at
# 380mbit over ~160ms RTT needs 7.6 MB of in-flight window, which a single
# inner TCP connection does not reach inside a short sample. That is how the
# widest transit in the catalog came back at 20.6 Mbps while a 50mbit private
# line measured 37.6 -- the sample was the bottleneck, not the emulated link.
IPERF_STREAMS="${CI_BENCH_IPERF_STREAMS:-4}"

# How long to wait for the tunnel before recording a failure. The bad profiles
# are marginal rather than impossible (catalog bgp_junk+eth does reach 0.5
# Mbps), so a wait tuned for a clean path turns "slow to connect" into "never
# connected" and publishes it as a zero.
TUNNEL_WAIT_SEC="${CI_BENCH_TUNNEL_WAIT_SEC:-40}"
# Repeats per measurement. Two on the per-commit gate (it must not become the
# slowest thing in the push path), three where the number feeds a trend line.
REPEATS="${CI_BENCH_REPEATS:-2}"

CI_BENCH_RESULTS="${CI_BENCH_RESULTS:-${SCRIPT_DIR}/../../ci_bench_results}"
mkdir -p "$CI_BENCH_RESULTS"
ROWS="$(mktemp)"
# Fixed at startup so emit_results always rewrites the same file rather than
# leaving one document per scenario behind.
RESULTS_STAMP="$(date -u '+%Y%m%d_%H%M%S')"
trap 'emit_results; rm -f "$ROWS" "${_CB_CLIENT_PIDS:-}"; netsim_spike_stop; ci_bench_host_stop 2>/dev/null || true; ci_bench_stop_vpn 2>/dev/null || true; ci_bench_tier_cleanup 2>/dev/null || true; netsim_teardown' EXIT
# An untrapped SIGTERM kills the shell without running the EXIT trap, and a
# job-level `timeout-minutes` cancellation is delivered as one. Convert both to
# a normal exit so the partial results survive the way they do on any other
# failure.
trap 'exit 143' TERM
trap 'exit 130' INT

# ── helpers ────────────────────────────────────────────────────────────────

# Write every row collected so far as the results document.
#
# Called after each scenario, not only at the end. The 2026-08-26 weekly lost
# three entire jobs this way: the document used to be written once, after the
# mode's loop returned, so a job cancelled at the 60-minute cap uploaded
# nothing at all -- "No files were found with the provided path:
# ci_bench_results/*.json" -- and the scenarios that HAD completed went with
# it. Rewriting after every row costs one python invocation against a scenario
# that takes 40-100 s, and means the artifact is never further behind than the
# scenario currently running.
emit_results() {
    [ -n "${TEST_NAME:-}" ] || return 0
    [ -s "$ROWS" ] || return 0
    RESULTS_OUT="${CI_BENCH_RESULTS}/${TEST_NAME}_${RESULTS_STAMP}.json"
    python3 -c "
import json, sys, os
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
doc = {
  'test': sys.argv[2],
  'commit': os.environ.get('CI_BENCH_COMMIT', 'unknown'),
  'timestamp': sys.argv[3],
  'mode': sys.argv[4],
  # Self-describing arm, so a document read on its own says which side of an
  # A/B it is rather than relying on the artifact it arrived in.
  'arm': os.environ.get('CI_BENCH_ARM') or 'default',
  'arm_reorder': os.environ.get('CI_BENCH_REORDER') or 'default',
  'iperf_sec': int(sys.argv[5]),
  'iperf_streams': int(sys.argv[6]),
  'repeats': int(sys.argv[7]),
  'complete': int(sys.argv[8]),
  'caps': {'netem_seed': int(sys.argv[9]), 'pps_police': int(sys.argv[10]),
           'nat': int(sys.argv[11])},
  'results': rows,
}
json.dump(doc, open(sys.argv[12], 'w'), indent=2)" \
        "$ROWS" "$TEST_NAME" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$MODE" \
        "$IPERF_SEC" "$IPERF_STREAMS" "$REPEATS" "${RESULTS_COMPLETE:-0}" \
        "$NETSIM_HAVE_SEED" "$NETSIM_HAVE_PPS" "$NETSIM_HAVE_NAT" \
        "$RESULTS_OUT" ||
        echo "::error::emit_results failed to write ${RESULTS_OUT} -- a" \
             "malformed row used to discard the whole document silently"
}

# Echo the findings on the row just appended as GitHub annotations, so a defect
# the harness detected reaches the job summary instead of living only in an
# artifact nobody opens.
_cb_note_row_findings() {
    tail -n 1 "$ROWS" 2>/dev/null | python3 -c "
import json, sys
line = sys.stdin.read().strip()
if not line:
    raise SystemExit(0)
try:
    row = json.loads(line)
except Exception:
    raise SystemExit(0)
for msg in row.get('findings') or []:
    print('::warning title=netsim %s::%s' % (row.get('scenario', '?'), msg))
" || true
}

# Harness health, as a gate rather than a footnote.
#
# Every scenario loop swallows its own failure (`|| echo "(continuing)"`) and
# every mode ends by setting RESULTS_COMPLETE=1, so a mode in which most
# scenarios never established a tunnel still exited 0 with complete: 1. Run
# 33302660068 was exactly that: 6 of 13 `classes` rows, 4 of 10 `combo` rows and
# 2 of 4 `special` rows never brought a tunnel up -- the entire non-public NAT
# axis -- and the weekly was green on all eleven jobs. `complete` only ever
# meant "the loop reached the end", which is not how it reads.
#
# The split matters: a scenario that could not RUN is a harness defect and fails
# the job. A scenario that ran and produced a bad number is a finding about the
# code under test -- those are annotated and counted, never fatal, or the weekly
# would be red until xquic is fixed and would stop reporting anything.
CI_BENCH_MAX_FAIL_PCT="${CI_BENCH_MAX_FAIL_PCT:-25}"

_cb_summarise_and_gate() {
    [ -s "$ROWS" ] || { echo "::error::no rows recorded at all"; return 1; }
    python3 -c "
import json, sys, collections
rows = [json.loads(l) for l in open(sys.argv[1]) if l.strip()]
limit = float(sys.argv[2])

# Only a scenario that could not RUN counts against this gate. 'measured_zero'
# means the tunnel came up, iperf ran, and the answer was zero -- a result, and
# a bad one, but not a harness failure, so gating on it would make the harness
# fail whenever the code under test performs badly. That distinction is the
# whole point of the status field §0.3 B added.
COULD_NOT_RUN = {'tunnel_never_up', 'client_start_failed', 'setup_failed'}

# Three outcomes, not two. A run_pair row carries one status per measurement,
# so 'a=ok b=tunnel_never_up mp=ok' did produce two of its three numbers -- but
# its aggregation_efficiency and vs_best_single are null, which is what the
# multipath conclusions in the matrix are actually built on. Partial counts
# against the gate; the message says so rather than claiming nothing ran.
def outcome(r):
    parts = [p.split('=')[-1] for p in str(r.get('status', '?')).split()]
    blocked = [p for p in parts if p in COULD_NOT_RUN]
    if not blocked:
        return 'complete'
    return 'dead' if len(blocked) == len(parts) else 'partial'

kinds = collections.Counter(outcome(r) for r in rows)
bad = [r for r in rows if outcome(r) != 'complete']
reasons = collections.Counter()
for r in rows:
    for part in [p.split('=')[-1] for p in str(r.get('status', '?')).split()]:
        if part != 'ok':
            reasons[part] += 1

findings = collections.Counter()
for r in rows:
    for f in r.get('findings') or []:
        findings[f.split(':', 1)[0]] += 1

print('')
print('rows=%d  complete=%d  partial=%d  dead=%d'
      % (len(rows), kinds['complete'], kinds['partial'], kinds['dead']))
if reasons:
    print('failure reasons: ' + ', '.join('%s x%d' % kv for kv in reasons.most_common()))
if findings:
    print('findings: ' + ', '.join('%s x%d' % kv for kv in findings.most_common()))
    for k, n in findings.most_common():
        print('::warning title=netsim findings::%s raised on %d row(s)' % (k, n))

pct = 100.0 * len(bad) / len(rows)
if pct > limit:
    print('::error::%.0f%% of scenarios (%d/%d) did not produce a complete '
          'measurement (%d partial, %d with nothing at all), over the %.0f%% '
          'allowed -- read this as a harness failure, not as a result'
          % (pct, len(bad), len(rows), kinds['partial'], kinds['dead'], limit))
    raise SystemExit(1)
print('scenarios measured end to end: %.0f%%' % (100.0 - pct))
" "$ROWS" "$CI_BENCH_MAX_FAIL_PCT"
}

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

# measure_pathset "<--path args>"
#   -> MEASURED_MBPS / MEASURED_CV / MEASURED_STATUS
#
# Restarts the client so only the requested paths exist, then measures.
#
# NEVER call this through $(...) or < <(...). Both are subshells, and
# ci_bench_start_client records the client pid in a shell variable that the
# subshell takes with it when it exits -- so the next call starts a second
# client beside the first, three end up coexisting inside one netsim_setup, and
# the first one keeps the tunnel address and the TUN. Every solo_b and
# multipath figure this harness produced before that was found was really path
# 0 measured a second and third time, which is why aggregation_efficiency sat
# at exactly 0.50 across the whole matrix. Results come back in globals for
# that reason; keep it that way.
MEASURED_MBPS=0.0
MEASURED_CV=0.0
MEASURED_STATUS=ok

measure_pathset() {
    local paths="$1"
    MEASURED_MBPS=0.0; MEASURED_CV=0.0; MEASURED_STATUS=ok

    if ! ci_bench_start_client "$paths" >/dev/null 2>&1; then
        MEASURED_STATUS=client_start_failed
        return 0
    fi
    if ! ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
        MEASURED_STATUS=tunnel_never_up
        return 0
    fi

    # One server serves solo-A, solo-B and multipath in turn, and the scheduler
    # counters in its log are cumulative from process start. Mark the log here
    # so collect_wlb_instr reads only THIS measurement's window -- otherwise the
    # multipath row would carry the two solo phases' packets as well, each of
    # which used exactly one path and would fake a lopsided split.
    ci_bench_mark_server_log

    local samples=() i jf
    for (( i=0; i<REPEATS; i++ )); do
        jf="$(ci_bench_run_iperf TCP DL "$IPERF_SEC" "$IPERF_STREAMS")"
        samples+=("$(ci_bench_parse_throughput "$jf")")
        rm -f "$jf"
    done
    MEASURED_MBPS="$(med "${samples[@]}")"
    MEASURED_CV="$(cv_pct "${samples[@]}")"

    # A tunnel that came up and then carried nothing is a different finding
    # from one that never came up, and the row has to be able to say which --
    # both used to be written as a bare 0.0.
    awk -v v="$MEASURED_MBPS" 'BEGIN{exit !(v+0>0)}' || MEASURED_STATUS=measured_zero
    return 0
}

# Scrape the control API for everything that is not throughput. Echoes a JSON
# fragment (no braces) so the row builder can splice it in.
collect_stats() {
    local status stats
    status="$(netsim_query_control get_status)"
    stats="$(netsim_query_control get_stats)"
    python3 -c "
import json, os, sys

# xquic initialises ctl_minrtt to XQC_MAX_UINT32_VALUE and resets it to that on
# a route change (xqc_send_ctl.c:129, 215, 320, 1588). xqc_multipath.c:955
# copies it into path_min_rtt, mqvpn forwards it as min_rtt_us, and
# control_socket.c:283 divides by 1000 -- so a path that has never taken an RTT
# sample publishes min_rtt_ms = 4294967 as though it were a measurement, and
# the path stats carry no 'no sample yet' flag to read instead.
MIN_RTT_UNSET_MS = 4294967

def load(s):
    try: return json.loads(s)
    except Exception: return {}
st, gs = load(sys.argv[1]), load(sys.argv[2])
out = {}

# Three different nothings, which all used to emit exactly no keys: the control
# socket not answering, answering with no client, and a client with no paths.
# That is why special/ack_starvation could report status 'ok' with no RTT keys
# at all and nothing said which of the three had happened.
clients = st.get('clients')
out['stats_source'] = ('control_query_failed' if not st
                       else 'no_client' if not clients else 'ok')

cl = (clients or [{}])[0]
paths = cl.get('paths') or []
out['paths_seen'] = len(paths)

# A path is RTT-sampled only if its floor is a real measurement. Sub-millisecond
# floors truncate to 0 on the way through the control API, which is every path
# on the unshaped 'lan' profile -- six of the seven tier rows reported
# min_rtt_ms 0 and therefore rtt_inflation 0.
sampled = [p for p in paths if 0 < p.get('min_rtt_ms', 0) < MIN_RTT_UNSET_MS]
subms = [p for p in paths
         if p.get('min_rtt_ms', 0) == 0 and p.get('srtt_ms', 0) > 0]
out['paths_rtt_sampled'] = len(sampled)

if paths:
    out['pkt_lost'] = sum(p.get('pkt_lost', 0) for p in paths)

if sampled:
    out['srtt_ms'] = max(p['srtt_ms'] for p in sampled)
    # The connection's RTT floor is the MINIMUM across paths. Taking max() put
    # the unset sentinel here whenever any path was unsampled, so min_rtt_ms
    # came out as 4294967 and rtt_inflation -- a ratio that cannot fall below
    # 1 -- was published as 0.0 on every row with a dead leg.
    out['min_rtt_ms'] = min(p['min_rtt_ms'] for p in sampled)
    # Bufferbloat readout: per path, then the worst of them. srtt and min_rtt
    # taken from *different* paths do not form a ratio that means anything.
    out['rtt_inflation'] = round(
        max(p['srtt_ms'] / p['min_rtt_ms'] for p in sampled), 3)
elif subms:
    out['srtt_ms'] = max(p.get('srtt_ms', 0) for p in subms)
    out['min_rtt_ms'] = 0
    out['rtt_inflation'] = None
    out['rtt_note'] = 'min_rtt < 1ms: not representable at the API ms resolution'
elif paths:
    out['srtt_ms'] = None
    out['min_rtt_ms'] = None
    out['rtt_inflation'] = None
    out['rtt_note'] = 'no path has taken an RTT sample'

if paths:
    load_ = [p.get('bytes_tx', 0) + p.get('bytes_rx', 0) for p in paths]
    tot, hi = sum(load_), max(load_)
    # A share, so it is comparable against the 1/n a fair scheduler would hand
    # each path. The old value was min/max, which ranges up to 1.0 and is not a
    # share: sched/one_flapping/minrtt published path_minshare 0.833, which no
    # minimum share can be. Same name, same documented meaning, correct value.
    out['path_minshare'] = round(min(load_) / tot, 3) if tot else None
    out['path_share_fair'] = round(1.0 / len(load_), 3)
    out['path_load_ratio'] = round(min(load_) / hi, 3) if hi else None
for k in ('dgram_lost', 'dgram_sent', 'bytes_tx', 'bytes_rx'):
    if k in gs: out[k] = gs[k]
# Batching factors: the readout that makes carrier_qos actionable, since a
# packet-rate cap makes goodput scale with bytes-per-packet.
#
# These also VERIFY the [Advanced] setting rather than trusting it. A mode that
# writes UdpGso=false gets 1.0 here if it worked; anything above 1.0 means the
# config did not reach the process, which is a silent failure the startup
# marker cannot catch (it reports the kernel capability probe, not whether
# batching is happening).
if gs.get('udp_tx_sends'):
    out['gso_factor'] = round(gs['udp_tx_datagrams'] / gs['udp_tx_sends'], 2)
if gs.get('udp_rx_receives'):
    out['gro_factor'] = round(gs['udp_rx_datagrams'] / gs['udp_rx_receives'], 2)
want_off = os.environ.get('CI_BENCH_OFFLOAD') == 'off'
if want_off:
    out['offload_requested'] = 'UdpGso=false UdpGro=false'
    g = out.get('gso_factor')
    if g is not None and g > 1.05:
        out['offload_applied'] = 'no'
        out['offload_note'] = ('gso_factor %.2f with UdpGso=false requested -- '
                               'the config did not reach the process' % g)
    elif g is not None:
        out['offload_applied'] = 'yes'
print(','.join(json.dumps(k) + ':' + json.dumps(v) for k, v in out.items()))
" "$status" "$stats"
}

# Read the WLB scheduler's own counters out of the SERVER log. Echoes a JSON
# fragment (no braces), same contract as collect_stats; empty when
# CI_BENCH_WLB_INSTR is off, so the row is unchanged for ordinary runs.
#
# Server, not client: every measurement is iperf3 DL, so the bulk data is
# scheduled by the server's WLB instance and the client's schedules only the
# returning ACKs. Reading the client would report the ACK split as though it
# were the throughput split -- and path_minshare next to it already comes from
# the server's own path stats, so the two would have disagreed by construction.
#
# Windowed: one server serves solo-A, solo-B and multipath in turn, and
# measure_pathset marks the log before its own samples so the earlier phases
# cannot leak into this row. Within the window the values are read as-is rather
# than differenced: the scheduler is allocated per connection from conn_pool
# (xqc_conn.c:1258) and each measurement starts a fresh client, so its counters
# already begin at zero. Differencing would instead throw away the first
# reporting interval. A drop in the cumulative `sched` inside one window means a
# second connection appeared -- a mid-measurement reconnect -- which is reported
# rather than silently averaged.
#
# What this is for. The netsim matrix says WLB fails to aggregate two identical
# healthy paths and can land under the better single path, while MinRTT does
# aggregate. Static reading of xqc_scheduler_wlb.c predicts a specific cause,
# and these counters are what would confirm or refute it:
#
#   - LATE weight is derived from cwnd (wlb_compute_weight), and cwnd is a
#     function of the traffic the scheduler already sent that path. Whichever
#     path warms first wins the weight comparison, so it gets the next flow,
#     so it warms further. Expect a lopsided `pins`.
#   - Weights are recomputed only at a round boundary, and pinned traffic
#     returns from the flow-hit fast path before the round check ever runs.
#     Expect `rounds` to stall near-constant while `sched` keeps climbing.
#
# If instead pins are near-even and rounds keep advancing, the static reading is
# wrong and the cause is elsewhere -- which is equally worth knowing.
collect_wlb_instr() {
    [ "${CI_BENCH_WLB_INSTR:-0}" = "1" ] || return 0
    [ -n "${CI_BENCH_SERVER_LOG:-}" ] && [ -r "$CI_BENCH_SERVER_LOG" ] || {
        echo -n ',"wlb_instr":"no_log"'
        return 0
    }

    python3 -c "
import json, re, sys

pat = re.compile(r'\|wlb_instr\|path:(\d+)\|weight:(\d+)\|deficit:(-?\d+)'
                 r'\|pins:(\d+)\|sched:(\d+)\|rounds:(\d+)'
                 r'\|spread_clamped:(\d+)\|spread_max:(-?\d+)\|n_paths:(\d+)\|')

path_log, mark = sys.argv[1], int(sys.argv[2])

def emit(d):
    print(',' + ','.join(json.dumps(k) + ':' + json.dumps(v)
                         for k, v in d.items()))
    raise SystemExit(0)

W, D, P, S, R, SC, SMAX = 0, 1, 2, 3, 4, 5, 6

# Last sample per path inside the window, plus whether any counter went
# backwards (which only a second scheduler instance can cause).
last, samples, reconnect = {}, {}, False
try:
    with open(path_log, errors='replace') as fh:
        fh.seek(mark)
        for line in fh:
            m = pat.search(line)
            if not m:
                continue
            pid = int(m.group(1))
            vals = [int(g) for g in m.groups()[1:]]
            prev = last.get(pid)
            if prev is not None and vals[S] < prev[S]:
                reconnect = True
            last[pid] = vals
            samples[pid] = samples.get(pid, 0) + 1
except OSError:
    emit({'wlb_instr': 'unreadable'})

if not last:
    emit({'wlb_instr': 'no_lines'})

ids = sorted(last)
pins  = [last[i][P] for i in ids]
sched = [last[i][S] for i in ids]
rounds = max(last[i][R] for i in ids)

out = {}
if sum(sched) == 0:
    # Lines present but nothing scheduled: no split exists to report, and
    # emitting 0.0 shares here would read exactly like a collapse.
    out['wlb_instr'] = 'no_packets_scheduled'
    out['wlb_samples'] = {str(i): samples[i] for i in ids}
    emit(out)

out['wlb_instr'] = 'ok'
out['wlb_samples'] = {str(i): samples[i] for i in ids}
if reconnect:
    # The counters below are the surviving connection's only.
    out['wlb_note'] = 'connection restarted mid-measurement'
out['wlb_path_ids'] = ids
out['wlb_weights'] = [last[i][W] for i in ids]
out['wlb_deficits'] = [last[i][D] for i in ids]
out['wlb_pins'] = pins
out['wlb_sched'] = sched
out['wlb_rounds'] = rounds
out['wlb_spread_clamped'] = [last[i][SC] for i in ids]
out['wlb_spread_max'] = max(last[i][SMAX] for i in ids)

# The quantity WRR actually reads. Selection compares deficits against each
# other, so the GAP is the signal and the absolute values are not: under the
# old per-path floor both paths sat at -64 in 20 of 22 rows (34036912262) and
# this gap was 0, meaning a fourfold overspend and a slight one were
# indistinguishable to the scheduler. A gap pinned at wlb_spread_max means the
# allowance is binding and the imbalance is larger than WRR will chase.
defs = [last[i][D] for i in ids]
out['wlb_deficit_gap'] = max(defs) - min(defs) if len(defs) > 1 else 0
out['wlb_at_spread_limit'] = (
    1 if (len(defs) > 1 and out['wlb_deficit_gap'] >= out['wlb_spread_max'])
    else 0)

tp, ts = sum(pins), sum(sched)
# Shares, directly comparable against the 1/n a balanced scheduler would give
# -- same convention as path_minshare above.
out['wlb_pin_minshare'] = round(min(pins) / tp, 3) if tp else None
out['wlb_sched_minshare'] = round(min(sched) / ts, 3) if ts else None
# Packets scheduled per round turned over. Large means the round is not
# rolling, so the weights behind the split are stale.
out['wlb_pkts_per_round'] = round(ts / rounds, 1) if rounds else None
w = out['wlb_weights']
out['wlb_weight_ratio'] = (round(max(w) / min(w), 2)
                           if w and min(w) > 0 else None)
emit(out)
" "$CI_BENCH_SERVER_LOG" "${CI_BENCH_SERVER_LOG_MARK:-0}"
}

# Where the send side stopped, from xquic's |send_supply| line.
#
# The question this exists for: asym_capacity puts a 50 Mbit leg beside a 380
# Mbit one and the tunnel delivers 49.7 Mbps -- below the fast leg's own 234.6
# solo figure. Static review ruled out the shared send-side resources it could
# reach (connection flow control does not cover DATAGRAM, the per-tick send
# budget is per-path, the packet pool is 18000 deep), which leaves two
# possibilities that no artifact the harness collected could tell apart:
#
#   supply-limited -- the send side never offered enough to fill both legs.
#     Reads as `drain_ratio` near 1: nearly every scheduling pass emptied the
#     queue, so the paths were never the constraint. The next place to look is
#     then the TUN read loop and the datagram write, not the scheduler.
#
#   clamp-limited -- the congestion controllers had room and mqvpn's own
#     so_sndbuf ceiling (8 MiB, mqvpn_conn_settings.c:128) refused the packet
#     anyway. Reads as `stop_sndbuf_clamp` climbing. A configuration bug rather
#     than congestion, and the leading untested explanation for an aggregate
#     below one leg alone -- the fast leg's BDP is ~7.6 MB, so one path can
#     very nearly exhaust a ceiling that is shared with the other.
#
#   network-limited -- `stop_all_blocked` dominates and backlog is deep. Honest
#     congestion; the emulated capacity is what it is.
#
# This replaces a `stop_headroom_left` counter that could not fire. It asked
# whether any path had cwnd headroom using the same predicate the scheduler had
# just consulted, so the answer was structurally always no: 0 in all 46 rows of
# run 34026833126 and all 22 WLB rows of 34036912262. A column that is uniform
# across every row is presumed broken until shown otherwise, and this one was.
#
# Same window discipline as collect_wlb_instr: cumulative counters read from
# the server log after the mark, because the scheduler and the connection are
# both per-connection and the solo phases would otherwise be folded in.
collect_send_supply() {
    [ "${CI_BENCH_WLB_INSTR:-0}" = "1" ] || return 0
    [ -n "${CI_BENCH_SERVER_LOG:-}" ] && [ -r "$CI_BENCH_SERVER_LOG" ] || {
        echo -n ',"send_supply":"no_log"'
        return 0
    }

    python3 -c "
import json, re, sys

pat = re.compile(r'\|send_supply\|passes:(\d+)\|drained:(\d+)'
                 r'\|stop_all_blocked:(\d+)\|stop_sndbuf_clamp:(\d+)'
                 r'\|stop_backlog:(\d+)\|sndq_used:(\d+)\|paths:(\d+)\|')

path_log, mark = sys.argv[1], int(sys.argv[2])

def emit(d):
    print(',' + ','.join(json.dumps(k) + ':' + json.dumps(v)
                         for k, v in d.items()))
    raise SystemExit(0)

# Last line in the window wins: the counters are cumulative per connection.
last, n = None, 0
try:
    with open(path_log, errors='replace') as fh:
        fh.seek(mark)
        for line in fh:
            m = pat.search(line)
            if m:
                vals = [int(g) for g in m.groups()]
                # A counter going backwards means a second connection, whose
                # numbers would otherwise be added to the first's.
                if last is not None and vals[0] < last[0]:
                    n = 0
                last = vals
                n += 1
except OSError:
    emit({'send_supply': 'unreadable'})

if last is None:
    emit({'send_supply': 'no_lines'})

passes, drained, all_blk, clamp, backlog, sndq, paths = last
out = {'send_supply': 'ok', 'supply_samples': n}
out['supply_passes'] = passes
out['supply_drained'] = drained
out['supply_stop_all_blocked'] = all_blk
out['supply_stop_sndbuf_clamp'] = clamp
out['supply_sndq_used_last'] = sndq

if passes:
    # The headline. Near 1.0 means the paths were never the limit.
    out['supply_drain_ratio'] = round(drained / passes, 3)
    stops = all_blk + clamp
    out['supply_stop_ratio'] = round(stops / passes, 3)
    # Of the passes that DID stop, the fraction stopped by our own sndbuf
    # ceiling rather than by congestion. This is the number that accuses the
    # configuration.
    out['supply_clamp_share'] = (round(clamp / stops, 3) if stops
                                 else None)
    # Mean depth left behind per stop, so a rare deep stall is not read the
    # same as constant shallow ones. Capped at 512 per stop in xquic.
    out['supply_backlog_per_stop'] = (round(backlog / stops, 1) if stops
                                      else None)
    out['supply_verdict'] = (
        'supply_limited' if out['supply_drain_ratio'] >= 0.95 else
        'clamp_limited'  if (out['supply_clamp_share'] or 0) >= 0.5 else
        'network_limited')
emit(out)
" "$CI_BENCH_SERVER_LOG" "${CI_BENCH_SERVER_LOG_MARK:-0}"
}

# Oscillation shape of a per-second rate series, plus the header-overhead ratio.
#
# The question: does the rate hold steady, or does it swing? Nested congestion
# control -- an outer retransmit inflating the inner connection's RTT until the
# inner stack also backs off, then both ramping together -- produces a mean that
# looks ordinary and a shape that does not. Every row in this artifact was one
# scalar before now, so that shape had nowhere to appear.
#
# Peak-to-trough ratio rather than an absolute swing, because it is a
# WITHIN-row quantity. Run 34019491401 vs 34026833126 measured the same
# single-path control rows 13% apart at the median and 129% apart at worst, so
# any metric compared across runs is unreadable at this repeat count; a ratio
# computed inside one measurement is not.
#
# Two conditions, not one: a large ratio ALONE is also what a single stall
# looks like, so a period must be detectable in the autocorrelation before this
# is called oscillation. One dropout is not a sine wave.
#
# What this does NOT show: the inner connection's RTT. The series is the outer
# tunnel's wire rate; inner behaviour is INFERRED from outer retransmit landing
# beside a trough. The finding text says "inferred" for that reason.
collect_oscillation() {
    [ "${CI_BENCH_SAMPLE:-0}" = "1" ] || return 0
    [ -n "${SAMPLED_JSON:-}" ] || return 0

    python3 -c '
import json, sys

frag = sys.argv[1]
try:
    samp = json.loads("{" + frag.lstrip(",") + "}")
except Exception:
    print(",\"osc\":\"unparsed\"")
    raise SystemExit(0)

s = samp.get("samp_tx_mbps_series") or []
out = {}

# Drop the first tick: it covers the interval in which the transfer started, so
# it is a partial window and always reads low. Counting it would manufacture a
# trough at t=0 in every single row.
s = [float(x) for x in s[1:] if isinstance(x, (int, float))]

if len(s) < 6:
    # Too short for a period to mean anything. Named rather than silently
    # omitted, so a missing osc column is never mistaken for a flat series.
    out["osc"] = "too_short"
    out["osc_ticks"] = len(s)
    print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                         for k, v in out.items()))
    raise SystemExit(0)

n = len(s)
mean = sum(s) / n
out["osc"] = "ok"
out["osc_ticks"] = n
out["osc_mean_mbps"] = round(mean, 2)

if mean <= 0:
    out["osc"] = "no_throughput"
    print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                         for k, v in out.items()))
    raise SystemExit(0)

var = sum((x - mean) ** 2 for x in s) / n
sd = var ** 0.5
out["osc_cv_pct"] = round(100.0 * sd / mean, 1)

# Percentiles, not raw min/max: one scheduling hiccup on a shared runner
# should not define the amplitude of a claimed oscillation.
srt = sorted(s)
p10 = srt[max(0, int(0.10 * (n - 1)))]
p90 = srt[min(n - 1, int(0.90 * (n - 1)))]
out["osc_p10_mbps"] = round(p10, 2)
out["osc_p90_mbps"] = round(p90, 2)
out["osc_peak_trough_ratio"] = round(p90 / p10, 2) if p10 > 0 else None

# Autocorrelation against the WHOLE-SERIES mean with a fixed normaliser (the
# textbook estimator), rather than re-centering each shifted window on its own
# mean.
#
# Re-centering was the first attempt and it was wrong: a monotonic ramp then
# scored r = 1.0 at every lag, because each half of a straight line correlates
# perfectly with the other half once both are separately de-meaned. A ramp is a
# trend, not an oscillation, and it was duly reported as "oscillating" with a
# 9x peak-to-trough ratio. The fixed-mean estimator instead drives a trend
# toward negative correlation at long lags, which is what separates the two
# shapes.
#
# A period is only accepted at a LOCAL MAXIMUM of the correlogram. The first
# attempt took the global maximum over all lags, which on a period-6 sine
# returned 12: the second harmonic scores just as highly, and reporting twice
# the true period would make the number worse than useless for lining an
# oscillation up against an RTT.
den = sum((x - mean) ** 2 for x in s)
acf = {}
if den > 0:
    for lag in range(1, max(2, n // 2) + 1):
        num = sum((s[i] - mean) * (s[i + lag] - mean)
                  for i in range(n - lag))
        acf[lag] = num / den

best_lag, best_r = None, 0.0
for lag in sorted(acf):
    r = acf[lag]
    # Interior local maximum, so the fundamental is found before its
    # harmonics. Lag 1 is excluded: adjacent samples of anything smooth
    # correlate, which says nothing about periodicity.
    prev = acf.get(lag - 1)
    nxt = acf.get(lag + 1)
    if lag < 2 or prev is None or nxt is None:
        continue
    if r > prev and r >= nxt and r > best_r:
        best_r, best_lag = r, lag

out["osc_autocorr_r"] = round(best_r, 3)
out["osc_autocorr_period_s"] = best_lag
# The trend term, reported so a rising or falling transfer is legible as such
# rather than hidden inside the amplitude figures. Spearman-style sign
# agreement against time, which needs no numpy.
mid = n // 2
first_half = sum(s[:mid]) / mid
last_half = sum(s[n - mid:]) / mid
out["osc_trend_ratio"] = (round(last_half / first_half, 2)
                          if first_half > 0 else None)

# Both conditions. Thresholds are deliberately blunt -- a 2x swing between the
# 10th and 90th percentile is not subtle, and r >= 0.5 at some lag means the
# swing recurs. Anything milder is reported as numbers without a verdict.
ratio = out.get("osc_peak_trough_ratio")
out["osc_verdict"] = (
    "oscillating" if (ratio and ratio >= 2.0 and best_r >= 0.5) else
    "unstable"    if (ratio and ratio >= 2.0) else
    "steady")

print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                     for k, v in out.items()))
' "$SAMPLED_JSON" 2>/dev/null || printf '%s' ',"osc":"failed"'
}

# Per-packet wire cost of carrying the inner flow.
#
# The specific worry with inner QUIC is its pure-ACK datagrams -- a few tens of
# bytes of inner payload, each wrapped in an outer QUIC DATAGRAM plus UDP/IP.
#
# There is NO wire/app ratio here any more, because the control socket exposes
# no app-byte counter to divide by. The previous version thought it did and
# published a ratio that was arithmetically incapable of exceeding 1.0:
#
#   clients[].bytes_tx is xquic's total_app_bytes (xqc_multipath.c:983), which
#   despite the name is send+recv summed over paths, and both terms are
#   post-encryption wire bytes (po_enc_size, xqc_send_ctl.c:704). Per-path
#   bytes_tx/bytes_rx are the same two counters unsummed. So with S = sum of
#   path sends and R = sum of path receives, the old expression computed
#   (S+R)/(S+2R) -- the same numbers over themselves with the receive
#   direction double-counted, hence 0.995-0.999 in all 20 rows of run
#   34036912262 rather than the >1 any real encapsulation ratio must give.
#   The name total_app_bytes was read as its contract; that was the whole bug.
#
# The replacement for that ratio -- per-direction bytes per packet, computed
# here from the same four fields -- was wrong too, and in run 34043133862 it
# said so plainly: overhead_bytes_per_pkt_rx came back as 0.2 bytes per packet
# on the game rows. A packet cannot be under one byte.
#
# The cause is the same class of mistake one layer down. These four fields are
# not two matched pairs:
#
#   bytes_tx/bytes_rx  <- ctl_app_bytes_send/recv, which accumulate ONLY for
#                         STREAM|DATAGRAM frames. xqc_send_ctl.h:142 says it
#                         outright: "only accounts for stream and datagram
#                         packets".
#   pkt_sent/pkt_recv  <- ctl_send_count/ctl_recv_count, where ctl_recv_count
#                         increments once per datagram received
#                         (xqc_send_ctl.c:1153), pure ACKs included.
#
# So on an ACK-dominated reverse direction the numerator counts almost nothing
# and the denominator counts everything. The tx side looked plausible only by
# luck: a bulk sender puts a STREAM frame in nearly every packet, so its two
# counters happen to move together. Twice now a field name has been read as a
# contract without checking the increment site.
#
# What remains here are the four raw counters, which are each individually
# true, with their semantics stated. The per-packet cost moved to the sampler
# (samp_wire_bytes_per_pkt_tx/_rx), which differences the veth's own
# tx_bytes/tx_packets -- a pair the kernel maintains over the same frames, and
# which includes the outer UDP/IP headers that actually cost capacity.
#
# A true wire/app ratio still needs a tun-side payload counter that does not
# exist (mqvpn_server.c:2971 bumps dgram_sent but no byte total), so the gap
# is named rather than filled with whatever is nearby.
collect_overhead() {
    local status
    status="$(netsim_query_control get_status)"
    python3 -c '
import json, sys

def load(s):
    try:
        return json.loads(s)
    except Exception:
        return {}

st = load(sys.argv[1])
out = {}
clients = st.get("clients") or []
if not clients:
    print(",\"overhead\":\"no_client\"")
    raise SystemExit(0)

cl = clients[0]
paths = cl.get("paths") or []
tx = sum(p.get("bytes_tx") or 0 for p in paths)
rx = sum(p.get("bytes_rx") or 0 for p in paths)
pkt_tx = sum(p.get("pkt_sent") or 0 for p in paths)
pkt_rx = sum(p.get("pkt_recv") or 0 for p in paths)

out["overhead"] = "ok"
# Named for what they are, not for what they were assumed to be. These two are
# STREAM|DATAGRAM frame bytes only, post-encryption -- not every wire byte.
out["overhead_app_frame_tx_bytes"] = tx
out["overhead_app_frame_rx_bytes"] = rx
# These two count all packets on the path, ACKs included on the receive side.
out["overhead_wire_pkts_tx"] = pkt_tx
out["overhead_wire_pkts_rx"] = pkt_rx
out["overhead_counter_note"] = (
    "app_frame_* are STREAM|DATAGRAM bytes (xqc_send_ctl.h:142); wire_pkts_* "
    "count every packet. Do NOT divide one by the other -- they are not a "
    "matched pair. Per-packet cost is samp_wire_bytes_per_pkt_tx/_rx.")
out["overhead_app_bytes"] = "unavailable: no tun-side byte counter in get_status"

print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                     for k, v in out.items()))
' "$status" 2>/dev/null || printf '%s' ',"overhead":"failed"'
}

# What the box actually looked like, as observed rather than as intended.
#
# The target profile is a 1-vCPU VPS with one VirtIO RX queue, one TX queue,
# RPS disabled, and IRQ/NET_RX on CPU0. Three of those four need no emulation:
# a veth pair is created with exactly one rx and one tx queue, rps_cpus reads
# all-zero unless something writes it, and NET_RX lands on CPU0 already. So
# this records them instead of pretending to have arranged them -- a row that
# claims an emulation it did not perform is worse than one that admits the
# default happened to match.
#
# What CANNOT be reproduced from inside a guest, and is named on the row rather
# than quietly omitted: VirtIO interrupt coalescing, and hypervisor steal time.
# (/proc/stat does report a steal figure, but on a GitHub runner that is the
# RUNNER being descheduled by its own host, not the emulated tier.)
# Run INSIDE the server netns. /sys/class/net is namespace-scoped and the veth
# was moved into netsim-server (ci_bench_netsim.sh:613), so reading it from the
# root netns raised OSError on every row of run 34036912262 -- host_queues came
# back "unreadable" and the rps_cpus keys silently vanished, which meant the
# single-queue and RPS-disabled assertions this function exists to make had
# never once fired. Same `ip netns exec` shape sampler_start uses.
collect_host_profile() {
    local dev="$1" ns="${2:-$NETSIM_NS_SERVER}"
    ip netns exec "$ns" python3 -c "
import json, os, sys

dev = sys.argv[1]
base = '/sys/class/net/%s' % dev
out = {}

def read(path):
    try:
        with open(path) as fh:
            return fh.read().strip()
    except OSError:
        return None

try:
    qs = os.listdir(base + '/queues')
    out['host_rx_queues'] = sum(1 for q in qs if q.startswith('rx-'))
    out['host_tx_queues'] = sum(1 for q in qs if q.startswith('tx-'))
except OSError:
    out['host_queues'] = 'unreadable'

rps = read(base + '/queues/rx-0/rps_cpus')
if rps is not None:
    out['host_rps_cpus'] = rps
    # All-zero (allowing for the comma grouping) means RPS is off.
    out['host_rps_enabled'] = any(c not in '0,' for c in rps)

# The RUNNER's CPU count, not the tier's. This function runs outside the
# transient scope, and os.cpu_count() ignores cpusets even inside one, so it
# cannot report a quota ceiling however it is called -- naming it for what it
# measures beats publishing 4 in a column a reader would take for the tier.
# The tier's actual allowance is CI_BENCH_TIER_NCPU, recorded below.
out['host_runner_nproc'] = os.cpu_count()
tier = os.environ.get('CI_BENCH_TIER') or 'untiered'
out['host_tier'] = tier
if tier != 'untiered':
    out['host_tier_ncpu'] = os.environ.get('CI_BENCH_TIER_NCPU_VAL') or 'unknown'
    # A tier LABEL is not proof the scope was created. ci_bench_have_tiers
    # degrades to untiered with only a ::warning:: to say so, so the row
    # carries the assertion explicitly rather than implying it.
    out['host_tier_applied'] = os.environ.get('CI_BENCH_TIER_OK') or 'unknown'
# The competing load the box was already carrying. 'healthy' means the tier was
# handed entirely to mqvpn, which is what made the vps rows of run 34043133862
# indistinguishable from untiered ones -- so this belongs on the row rather
# than being inferred from the mode name.
state = os.environ.get('CI_BENCH_HOST_STATE') or 'healthy'
out['host_state'] = state
if state == 'vps_baseline':
    out['host_baseload_cpu_pct'] = os.environ.get(
        'CI_BENCH_BASELOAD_CPU_PCT') or '15'
    out['host_baseload_mem_mb'] = os.environ.get(
        'CI_BENCH_BASELOAD_MEM_MB') or '559'
    out['host_baseload_swap_mb'] = os.environ.get(
        'CI_BENCH_BASELOAD_SWAP_MB') or '378'
    out['host_baseload_note'] = ('competing load shares the tier cpuset, as '
                                 'existing services on a 1-vCPU box must; '
                                 'the memory holder is madvised cold so the '
                                 'kernel may swap it, which is what puts '
                                 'page-in latency on the forwarding path')
out['host_not_emulated'] = ('virtio interrupt coalescing; hypervisor steal '
                            'time (a guest cannot reproduce either, and the '
                            'steal figure in /proc/stat is the runner being '
                            'descheduled, not this tier); host_runner_nproc '
                            'is the runner CPU count, not the tier ceiling')
print(',' + ','.join(json.dumps(k) + ':' + json.dumps(v)
                     for k, v in out.items()))
" "$dev" 2>/dev/null || printf '%s' ',"host_profile":"failed"'
}

# The pid of the mqvpn server process itself.
#
# _CB_SERVER_PID is not always the server: under a tier, ci_bench_start_server
# splices `systemd-run --scope` ahead of the binary (ci_bench_env.sh:258), so
# the recorded pid belongs to systemd-run and its VmHWM is a few MB of systemd
# rather than the server's footprint. ci_bench_env.sh:427 documents the same
# mismatch for the kill path. Every tier row published before this read
# systemd-run's high-water mark and called it the server's, which is precisely
# the column the vps mode leans on.
#
# Resolve through the cgroup when tiered: the scope holds exactly one mqvpn.
# Echoes nothing when it cannot be resolved, so a caller can tell "no server"
# from "pid 0".
server_pid() {
    local pid="${_CB_SERVER_PID:-}"
    [ -n "$pid" ] || return 0

    if [ -n "${CI_BENCH_TIER:-}" ]; then
        local kid
        # pgrep is bounded to this scope's descendants, so a stray mqvpn from
        # another namespace cannot be picked up.
        kid="$(pgrep -P "$pid" -x mqvpn 2>/dev/null | head -1 || true)"
        # One more level: systemd-run -> ip netns exec -> mqvpn.
        if [ -z "$kid" ]; then
            local mid
            mid="$(pgrep -P "$pid" 2>/dev/null | head -1 || true)"
            [ -n "$mid" ] && kid="$(pgrep -P "$mid" -x mqvpn 2>/dev/null \
                                    | head -1 || true)"
        fi
        [ -n "$kid" ] && pid="$kid"
    fi
    echo "$pid"
}

# Peak RSS of the mqvpn server, in KB.
server_rss_kb() {
    local pid; pid="$(server_pid)"
    [ -n "$pid" ] || { echo 0; return; }
    awk '/VmHWM/{print $2}' "/proc/${pid}/status" 2>/dev/null || echo 0
}

# What the server process itself looked like: threads, memory, and how often
# the scheduler took the CPU away from it.
#
# Every field here is one a production incident turned up and this harness
# could not see. A 1 vCPU VPS carrying a real workload showed:
#
#   mqvpn threads = 1        -- a single-threaded forwarder on one core stops
#                               forwarding entirely whenever it is preempted,
#                               and the TUN ring fills during the gap.
#   VmRSS 524 kB / VmSwap 68 MB
#                            -- nearly the whole process paged out. Coming back
#                               from swap is milliseconds, which at 17k pps is
#                               thousands of queued packets.
#
# nonvoluntary_ctxt_switches is the direct measure of the first: it counts the
# times the kernel preempted the process rather than the process yielding. A
# forwarder that is being preempted thousands of times a second is the
# mechanism behind a TUN drop count, and no throughput number shows it.
collect_proc_state() {
    local pid; pid="$(server_pid)"
    if [ -z "$pid" ] || [ ! -r "/proc/${pid}/status" ]; then
        printf '%s' ',"proc_state":"no_server_pid"'
        return 0
    fi
    python3 -c '
import json, sys

pid = sys.argv[1]
out = {"proc_state": "ok"}
want = {
    "Threads": "proc_threads",
    "VmRSS": "proc_vmrss_kb",
    "VmHWM": "proc_vmhwm_kb",
    "VmSwap": "proc_vmswap_kb",
    "voluntary_ctxt_switches": "proc_ctxsw_voluntary",
    "nonvoluntary_ctxt_switches": "proc_ctxsw_nonvoluntary",
}
try:
    with open("/proc/%s/status" % pid) as fh:
        for line in fh:
            k, _, v = line.partition(":")
            if k in want:
                out[want[k]] = int(v.split()[0])
except Exception:
    print(",\"proc_state\":\"unreadable\"")
    raise SystemExit(0)

# Named so a reader does not have to know that a forwarder should be
# multi-threaded to see that this one is not.
if out.get("proc_threads") == 1:
    out["proc_thread_note"] = ("single-threaded: any preemption stops "
                               "forwarding until it is rescheduled")
sw = out.get("proc_vmswap_kb")
rss = out.get("proc_vmrss_kb")
if sw and rss is not None and sw > rss:
    out["proc_swap_note"] = ("more of the process is swapped out than "
                             "resident -- page-in latency is on the "
                             "forwarding path")

print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                     for k, v in out.items()))
' "$pid" 2>/dev/null || printf '%s' ',"proc_state":"failed"'
}

# Drops on the tunnel interface and on its qdisc, plus the qdisc's own
# backlog and flow-limit counters.
#
# This is the counter a production incident was actually diagnosed on, and the
# one this harness never collected: mqvpn0 TX dropped 15421/816149 = 1.9%,
# against a qdisc that had dropped nothing. That combination localises the loss
# precisely -- not the network, not the qdisc, but the TUN's own ring, which
# fills while a single-threaded forwarder is off-CPU.
#
# The device default matters here: src/platform/linux/tun.c creates the TUN
# with TUNSETIFF and never sets txqueuelen, so the ring is the kernel default
# of 500 slots. At the offered rates a game workload reaches, 500 slots is a
# few milliseconds of headroom.
#
# fq's flows_plimit is collected for the same reason: it caps a single flow at
# 100 packets by default, so a tunnel that bursts -- which is exactly what an
# encapsulated game tick does -- is dropped by its own qdisc while the link
# sits idle. Field data showed 1450 of these.
collect_iface_drops() {
    local dev="$1" ns="${2:-$NETSIM_NS_SERVER}"
    local stats qd
    stats="$(ip netns exec "$ns" cat \
        "/sys/class/net/${dev}/statistics/tx_dropped" \
        "/sys/class/net/${dev}/statistics/rx_dropped" \
        "/sys/class/net/${dev}/statistics/tx_packets" \
        "/sys/class/net/${dev}/statistics/rx_packets" 2>/dev/null \
        | tr '\n' ' ')"
    qd="$(ip netns exec "$ns" tc -s -j qdisc show dev "$dev" 2>/dev/null)"
    local qlen
    qlen="$(ip netns exec "$ns" cat "/sys/class/net/${dev}/tx_queue_len" \
            2>/dev/null)"

    python3 -c '
import json, sys

nums, qjson, qlen, dev = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
out = {}
parts = nums.split()
if len(parts) != 4:
    print(",\"iface_drops\":\"unreadable\"")
    raise SystemExit(0)
try:
    txd, rxd, txp, rxp = (int(x) for x in parts)
except ValueError:
    print(",\"iface_drops\":\"unreadable\"")
    raise SystemExit(0)

out["iface_drops"] = "ok"
out["iface_dev"] = dev
out["iface_tx_dropped"] = txd
out["iface_rx_dropped"] = rxd
# Shares, because an absolute drop count means nothing without the offered
# count beside it -- 15421 drops is 1.9% or 0.02% depending on the denominator.
if txp + txd > 0:
    out["iface_tx_drop_pct"] = round(100.0 * txd / (txp + txd), 3)
if rxp + rxd > 0:
    out["iface_rx_drop_pct"] = round(100.0 * rxd / (rxp + rxd), 3)
try:
    out["iface_txqueuelen"] = int(qlen)
except (TypeError, ValueError):
    pass

# qdisc drops, separately from the device ring. The pair is what localises a
# loss: ring drops with a clean qdisc means the reader was too slow, while
# qdisc drops mean the shaper refused it.
try:
    qs = json.loads(qjson) if qjson else []
except Exception:
    qs = []
if qs:
    q = qs[0]
    out["qdisc_kind"] = q.get("kind")
    for src, dst in (("drops", "qdisc_drops"), ("overlimits", "qdisc_overlimits"),
                     ("requeues", "qdisc_requeues"), ("backlog", "qdisc_backlog"),
                     ("qlen", "qdisc_qlen")):
        if q.get(src) is not None:
            out[dst] = q[src]
    # fq only. flows_plimit is the per-flow 100-packet cap: a tunnel is one
    # flow to fq, so an encapsulated burst hits it while the link is idle.
    for k in ("flows_plimit", "throttled", "pkts_too_long"):
        if q.get(k) is not None:
            out["qdisc_" + k] = q[k]

print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                     for k, v in out.items()))
' "$stats" "${qd:-}" "${qlen:-}" "$dev" 2>/dev/null \
        || printf '%s' ',"iface_drops":"failed"'
}

# The same counters for the tunnel interface, on both ends.
#
# This is the one that matters most and the one the harness never had. The veth
# is the emulated network; mqvpn0 is where mqvpn itself hands packets to the
# kernel, and it is where a forwarder that fell behind loses them. On a real
# 1 vCPU box the split was unambiguous -- mqvpn0 TX dropped 1.9%, its qdisc
# dropped nothing -- and neither number was collectable here.
#
# Both directions are read because they fail for different reasons: the
# server's TUN TX is the downlink a game broadcasts, the client's TUN TX is
# the uplink. Prefixed rather than merged so a row can say which end.
collect_tun_drops() {
    local dev="${CI_BENCH_TUN_NAME:-mqvpn0}"
    local out="" side ns
    for side in srv cli; do
        if [ "$side" = srv ]; then ns="$NETSIM_NS_SERVER"; else ns="$NS_CLIENT"; fi
        # Absent until the tunnel is up, and absent is not an error: the
        # baseline leg runs before any TUN exists.
        ip netns exec "$ns" test -d "/sys/class/net/${dev}" 2>/dev/null \
            || continue
        local frag
        frag="$(collect_iface_drops "$dev" "$ns")"
        # Re-key so the two ends do not collide, and so a reader never has to
        # guess which interface a drop count belongs to. Both prefixes are
        # rewritten: leaving qdisc_* alone would let the client's qdisc
        # silently overwrite the server's in the merged row.
        frag="$(printf '%s' "$frag" | sed \
            -e "s/\"iface_/\"tun_${side}_/g" \
            -e "s/\"qdisc_/\"tun_${side}_qdisc_/g")"
        out="${out}${frag}"
    done
    [ -n "$out" ] || out=',"tun_drops":"no_tun_device"'
    printf '%s' "$out"
}

start_server_with_ctrl() {
    ci_bench_start_server "${1:-$CI_BENCH_SCHEDULER}" "--control-port ${CTRL_PORT}"
}

# ── scenario: one heterogeneity class from the curated table ──────────────
run_class() {
    local class="$1" sched="${2:-$CI_BENCH_SCHEDULER}"
    local spec="${NETSIM_CLASS[$class]:-}"
    [ -n "$spec" ] || { echo "unknown class $class" >&2; return 1; }
    run_pair "$class" "${spec%%|*}" "${spec##*|}" "$sched"
}

# ── scenario: any two path specs (solo A, solo B, both) ───────────────────
#
# run_pair <label> <specA> <specB> [scheduler]
#
# A spec is "<access>:<transit>[:<nat>[:<mtu>]]", so the two legs are freely
# composed and need no entry in any table — which is what lets run_combo
# generate a covering set instead of hand-authoring one.
#
# Measuring each leg alone and then together, back to back in the same job, is
# what makes the ratios meaningful: they divide out whatever share of the
# shared runner we happened to get. Comparing a multipath number here against a
# solo number from a different job would not.
run_pair() {
    local class="$1" a_spec="$2" b_spec="$3" sched="${4:-$CI_BENCH_SCHEDULER}"
    local spec="${a_spec}|${b_spec}"

    echo ""
    echo "── ${class} (scheduler=${sched}) ──"
    # Defensive: an earlier scenario that returned early may have left a
    # server running. Deleting a namespace does not kill what runs inside it,
    # so without this the orphans accumulate across the loop.
    ci_bench_stop_vpn 2>/dev/null || true
    netsim_setup 2 >/dev/null || return 1
    netsim_apply_path 0 "$a_spec" 4242 || return 1
    netsim_apply_path 1 "$b_spec" 4252 || return 1

    # A flapping path needs its storm running for the whole measurement,
    # otherwise the scenario degenerates into its stable base profile.
    # netsim_spike_start reports through NETSIM_SPIKE_PID rather than stdout:
    # reading it back with $(...) blocked forever, because the backgrounded
    # loop holds the substitution's pipe open for as long as it runs.
    netsim_spike_stop
    local t0 t1
    t0="$(netsim_path_field "$a_spec" transit)"
    t1="$(netsim_path_field "$b_spec" transit)"
    if [ -n "${NETSIM_SPIKE[$t1]:-}" ]; then
        netsim_spike_start 1 "$t1" 8 20 35
    elif [ -n "${NETSIM_SPIKE[$t0]:-}" ]; then
        netsim_spike_start 0 "$t0" 8 20 35
    fi

    start_server_with_ctrl "$sched" >/dev/null || return 1

    local dev0 dev1 a b mp cv st_a st_b st_mp status
    dev0="$(netsim_veth_cli 0)"; dev1="$(netsim_veth_cli 1)"
    # Plain calls, not $( ) -- see measure_pathset.
    measure_pathset "--path $dev0"
    a="$MEASURED_MBPS"; st_a="$MEASURED_STATUS"
    measure_pathset "--path $dev1"
    b="$MEASURED_MBPS"; st_b="$MEASURED_STATUS"
    measure_pathset "--path $dev0 --path $dev1"
    mp="$MEASURED_MBPS"; cv="$MEASURED_CV"; st_mp="$MEASURED_STATUS"

    status=ok
    [ "${st_a}${st_b}${st_mp}" = okokok ] ||
        status="a=${st_a} b=${st_b} mp=${st_mp}"

    local stats rss
    stats="$(collect_stats)"; rss="$(server_rss_kb)"
    # Must run before ci_bench_stop_vpn: the server log lives under the work
    # dir the teardown removes. The multipath pathset was measured last, so the
    # window mark points at that run -- the only one whose split matters.
    stats="${stats}$(collect_wlb_instr)$(collect_send_supply)"
    netsim_spike_stop
    ci_bench_stop_vpn

    # Ratios are the gate-able numbers: they divide out the runner's speed,
    # which absolute Mbps on a shared vCPU cannot.
    python3 -c "
import json,sys
a,b,mp = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3])
st_a, st_b, st_mp = sys.argv[12], sys.argv[13], sys.argv[14]
mcls_a, mcls_b = sys.argv[15], sys.argv[16]
import os
row = {
  'scenario': sys.argv[4], 'scheduler': sys.argv[5],
  'path_a': sys.argv[6], 'path_b': sys.argv[7],
  # Which A/B arm produced this row. Stamped on every row so a comparison is a
  # group-by rather than a matter of recalling which dispatch was which.
  'arm': os.environ.get('CI_BENCH_ARM') or 'default',
  'arm_reorder': os.environ.get('CI_BENCH_REORDER') or 'default',
  'solo_a_mbps': a, 'solo_b_mbps': b, 'multipath_mbps': mp,
  'multipath_cv_pct': float(sys.argv[8]),
  'server_rss_peak_kb': int(sys.argv[9]),
  'status': sys.argv[11],
  'status_a': st_a, 'status_b': st_b, 'status_mp': st_mp,
  'mtu_class_a': mcls_a, 'mtu_class_b': mcls_b,
}

# Both ratios are only defined when all three measurements happened. They used
# to be computed regardless, so hetero_extreme published
# aggregation_efficiency 1.009 and vs_best_single 1.009 with path B at
# tunnel_never_up -- mp/(a+0) on a single-path run, reading as near-perfect
# aggregation. These two are the gate-able numbers in the weekly, so a
# plausible-looking value from a half-dead scenario is worse than no value.
if st_a == st_b == st_mp == 'ok':
    row['aggregation_efficiency'] = round(mp/(a+b), 3) if a+b else None
    row['vs_best_single'] = round(mp/max(a,b), 3) if max(a,b) else None
else:
    row['aggregation_efficiency'] = None
    row['vs_best_single'] = None
    row['ratio_note'] = 'not computed: one or more measurements did not complete'

extra = sys.argv[10]
if extra: row.update(json.loads('{' + extra + '}'))

# Name what the numbers show, rather than leaving it to whoever reads the
# artifact. Each finding is a claim about the code under test, not about the
# harness, and each is only raised where the measurement supporting it is valid.
f = []
agg, vsb = row.get('aggregation_efficiency'), row.get('vs_best_single')
share, fair = row.get('path_minshare'), row.get('path_share_fair')
if vsb is not None and vsb < 0.98:
    f.append('mp_regression: multipath %.3fx the best single path -- adding a '
             'healthy second path COST throughput' % vsb)
if agg is not None and agg < 0.60:
    f.append('agg_deficit: aggregation_efficiency %.3f -- two paths delivered '
             'under 60%% of their combined solo throughput' % agg)
if share is not None and fair and share < fair * 0.5:
    f.append('share_imbalance: minority path carried %.1f%% of bytes against a '
             '%.1f%% fair share' % (share*100, fair*100))
# A leg below the outer datagram size used to be a standing defect: xquic could
# only ever raise a path's packet size, so such a leg was sent packets it could
# not forward for the life of the connection. xquic 6ba4261 makes the PMTU
# search per path and lets the connection size come down, so this is now a
# regression guard rather than a restatement of the bug -- the scenario is
# expected to aggregate, and only a failure to is worth reporting.
if 'below' in (mcls_a, mcls_b):
    if vsb is not None and vsb < 0.90:
        f.append('pmtu_blackhole_regression: a leg MTU is under the outer '
                 'datagram size and multipath came out %.3fx the best single '
                 'path -- the per-path PMTU search (xquic 6ba4261) should have '
                 'lowered the connection to fit that leg' % vsb)
    elif vsb is None:
        f.append('pmtu_below_unmeasured: a leg MTU is under the outer datagram '
                 'size, but one measurement did not complete, so whether the '
                 'PMTU search handled it is unknown on this row')

# WLB scheduler counters, present only under CI_BENCH_WLB_INSTR=1. These test
# the static reading of xqc_scheduler_wlb.c against a real run; see
# collect_wlb_instr for what each one would mean.
if row.get('wlb_instr') == 'ok':
    pin_share = row.get('wlb_pin_minshare')
    sch_share = row.get('wlb_sched_minshare')
    ppr = row.get('wlb_pkts_per_round')
    wr = row.get('wlb_weight_ratio')
    npaths = len(row.get('wlb_path_ids') or [])
    fair = (1.0 / npaths) if npaths else None
    if pin_share is not None and fair and pin_share < fair * 0.5:
        f.append('wlb_pin_collapse: the minority path took %.1f%% of flow pins '
                 'against a %.1f%% fair share -- pin assignment is following a '
                 'weight that the scheduler\\'s own traffic created'
                 % (pin_share * 100, fair * 100))
    if sch_share is not None and fair and sch_share < fair * 0.5:
        f.append('wlb_sched_collapse: the minority path was chosen for %.1f%% '
                 'of packets against a %.1f%% fair share' % (sch_share * 100,
                                                             fair * 100))
    if ppr is not None and ppr > 100:
        f.append('wlb_round_stall: %.1f packets scheduled per WRR round -- '
                 'pinned traffic returns from the flow-hit fast path without '
                 'consuming deficit, so weights are not being recomputed'
                 % ppr)
    if wr is not None and wr > 4 and 'homo' in row.get('scenario', ''):
        f.append('wlb_weight_skew: LATE weights differ %.2fx between paths the '
                 'scenario made identical -- the weight is tracking cwnd, not '
                 'capacity' % wr)

row['findings'] = f
row['finding_count'] = len(f)
print(json.dumps(row))" \
        "$a" "$b" "$mp" "$class" "$sched" \
        "${spec%%|*}" "${spec##*|}" "$cv" "$rss" "$stats" "$status" \
        "$st_a" "$st_b" "$st_mp" \
        "$(netsim_mtu_class "$(netsim_path_field "$a_spec" mtu)")" \
        "$(netsim_mtu_class "$(netsim_path_field "$b_spec" mtu)")" >> "$ROWS"

    echo "   solo_a=${a} solo_b=${b} mp=${mp} Mbps  (cv ${cv}%)  [${status}]"
    _cb_note_row_findings
    emit_results
    netsim_teardown
}

# ── scenario: server tier and host state ──────────────────────────────────
#
# run_tier <tier> <host_state> [class]
#
# The network is held at a class fast enough that it is not the bottleneck, so
# what the number moves with is the server's own box. Read these as ceilings of
# a smaller instance, not as that instance's latency: CPUQuota throttles time
# slices, and no cgroup makes a fast core into a slow one. `noisy_neighbour`
# and `softirq_storm` are competing-load proxies for a busy hypervisor, which
# cannot be emulated from inside the guest at all.
run_tier() {
    local tier="$1" state="$2" class="${3:-tier_ref}"
    local spec="${NETSIM_CLASS[$class]:-}"
    [ -n "$spec" ] || { echo "unknown class $class" >&2; return 1; }

    echo ""
    echo "── tier ${tier} / host ${state} (net=${class}) ──"
    ci_bench_stop_vpn 2>/dev/null || true
    netsim_setup 2 >/dev/null || return 1
    netsim_apply_path 0 "${spec%%|*}" 4242 || return 1
    netsim_apply_path 1 "${spec##*|}" 4252 || return 1

    # Exported so ci_bench_start_server picks the tier up without every caller
    # having to thread it through.
    CI_BENCH_TIER="$tier"
    ci_bench_host_start "$state" "$tier"

    if ! start_server_with_ctrl "$CI_BENCH_SCHEDULER" >/dev/null; then
        ci_bench_host_stop
        CI_BENCH_TIER=""
        return 1
    fi

    # A provider throttle is only interesting if it lands on a transfer that is
    # already running, so it is scheduled rather than pre-applied.
    local cap_pid=""
    if [ "$state" = cpu_capped ]; then
        ( sleep $(( IPERF_SEC / 2 + 1 )); ci_bench_tier_throttle ) &
        cap_pid=$!
    fi

    local dev0 dev1 mp cv
    dev0="$(netsim_veth_cli 0)"; dev1="$(netsim_veth_cli 1)"
    measure_pathset "--path $dev0 --path $dev1"
    mp="$MEASURED_MBPS"; cv="$MEASURED_CV"

    local stats rss
    stats="$(collect_stats)"; rss="$(server_rss_kb)"
    # tier_ref is two identical unshaped legs, so this is the row where an
    # uneven split has no network explanation at all -- the cleanest place to
    # read the scheduler's own counters. Before ci_bench_stop_vpn: the log lives
    # under the work dir teardown removes.
    stats="${stats}$(collect_wlb_instr)$(collect_send_supply)"

    if [ -n "$cap_pid" ]; then
        kill "$cap_pid" 2>/dev/null || true
        wait "$cap_pid" 2>/dev/null || true
    fi
    ci_bench_host_stop
    ci_bench_stop_vpn
    CI_BENCH_TIER=""

    python3 -c "
import json,sys,os
row = {
  'scenario': sys.argv[1], 'tier': sys.argv[2], 'host_state': sys.argv[3],
  'net_class': sys.argv[4], 'scheduler': sys.argv[5],
  'arm': os.environ.get('CI_BENCH_ARM') or 'default',
  'arm_reorder': os.environ.get('CI_BENCH_REORDER') or 'default',
  'multipath_mbps': float(sys.argv[6]),
  'multipath_cv_pct': float(sys.argv[7]),
  'server_rss_peak_kb': int(sys.argv[8]),
  'tier_props': sys.argv[9],
  'tier_nominal': 'label only -- a quota ceiling, not this instance\'s latency',
  'status': sys.argv[11],
}
extra = sys.argv[10]
if extra: row.update(json.loads('{' + extra + '}'))

# tier_ref is two identical unshaped 'lan' legs, which makes this the cleanest
# place in the matrix to read scheduler fairness: with nothing to tell the paths
# apart, a fair scheduler splits the bytes evenly. Every tier row in run
# 33302660068 came back between 0.126 and 0.338 on the old min/max figure, so
# the imbalance is not a property of any emulated network.
f = []
share, fair = row.get('path_minshare'), row.get('path_share_fair')
if row.get('status') == 'ok' and share is not None and fair and share < fair * 0.5:
    f.append('share_imbalance: minority path carried %.1f%% of bytes against a '
             '%.1f%% fair share, on two identical unshaped paths'
             % (share*100, fair*100))

# Same scheduler counters as run_pair reads, and they mean more here: with the
# two legs identical, any imbalance in pins or any stall in the round counter
# is the scheduler's own doing and nothing the network can account for.
if row.get('wlb_instr') == 'ok':
    ps, ss = row.get('wlb_pin_minshare'), row.get('wlb_sched_minshare')
    ppr, wr = row.get('wlb_pkts_per_round'), row.get('wlb_weight_ratio')
    n = len(row.get('wlb_path_ids') or [])
    wfair = (1.0 / n) if n else None
    if ps is not None and wfair and ps < wfair * 0.5:
        f.append('wlb_pin_collapse: minority path took %.1f%% of flow pins '
                 'against a %.1f%% fair share, on identical paths'
                 % (ps * 100, wfair * 100))
    if ss is not None and wfair and ss < wfair * 0.5:
        f.append('wlb_sched_collapse: minority path was chosen for %.1f%% of '
                 'packets against a %.1f%% fair share, on identical paths'
                 % (ss * 100, wfair * 100))
    if ppr is not None and ppr > 100:
        f.append('wlb_round_stall: %.1f packets per WRR round -- pinned traffic '
                 'returns from the flow-hit fast path without consuming '
                 'deficit, so weights are never recomputed' % ppr)
    if wr is not None and wr > 4:
        f.append('wlb_weight_skew: LATE weights differ %.2fx between two '
                 'identical unshaped paths -- the weight is tracking cwnd, not '
                 'capacity' % wr)

row['findings'] = f
row['finding_count'] = len(f)
print(json.dumps(row))" \
        "${tier}+${state}" "$tier" "$state" "$class" "$CI_BENCH_SCHEDULER" \
        "$mp" "$cv" "$rss" "${CI_BENCH_TIER_PROPS[$tier]:-none}" "$stats" \
        "$MEASURED_STATUS" >> "$ROWS"

    echo "   mp=${mp} Mbps  (cv ${cv}%)  rss=${rss}kB  [${MEASURED_STATUS}]"
    _cb_note_row_findings
    emit_results
    netsim_teardown
}

# ── scenario: a generated covering set of multipath combinations ──────────
#
# The four axes are independent (any access leg composes with any transit, NAT
# and MTU), so the full product is 6 x 10 x 5 x 3 = 900 single paths and
# ~810,000 pairs. Enumerating it is not an option, and sampling it randomly
# would give an unrepeatable answer.
#
# Instead: a covering set. Rotate through each axis so that EVERY level of
# every axis appears at least once on the good side and at least once on the
# bad side, in ~10 pairs rather than 810,000. That catches "this transit is
# broken", "this NAT is broken", "this MTU is broken" — the single-factor
# faults, which is what a matrix this shape is actually good for. Specific
# multi-factor interactions worth naming live in NETSIM_CLASS instead, where
# they are curated rather than generated.
run_combo() {
    # Ordered worst-to-best-ish so a pair is always a real disagreement.
    local -a good_transit=(bgp_opt bgp_plain iplc bgp_opt bgp_plain iplc bgp_opt bgp_plain iplc bgp_opt)
    local -a good_access=(eth wifi_good 5g_full eth wifi_good 5g_full eth wifi_good 5g_full eth)
    local -a good_nat=(public public port_restricted public full_cone public port_restricted public public full_cone)
    local -a bad_transit=(bgp_junk carrier_qos bgp_flappy bgp_plain_peak bgp_junk carrier_qos bgp_flappy bgp_plain_peak bgp_junk carrier_qos)
    local -a bad_access=(5g_edge 5g_throttled wifi_busy tether_otg geo_sat starlink 5g_half 5g_edge 5g_throttled wifi_busy)
    local -a bad_nat=(symmetric cgnat port_restricted cgnat symmetric full_cone cgnat symmetric cgnat port_restricted)
    local -a bad_mtu=(1400 1400 1500 1400 1500 1500 1280 1400 1400 1500)

    local i n=${#good_transit[@]}
    for (( i=0; i<n; i++ )); do
        local a="${good_access[$i]}:${good_transit[$i]}:${good_nat[$i]}"
        local b="${bad_access[$i]}:${bad_transit[$i]}:${bad_nat[$i]}:${bad_mtu[$i]}"
        run_pair "combo${i}" "$a" "$b" || echo "  (combo$i failed, continuing)"
    done
}

# ── scenario: one transit profile across every access leg, single path ────
run_catalog() {
    local transit="$1" leg
    for leg in eth wifi_good wifi_busy 5g_full 5g_half 5g_edge 5g_throttled starlink geo_sat tether_otg; do
        echo ""
        echo "── catalog ${transit} + ${leg} ──"
        ci_bench_stop_vpn 2>/dev/null || true   # see run_class
        netsim_setup 1 >/dev/null || continue
        netsim_apply_path 0 "${leg}:${transit}" 4242 || { netsim_teardown; continue; }
        start_server_with_ctrl >/dev/null || { netsim_teardown; continue; }

        local mbps cv stats rss st
        measure_pathset "--path $(netsim_veth_cli 0)"
        mbps="$MEASURED_MBPS"; cv="$MEASURED_CV"; st="$MEASURED_STATUS"
        stats="$(collect_stats)"; rss="$(server_rss_kb)"
        ci_bench_stop_vpn

        local ceil; ceil="$(netsim_path_ceilings "${leg}:${transit}")"
        python3 -c "
import json,sys
row={'scenario':sys.argv[1],'access':sys.argv[2],'transit':sys.argv[3],
     'single_path_mbps':float(sys.argv[4]),'cv_pct':float(sys.argv[5]),
     'server_rss_peak_kb':int(sys.argv[6]),'status':sys.argv[8]}
extra=sys.argv[7]
if extra: row.update(json.loads('{'+extra+'}'))

# Which configured ceiling the reading is actually near. One path, so this is
# unambiguous here in a way it is not for a pair. Without it, a 0.5 Mbps row
# reads as a property of the emulated path when what it really says is that
# congestion control bound the transfer far below anything the profile
# configured -- the state every carrier_qos and 5g_throttled row was in.
mbps = row['single_path_mbps']
rate, pps = float(sys.argv[9]), float(sys.argv[10])
ceilings = {k: v for k, v in (('rate', rate), ('pps', pps)) if v > 0}
row['rate_ceiling_mbps'] = rate or None
row['pps_ceiling_mbps'] = pps or None
if ceilings and mbps > 0:
    name = min(ceilings, key=ceilings.get)
    lowest = ceilings[name]
    row['ceiling_utilisation'] = round(mbps / lowest, 3)
    row['binding_constraint'] = name if mbps >= 0.7 * lowest else 'loss_or_rtt'
elif mbps <= 0:
    row['binding_constraint'] = 'no_measurement'
print(json.dumps(row))" \
            "${transit}+${leg}" "$leg" "$transit" "$mbps" "$cv" "$rss" "$stats" \
            "$st" ${ceil} >> "$ROWS"

        echo "   ${mbps} Mbps (cv ${cv}%)  [${st}]"
        emit_results
        netsim_teardown
    done
}

# ── scenario: inner QUIC over the tunnel ──────────────────────────────────
#
# Everything else in this harness measures inner TCP. mqvpn is a QUIC proxy, so
# the protocol most of its traffic actually is has never been measured under
# load -- which means a TCP-specific pathology and a general tunnel one are
# indistinguishable in every artifact published so far. Run 34026833126 made
# that concrete: nat_split put two identical 88 Mbps legs together and got 56,
# with the send side draining on 100% of passes, so nothing downstream of the
# scheduler was the constraint. Inner TCP collapsing under cross-path reorder
# is the leading explanation and cannot be confirmed while only TCP is measured.
#
# The second reason: wlb vs wlb_udp_pin is defined entirely on inner UDP
# (flow_sched.c:61 pins UDP only when udp_pin is set; TCP is pinned either
# way). For inner TCP the two schedulers are byte-for-byte identical, so the
# README trade-off between them has never had a measurement behind it. These
# rows are the first that can separate them.
#
# On the oscillation column: what is sampled is the OUTER tunnel wire rate. The
# inner connection RTT is not observed here and is INFERRED. See
# collect_oscillation and ci_bench_quic.sh.
run_quic() {
    local class="$1" sched="${2:-$CI_BENCH_SCHEDULER}"
    local spec="${NETSIM_CLASS[$class]:-}"
    [ -n "$spec" ] || { echo "unknown class $class" >&2; return 1; }

    echo ""
    echo "── quic ${class} (scheduler=${sched}) ──"
    ci_bench_stop_vpn 2>/dev/null || true
    netsim_setup 2 >/dev/null || return 1
    netsim_apply_path 0 "${spec%%|*}" 4242 || { netsim_teardown; return 1; }
    netsim_apply_path 1 "${spec##*|}" 4252 || { netsim_teardown; return 1; }

    if ! start_server_with_ctrl "$sched" >/dev/null; then
        skip_row "quic_${class}" setup_failed
        netsim_teardown
        return 1
    fi

    local dev0 dev1 st=ok
    dev0="$(netsim_veth_cli 0)"; dev1="$(netsim_veth_cli 1)"

    # Multipath only. The solo legs are what run_pair already measures for TCP,
    # and a QUIC row costs a 20 MiB transfer -- tripling that to restate a
    # comparison the TCP rows already carry would buy nothing.
    if ! ci_bench_start_client "--path $dev0 --path $dev1" >/dev/null 2>&1; then
        st=client_start_failed
    elif ! ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
        st=tunnel_never_up
    fi

    local stats="" rss=0 gp="NA" qst="not_run" gp_cv="" gp_n=0 gp_all=""
    if [ "$st" = ok ]; then
        ci_bench_mark_server_log

        # Repeat the transfer. One measurement per cell was the shape of the
        # first run and it is not enough to carry the finding this mode exists
        # for: wlb vs wlb_udp_pin came out 4x-12x apart on all four scenarios,
        # which is far outside the 13% cross-run noise floor, but a single
        # observation of a 12x gap is still a single observation. REPEATS is
        # the harness-wide knob (2 by default) and it never reached here --
        # ci_bench_quic_transfer was called exactly once.
        #
        # The sampler covers only the FIRST transfer, deliberately. Its output
        # feeds collect_oscillation, which reads a per-second series and looks
        # for a period in it; spanning three transfers would put two idle gaps
        # and two BBR startup ramps inside that series, and the autocorrelation
        # would lock onto the transfer cadence rather than onto any nested-CC
        # effect. A shape metric needs one continuous transfer, so it gets one.
        local qreps="${CI_BENCH_QUIC_REPEATS:-3}" qi gsamples=()
        for (( qi=0; qi<qreps; qi++ )); do
            if [ "$qi" = 0 ]; then
                # Resolved here rather than inside the sampler: under a tier
                # the pid must be walked down through systemd-run, which
                # server_pid already knows how to do.
                CI_BENCH_SAMPLE_PID="$(server_pid)"
                sampler_start "$NETSIM_NS_SERVER" "$(netsim_veth_srv 0)"
            fi

            # Globals, never $( ) -- ci_bench_quic_transfer starts a background
            # server and records its pid, which a subshell would take with it.
            ci_bench_quic_transfer "$TUNNEL_SERVER_IP"

            if [ "$qi" = 0 ]; then
                sampler_stop
                # Counters and the shape metric come from the first transfer's
                # window, matching what the sampler saw. Reading them after
                # three transfers would describe a different interval than
                # osc_* does, and the row would silently mix the two.
                stats="$(collect_stats)"; rss="$(server_rss_kb)"
                stats="${stats}$(collect_wlb_instr)$(collect_send_supply)"
                stats="${stats}$(collect_sampler)$(collect_oscillation)"
                stats="${stats}$(collect_overhead)$(collect_proc_state)"
                stats="${stats}$(collect_iface_drops "$(netsim_veth_srv 0)")"
                CI_BENCH_SAMPLE_PID=""
                qst="$QUIC_STATUS"
            elif [ "$QUIC_STATUS" != ok ] && [ "$qst" = ok ]; then
                # A cell that succeeded once and failed later is neither "ok"
                # nor the failure status -- say so rather than letting whichever
                # transfer ran last define the row.
                qst=quic_partial_failure
            fi

            [ "$QUIC_GOODPUT" != NA ] && gsamples+=("$QUIC_GOODPUT")
        done

        gp_n="${#gsamples[@]}"
        if [ "$gp_n" -gt 0 ]; then
            gp="$(med "${gsamples[@]}")"
            gp_cv="$(cv_pct "${gsamples[@]}")"
            gp_all="$(IFS=,; echo "${gsamples[*]}")"
        fi
    fi

    ci_bench_stop_vpn

    python3 -c "
import json, os, sys

class_, sched, gp, qst = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
rss, extra, st = int(sys.argv[5]), sys.argv[6], sys.argv[7]
pin = sys.argv[8]
gp_cv, gp_n, gp_all = sys.argv[9], sys.argv[10], sys.argv[11]

row = {
  'scenario': class_,
  'mode_family': 'quic',
  'inner_proto': 'quic_h3',
  'scheduler': sched,
  'arm': os.environ.get('CI_BENCH_ARM') or 'default',
  'arm_reorder': os.environ.get('CI_BENCH_REORDER') or 'default',
  'picoquic_pin': pin,
  'server_rss_peak_kb': rss,
  'status': st,
  'quic_status': qst,
}

# NA is a sentinel, never 0: a transfer that failed and one that genuinely
# carried nothing are different findings, and the report filters the sentinel
# rather than averaging it in.
row['quic_goodput_mbps'] = None if gp == 'NA' else float(gp)

# The median's own spread, and the count behind it. A 12x gap between two
# schedulers means nothing without knowing whether either figure is stable, and
# n is here explicitly because the first run of this mode reported n=1 cells in
# a row-shape that looked identical to a repeated one.
row['quic_samples'] = int(gp_n)
row['quic_goodput_cv_pct'] = float(gp_cv) if gp_cv else None
# Every sample, not just the summary. A cell whose three transfers came out
# 70/2/68 is a different finding from one that came out 47/46/47, and the
# median hides which happened.
row['quic_goodput_all'] = ([float(x) for x in gp_all.split(',') if x]
                           if gp_all else [])

if extra:
    row.update(json.loads('{' + extra + '}'))

f = []
if row.get('osc_verdict') == 'oscillating':
    f.append('nested_cc_oscillation: outer wire rate swung %.1fx between its '
             '10th and 90th percentile with a %ss period -- consistent with '
             'inner and outer congestion control backing off together. The '
             'inner RTT is INFERRED from the outer series, not measured.'
             % (row.get('osc_peak_trough_ratio') or 0,
                row.get('osc_autocorr_period_s')))
elif row.get('osc_verdict') == 'unstable':
    f.append('quic_rate_unstable: outer wire rate swung %.1fx with no '
             'detectable period -- a stall or a trend rather than an '
             'oscillation' % (row.get('osc_peak_trough_ratio') or 0))
# Reverse-direction per-packet cost, from the veth counters rather than the
# xquic ones. The field this used to read (overhead_bytes_per_pkt_rx) divided
# a STREAM|DATAGRAM byte counter by an all-packets counter and reported 0.2
# bytes per packet, so the threshold below had never once been reachable.
#
# The new figure INCLUDES the outer UDP/IP headers (the old one claimed to
# exclude them), so 200 bytes here means the reverse path is carrying about
# 170 bytes of QUIC on top of a 28-byte outer header -- large for a direction
# that should be mostly ACKs.
bpp_rx = row.get('samp_wire_bytes_per_pkt_rx')
if bpp_rx is not None and bpp_rx >= 200:
    f.append('quic_ack_overhead: %.0f wire bytes per reverse-direction packet, '
             'outer headers included (inner ACKs are small; this is mostly '
             'encapsulation)' % bpp_rx)
if row.get('samp_sndbuf_errors'):
    f.append('quic_sndbuf_blocked: %d socket-buffer refusals during the run'
             % row['samp_sndbuf_errors'])
if qst not in ('ok',):
    f.append('quic_transfer_incomplete: %s' % qst)
# A cell whose repeats disagree by more than the harness-wide noise floor is
# reporting a bimodal regime, not a rate. Worth naming: it is the signature of
# a transfer that sometimes completes and sometimes collapses, which is exactly
# what nested CC would do near its tipping point.
if (row.get('quic_goodput_cv_pct') or 0) >= 30:
    f.append('quic_goodput_unstable: %.0f%% CV across %d transfers (%s) -- the '
             'median is not a rate here'
             % (row['quic_goodput_cv_pct'], row['quic_samples'],
                row.get('quic_goodput_all')))
row['findings'] = f
row['finding_count'] = len(f)
print(json.dumps(row))" \
        "$class" "$sched" "$gp" "$qst" "$rss" "$stats" "$st" \
        "${CI_BENCH_QUIC_PIN:-unknown}" "$gp_cv" "$gp_n" "$gp_all" >> "$ROWS"

    echo "   quic ${gp} Mbps (n=${gp_n} cv ${gp_cv:-NA}%) [${qst}] ${st}"
    emit_results
    netsim_teardown
}

# ── scenario: game proxy, single path, small packets ──────────────────────
#
# The workload this models: an optimized single-path proxy carrying a game.
# Euro Truck Simulator 2 is the reference -- a few 1400-byte openers, then a
# continuous stream of 10-50 byte updates at high frequency, never more than a
# few hundred kbit/s. Bandwidth cannot be the constraint at that rate, so
# throughput is NOT the measurement here and no *_mbps field from these rows
# enters the report's METRIC_FIELDS. What binds is latency and per-packet
# handling cost.
#
# Scheduler: wlb_udp_pin, set at MODE scope rather than per row. Every other
# mode passes the scheduler to the server only (all four ci_bench_start_client
# call sites omit it), which is defensible while every measurement is a DL bulk
# transfer -- the server schedules the payload and the client only schedules
# returning ACKs (ci_bench_env.sh:53). Game traffic is bidirectional and small
# in both directions, so the client's scheduler stops being irrelevant and both
# ends have to agree.
#
# What this does and does not test: on ONE path, wlb_udp_pin cannot change
# which path is chosen, because there is no choice. What it changes is which
# code path runs -- flow_sched.c:61 pins UDP only when udp_pin is set, so this
# exercises the pinned datagram lane and XQC_DATA_QOS_HIGH instead of unpinned
# WRR. Read these rows as a test of that lane, not of path selection.
#
# The criterion is added latency against the SAME tier measured without the
# tunnel. Both halves come from one scenario setup, so the difference is a
# within-run quantity: run 34019491401 vs 34026833126 disagreed by 13% at the
# median on control rows that neither run could have affected, so anything
# compared across runs at this repeat count is not a result.
# gamegen both directions at once, against one target address.
#
# Bidirectional because a game proxy is: the client sends input, the server
# broadcasts entity state back, and it is the RETURN direction that carries the
# packets a player sees. Every measurement here before this was `iperf3 UDP DL`
# -- one direction, and the quality parsed at the receiver -- so the uplink was
# never loaded at all while the downlink was measured on an otherwise idle
# tunnel. That is not the shape under test.
#
# Sets GG_DL_* and GG_UL_* rather than echoing: two background receivers and a
# subshell would take their pids with it.
#
# The client's own address inside the tunnel, read from the interface rather
# than assumed. The harness has never needed it -- every prior measurement
# dialled TUNNEL_SERVER_IP -- so there is no constant to reuse, and the server
# assigns it out of the 10.0.0.0/24 pool at connect time.
gg_client_tun_ip() {
    ip netns exec "$NS_CLIENT" ip -4 -o addr show dev "${CI_BENCH_TUN_NAME:-mqvpn0}" \
        2>/dev/null | awk '{split($4,a,"/"); print a[1]; exit}'
}

# $1 target address, $2 seconds, $3 pps per direction, $4 tag for temp files
gamegen_pair() {
    local target="$1" secs="$2" pps="$3" tag="$4"
    local gg="${SCRIPT_DIR}/gamegen.py"
    local dl_out="/tmp/gg_${tag}_dl.bin" ul_out="/tmp/gg_${tag}_ul.bin"
    local hz="${CI_BENCH_GAME_TICK_HZ:-10}"
    local len="${CI_BENCH_GAME_LEN:-10:30}"
    local bm="${CI_BENCH_GAME_BURST_MULT:-2.0}"
    local be="${CI_BENCH_GAME_BURST_EVERY:-10}"
    local dlp="${GG_PORT_DL:-5301}" ulp="${GG_PORT_UL:-5302}"

    GG_DL="" GG_UL="" GG_STATUS=ok
    rm -f "$dl_out" "$ul_out"

    local cli_ip; cli_ip="$(gg_client_tun_ip)"
    if [ -z "$cli_ip" ]; then
        # Without it there is no downlink target. Named rather than silently
        # producing a one-directional row that looks like the old behaviour.
        GG_STATUS=no_client_tun_ip
        return 0
    fi

    # Downlink receiver in the client ns, uplink receiver in the server ns.
    # Both armed before either sender starts, or the first tick is lost to a
    # bind race and scores as loss.
    ip netns exec "$NS_CLIENT" python3 "$gg" recv \
        --bind "${cli_ip}:${dlp}" \
        --out "$dl_out" --secs "$((secs + 10))" --idle 3 &>/dev/null &
    local dl_rx=$!
    ip netns exec "$NETSIM_NS_SERVER" python3 "$gg" recv \
        --bind "${target}:${ulp}" \
        --out "$ul_out" --secs "$((secs + 10))" --idle 3 &>/dev/null &
    local ul_rx=$!
    sleep 0.4

    ip netns exec "$NETSIM_NS_SERVER" python3 "$gg" send \
        --to "${cli_ip}:${dlp}" --secs "$secs" \
        --tick-hz "$hz" --pps "$pps" --len "$len" \
        --burst-mult "$bm" --burst-every "$be" &>/dev/null &
    local dl_tx=$!
    ip netns exec "$NS_CLIENT" python3 "$gg" send \
        --to "${target}:${ulp}" --secs "$secs" \
        --tick-hz "$hz" --pps "$pps" --len "$len" \
        --burst-mult "$bm" --burst-every "$be" &>/dev/null &
    local ul_tx=$!

    wait "$dl_tx" "$ul_tx" 2>/dev/null || true
    # The senders emit FIN; a bounded wait keeps one lost FIN from hanging the
    # mode the way an unguarded iperf3 client once burned a 60-minute job.
    # Both must be gone, not either -- `||` here would break as soon as the
    # faster receiver exited and truncate the other one's capture.
    local i alive
    for (( i=0; i<40; i++ )); do
        alive=0
        kill -0 "$dl_rx" 2>/dev/null && alive=1
        kill -0 "$ul_rx" 2>/dev/null && alive=1
        [ "$alive" = 1 ] || break
        sleep 0.25
    done
    kill "$dl_rx" "$ul_rx" 2>/dev/null || true
    wait "$dl_rx" "$ul_rx" 2>/dev/null || true

    local rto="${CI_BENCH_GAME_RTO_MS:-410}"
    [ -s "$dl_out" ] && GG_DL="$(python3 "$gg" analyze --in "$dl_out" \
        --rto-ms "$rto" --fragment 2>/dev/null)"
    [ -s "$ul_out" ] && GG_UL="$(python3 "$gg" analyze --in "$ul_out" \
        --rto-ms "$rto" --fragment 2>/dev/null)"
    rm -f "$dl_out" "$ul_out"
}

run_game() {
    local tier="$1" pps="$2"
    local scenario="${tier}_${pps}pps"

    echo ""
    echo "── game ${tier} @ ${pps} pps ──"
    ci_bench_stop_vpn 2>/dev/null || true
    netsim_setup 1 >/dev/null || return 1
    # Single path expressed procedurally, following run_catalog. It cannot go in
    # NETSIM_CLASS: ${spec%%|*} and ${spec##*|} both return the whole string
    # when there is no '|', so a single-leg entry there silently becomes a
    # duplicated pair rather than one path.
    netsim_apply_path 0 "eth:${tier}:public" 4242 || { netsim_teardown; return 1; }

    # ETS2-shaped: 50-byte payload at a fixed packet rate. -b takes bits/sec,
    # so pps x bytes x 8 pins the rate; iperf3's own sequence numbers give
    # loss and reorder end to end, independent of mqvpn's reorder engine
    # (which is off by default, so its counters would read zero for the wrong
    # reason).
    local pkt_len=50
    local target_bw=$(( pps * pkt_len * 8 ))
    local dur="${CI_BENCH_GAME_SEC:-20}"

    # gamegen's rate is derived the way the workload is actually specified:
    # bytes on the wire, not a packet count picked in advance. At a mean
    # payload of 20 B plus 28 B of UDP+IP, 800 KB/s is ~17,067 pps and 1 MB/s
    # is ~21,845. The pps tiers this function is indexed by stay as the iperf3
    # axis; gamegen gets the byte-derived figure.
    local gg_pps="${CI_BENCH_GAME_PPS:-17067}"

    CI_BENCH_IPERF_LEN="$pkt_len"
    CI_BENCH_IPERF_INTERVAL=1

    # ── Baseline: the same emulated tier, no tunnel ──
    # Bare path via IP_A_SERVER_ADDR, the pattern ci_bench_raw_throughput.sh:73
    # uses. This is what makes added_p99 a difference rather than an absolute.
    CI_BENCH_IPERF_TARGET="$IP_A_SERVER_ADDR"
    local base_jf base_q base_j
    base_jf="$(ci_bench_run_iperf UDP DL "$dur" 1 "$target_bw")"
    base_q="$(ci_bench_parse_udp_quality "$base_jf")"
    base_j="$(ci_bench_parse_udp_jitter_p99 "$base_jf")"
    rm -f "$base_jf"
    CI_BENCH_IPERF_TARGET=""

    # ── Through the tunnel ──
    if ! start_server_with_ctrl "$CI_BENCH_SCHEDULER" >/dev/null; then
        CI_BENCH_IPERF_LEN=""; CI_BENCH_IPERF_INTERVAL=""
        skip_row "$scenario" setup_failed
        netsim_teardown
        return 1
    fi

    local st=ok
    if ! ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1; then
        st=client_start_failed
    elif ! ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
        st=tunnel_never_up
    fi

    local tun_q="NA NA NA NA NA" tun_j="NA NA"
    local stats="" rss=0
    if [ "$st" = ok ]; then
        # A handful of MTU-sized openers first, the way a game ships its
        # initial state before settling into small updates -- enough to make
        # the tunnel discover its path MTU and fill any first-packet caches,
        # so the measured phase is not paying that cost.
        #
        # Deliberately BEFORE the sampler starts, and its result discarded. At
        # 2 Mbit/s of 1400-byte packets the openers are ten times the offered
        # rate of the phase under measurement; inside the sampled window they
        # would dominate samp_tx_pps and put a large step at the head of the
        # very series the oscillation metric reads, which is a trough this
        # function created rather than one the tunnel produced. The baseline
        # skips them too, so both halves of added_p99 see the same shape.
        CI_BENCH_IPERF_LEN=1400
        local open_jf; open_jf="$(ci_bench_run_iperf UDP DL 2 1 2000000)"
        rm -f "$open_jf"
        CI_BENCH_IPERF_LEN="$pkt_len"

        ci_bench_mark_server_log
        CI_BENCH_SAMPLE_PID="$(server_pid)"
        sampler_start "$NETSIM_NS_SERVER" "$(netsim_veth_srv 0)"

        local tun_jf; tun_jf="$(ci_bench_run_iperf UDP DL "$dur" 1 "$target_bw")"
        tun_q="$(ci_bench_parse_udp_quality "$tun_jf")"
        tun_j="$(ci_bench_parse_udp_jitter_p99 "$tun_jf")"
        rm -f "$tun_jf"

        # Then the shape the mode is actually about: bidirectional, 10 Hz
        # ticks, 10-30 byte payloads, every tenth tick doubled. iperf3 above
        # stays for continuity of added_p99 and because it is one direction of
        # fixed-size packets -- a different question, kept separate rather
        # than reinterpreted.
        gamegen_pair "$TUNNEL_SERVER_IP" "$dur" "$gg_pps" "${tier}_${pps}"

        sampler_stop
        CI_BENCH_SAMPLE_PID=""
        stats="$(collect_stats)"; rss="$(server_rss_kb)"
        stats="${stats}$(collect_wlb_instr)$(collect_send_supply)"
        stats="${stats}$(collect_sampler)$(collect_overhead)"
        stats="${stats}$(collect_host_profile "$(netsim_veth_srv 0)")"
        stats="${stats}$(collect_proc_state)"
        # Both interfaces: the veth is where the emulated network starts, the
        # TUN is where a single-threaded forwarder that fell behind drops
        # packets. Field data localised a 1.9% loss to the TUN ring precisely
        # because the two were read separately.
        stats="${stats}$(collect_iface_drops "$(netsim_veth_srv 0)")"
        stats="${stats}$(collect_tun_drops)"

        # Re-key each direction so the two cannot collide, and so no reader has
        # to guess which way a stall figure points. dl = server to client, the
        # direction a player's screen is fed from.
        #
        # `if` rather than `[ ] && ...`: under set -e a trailing test that
        # comes out false is the function's exit status, and this block is the
        # last thing before the row is written.
        if [ -n "${GG_DL:-}" ]; then
            stats="${stats}$(printf '%s' "$GG_DL" | sed -e 's/"gg_/"ggdl_/g')"
        fi
        if [ -n "${GG_UL:-}" ]; then
            stats="${stats}$(printf '%s' "$GG_UL" | sed -e 's/"gg_/"ggul_/g')"
        fi
        if [ "${GG_STATUS:-unrun}" != ok ]; then
            stats="${stats},\"gamegen\":\"${GG_STATUS:-unrun}\""
        fi
    fi

    ci_bench_stop_vpn
    CI_BENCH_IPERF_LEN=""; CI_BENCH_IPERF_INTERVAL=""

    python3 -c "
import json, os, sys

scenario, tier, pps, dur = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
base_q, base_j = sys.argv[5].split(), sys.argv[6].split()
tun_q, tun_j = sys.argv[7].split(), sys.argv[8].split()
rss, extra, st = int(sys.argv[9]), sys.argv[10], sys.argv[11]
reorder_state = sys.argv[12]

def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None

# quality tuple: lost_pct jitter_ms out_of_order packets mbps
bl, bj_ms, boo, bpk, bmb = (num(x) for x in base_q)
tl, tj_ms, too, tpk, tmb = (num(x) for x in tun_q)
b_p99, b_max = (num(x) for x in base_j)
t_p99, t_max = (num(x) for x in tun_j)

row = {
  'scenario': scenario,
  'mode_family': 'game',
  'rtt_tier_ms': int(tier.split('_')[1]),
  'game_pps_tier': pps,
  # gamegen's own offered rate, which is NOT game_pps_tier: that names the
  # iperf3 leg's fixed-size axis. Recorded so a row states the rate it was
  # driven at -- run 34084331771 lost 23-49% at 17067 and the obvious next
  # question was whether a lower rate is clean, which is unanswerable from an
  # artifact that does not say what rate it used.
  'gg_offered_pps': int(os.environ.get('CI_BENCH_GAME_PPS') or 17067),
  'gg_tick_hz': float(os.environ.get('CI_BENCH_GAME_TICK_HZ') or 10),
  'pkt_bytes': 50,
  'scheduler': os.environ.get('CI_BENCH_SCHEDULER') or 'wlb_udp_pin',
  'arm': os.environ.get('CI_BENCH_ARM') or 'default',
  'arm_reorder': os.environ.get('CI_BENCH_REORDER') or 'default',
  # So a zero reorder column can never be misread as 'no reorder happened'
  # when it really means 'the engine that counts it was switched off'.
  'reorder_engine': reorder_state,
  'duration_sec': dur,
  'server_rss_peak_kb': rss,
  'status': st,
}

row['baseline_loss_pct'] = bl
row['tunnel_loss_pct'] = tl
row['baseline_jitter_p99_ms'] = b_p99
row['tunnel_jitter_p99_ms'] = t_p99
row['baseline_out_of_order'] = boo
row['tunnel_out_of_order'] = too

# THE criterion. A within-run difference, so the cross-run drift that makes
# absolute figures unreadable does not touch it.
if b_p99 is not None and t_p99 is not None:
    row['added_jitter_p99_ms'] = round(t_p99 - b_p99, 3)
if bl is not None and tl is not None:
    row['added_loss_pct'] = round(tl - bl, 4)

# Did the tunnel carry the packet rate it was asked for? Below 1.0 means
# packets were dropped or coalesced somewhere in the path, which on a
# latency-bound profile matters more than any byte figure.
if tpk is not None and dur > 0:
    row['tunnel_pps_measured'] = round(tpk / dur, 1)
    row['pps_fidelity'] = round((tpk / dur) / pps, 3) if pps else None
if too is not None and tpk:
    row['out_of_order_pct'] = round(100.0 * too / tpk, 4)

if extra:
    row.update(json.loads('{' + extra + '}'))

f = []
ap = row.get('added_jitter_p99_ms')
al = row.get('added_loss_pct')
fid = row.get('pps_fidelity')
# Thresholds are stated as what they are: a proxy. iperf3 reports jitter, not
# a latency distribution, so this is the p99 of the per-second jitter series
# and not a per-packet RTT percentile. Named accordingly everywhere.
if ap is not None and ap >= 5.0:
    f.append('game_jitter_cost: tunnel added %.1f ms of p99 jitter over the '
             'bare path at the same emulated RTT' % ap)
if al is not None and al >= 0.5:
    f.append('game_loss_cost: tunnel added %.2f%% loss over the bare path' % al)
if fid is not None and fid < 0.95:
    f.append('game_pps_shortfall: carried %.1f%% of the offered packet rate -- '
             'a packet-handling limit, not a bandwidth one' % (100.0 * fid))
if row.get('samp_sndbuf_errors'):
    f.append('game_sndbuf_blocked: %d socket-buffer refusals during the run'
             % row['samp_sndbuf_errors'])

# The stall window, both directions. This is the player-visible one: the time
# an ordered channel spends holding packets it already has, waiting for one
# that is late. A tunnel can score 0.3% loss and still be unplayable if that
# loss lands as long freezes.
for d, label in (('ggdl', 'downlink'), ('ggul', 'uplink')):
    pctime = row.get('%s_stall_time_pct' % d)
    if pctime is not None and pctime >= 1.0:
        f.append('game_stall_%s: an ordered channel would be stalled %.1f%% of '
                 'the time (%d stalls, p99 %s ms) -- this is the teleporting a '
                 'player sees, not the %s%% packet loss'
                 % (label, pctime, row.get('%s_stalls' % d) or 0,
                    row.get('%s_stall_p99_ms' % d),
                    row.get('%s_loss_pct' % d)))
    # A generator that could not keep up looks exactly like a lossy tunnel.
    if row.get('%s_rcvbuf_drops' % d):
        f.append('game_gen_overrun_%s: %d packets dropped in the RECEIVER '
                 'socket buffer -- the harness could not keep up, so loss and '
                 'stall figures for this direction are not the tunnel'
                 % (label, row['%s_rcvbuf_drops' % d]))
if row.get('gamegen') and row['gamegen'] != 'ok':
    f.append('game_gen_failed: %s' % row['gamegen'])

row['findings'] = f
row['finding_count'] = len(f)
print(json.dumps(row))" \
        "$scenario" "$tier" "$pps" "$dur" "$base_q" "$base_j" \
        "$tun_q" "$tun_j" "$rss" "$stats" "$st" \
        "${CI_BENCH_REORDER:-off_by_default}" >> "$ROWS"

    echo "   baseline_p99=$(echo "$base_j" | awk '{print $1}')ms" \
         "tunnel_p99=$(echo "$tun_j" | awk '{print $1}')ms  [${st}]"
    emit_results
    netsim_teardown
}

# ── scenario: special conditions, under saturating load ───────────────────
# All of these already have functional coverage under scripts/ci_e2e/. What
# was missing is running them while traffic is in flight, which is where the
# timing and buffering bugs actually live.
# A scenario that could not be set up still has to leave a row. run_special
# emitted nothing on its failure paths, so nat_aging and roam_under_load
# vanished from the artifact whenever their tunnel did not come up -- no row,
# no warning, and the job still reported success. The 2026-08-26 weekly shipped
# two rows where four were expected and nothing said so.
skip_row() {
    python3 -c "
import json,sys
print(json.dumps({'scenario': sys.argv[1], 'status': sys.argv[2]}))" "$1" "$2" >> "$ROWS"
    echo "   SKIPPED ($2)"
    emit_results
}

run_special() {
    local hop dev_up

    # 1. NAT state aging. conntrack's UDP timeout defaults to 30 s, so the
    #    "silent killer" is the default -- this just has to idle past it.
    echo ""
    echo "── special: nat_aging (idle 35s behind NAT, mid-session) ──"
    # The `:port_restricted` axis in the spec installs the masquerade and pins
    # nf_conntrack_udp_timeout to 30 s, so this test only has to idle past it.
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 "5g_half:bgp_plain:port_restricted" 4242; then
        if start_server_with_ctrl >/dev/null \
           && ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1 \
           && ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
            local before after jf
            jf="$(ci_bench_run_iperf TCP DL 4 "$IPERF_STREAMS")"; before="$(ci_bench_parse_throughput "$jf")"; rm -f "$jf"
            sleep 35
            jf="$(ci_bench_run_iperf TCP DL 4 "$IPERF_STREAMS")"; after="$(ci_bench_parse_throughput "$jf")"; rm -f "$jf"
            python3 -c "
import json,sys
b,a=float(sys.argv[1]),float(sys.argv[2])
# The status used to be the literal 'ok' whatever the samples did, so a run in
# which the tunnel came up and then carried nothing was indistinguishable from a
# clean one. The two samples are what decides it.
print(json.dumps({'scenario':'nat_aging','before_idle_mbps':b,'after_idle_mbps':a,
 'survived_ratio':round(a/b,3) if b else None,'recovered':1 if a>0.5 else 0,
 'status':'ok' if b>0 and a>0 else 'measured_zero' if b>0 or a>0
          else 'measured_zero_both'}))" \
                "$before" "$after" >> "$ROWS"
            echo "   before=${before} after=${after} Mbps"
            emit_results
        else
            skip_row nat_aging tunnel_never_up
        fi
        ci_bench_stop_vpn || true
        netsim_teardown
    else
        skip_row nat_aging setup_failed
    fi

    # 2. Corrupt / reorder / duplicate: radio-grade damage under load.
    echo ""
    echo "── special: corrupt_reorder (under load) ──"
    NETSIM_ACCESS[_damaged]="delay 30ms 10ms distribution normal corrupt 0.1% reorder 25% 50% duplicate 0.5% rate 100mbit"
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 "_damaged:bgp_plain" 4242 \
       && start_server_with_ctrl >/dev/null; then
        local mbps cv stats
        measure_pathset "--path $(netsim_veth_cli 0)"
        mbps="$MEASURED_MBPS"; cv="$MEASURED_CV"
        stats="$(collect_stats)"
        python3 -c "
import json,sys
row={'scenario':'corrupt_reorder','single_path_mbps':float(sys.argv[1]),
     'cv_pct':float(sys.argv[2]),'survived':1 if float(sys.argv[1])>0 else 0,
     'status':sys.argv[4]}
extra=sys.argv[3]
if extra: row.update(json.loads('{'+extra+'}'))
print(json.dumps(row))" "$mbps" "$cv" "$stats" "$MEASURED_STATUS" >> "$ROWS"
        echo "   ${mbps} Mbps  [${MEASURED_STATUS}]"
        emit_results
        ci_bench_stop_vpn || true
    else
        skip_row corrupt_reorder setup_failed
    fi
    netsim_teardown

    # 3. ACK starvation: saturate a deep uplink queue and see whether the
    #    downlink survives it. Asymmetric with a bloated up-queue is the
    #    classic consumer-broadband shape, and the thing that makes it a real
    #    test is that the ACKs for the downlink have to share that queue.
    echo ""
    echo "── special: ack_starvation (asymmetric + bloated uplink) ──"
    if netsim_setup 1 >/dev/null && netsim_apply_path 0 "eth:bgp_plain" 4242; then
        hop="$(netsim_hop_ns 0)"; dev_up="$(netsim_veth_hop_srv 0)"
        ip netns exec "$hop" tc qdisc replace dev "$dev_up" root \
            netem delay 30ms rate 5mbit limit 12000 || true
        if start_server_with_ctrl >/dev/null \
           && ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1 \
           && ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
            local dl_idle dl_busy ulpid jf2
            jf2="$(ci_bench_run_iperf TCP DL 5 "$IPERF_STREAMS")"; dl_idle="$(ci_bench_parse_throughput "$jf2")"; rm -f "$jf2"
            ip netns exec "$NS_SERVER" iperf3 -s -B "$TUNNEL_SERVER_IP" -p 5202 -1 &>/dev/null &
            sleep 1
            ip netns exec "$NS_CLIENT" iperf3 -c "$TUNNEL_SERVER_IP" -p 5202 -t 12 &>/dev/null &
            ulpid=$!
            sleep 3
            jf2="$(ci_bench_run_iperf TCP DL 5 "$IPERF_STREAMS")"; dl_busy="$(ci_bench_parse_throughput "$jf2")"; rm -f "$jf2"
            kill "$ulpid" 2>/dev/null || true; wait "$ulpid" 2>/dev/null || true
            python3 -c "
import json,sys
i,b=float(sys.argv[1]),float(sys.argv[2])
# Was the literal 'ok'. In run 33302660068 this row reported ok while carrying
# no RTT metrics at all, which no field in it could account for.
print(json.dumps({'scenario':'ack_starvation','dl_idle_mbps':i,'dl_under_ul_load_mbps':b,
 'dl_retention':round(b/i,3) if i else None,
 'status':'ok' if i>0 and b>0 else 'measured_zero' if i>0 or b>0
          else 'measured_zero_both'}))" "$dl_idle" "$dl_busy" >> "$ROWS"
            echo "   dl_idle=${dl_idle} dl_under_load=${dl_busy} Mbps"
            emit_results
        else
            skip_row ack_starvation tunnel_never_up
        fi
        ci_bench_stop_vpn || true
        netsim_teardown
    else
        skip_row ack_starvation setup_failed
    fi

    # 4. Live roaming: the client's source address changes mid-transfer, the
    #    way a handover looks to the server. Functional rebind coverage already
    #    exists under scripts/ci_e2e/; what it does not do is move the address
    #    while the link is saturated, which is where a rebind either costs a
    #    few hundred milliseconds or wedges the connection.
    echo ""
    echo "── special: roam_under_load (source address moves mid-transfer) ──"
    if netsim_setup 1 >/dev/null \
       && netsim_apply_path 0 "5g_full:bgp_plain:port_restricted" 4242; then
        if start_server_with_ctrl >/dev/null \
           && ci_bench_start_client "--path $(netsim_veth_cli 0)" >/dev/null 2>&1 \
           && ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null 2>&1; then
            local pre post roamed jf3
            jf3="$(ci_bench_run_iperf TCP DL 5 "$IPERF_STREAMS")"; pre="$(ci_bench_parse_throughput "$jf3")"; rm -f "$jf3"

            # Move the address underneath a transfer that is already running,
            # then measure what comes back. Measuring only after the roam would
            # not distinguish "recovered quickly" from "never noticed".
            ip netns exec "$NS_CLIENT" iperf3 -c "$TUNNEL_SERVER_IP" -t 14 &>/dev/null &
            local loadpid=$!
            sleep 3
            roamed=0
            if netsim_roam 0; then roamed=1; else
                echo "   (no conntrack/iptables — address did not actually move)"
            fi
            sleep 2
            jf3="$(ci_bench_run_iperf TCP DL 5 "$IPERF_STREAMS")"; post="$(ci_bench_parse_throughput "$jf3")"; rm -f "$jf3"
            kill "$loadpid" 2>/dev/null || true; wait "$loadpid" 2>/dev/null || true

            python3 -c "
import json,sys
p,q,r = float(sys.argv[1]), float(sys.argv[2]), int(sys.argv[3])
# Was the literal 'ok'. A roam that was never applied (no conntrack) or a
# transfer that died at the roam both used to publish as a clean pass.
print(json.dumps({'scenario':'roam_under_load','pre_roam_mbps':p,'post_roam_mbps':q,
 'roam_retention':round(q/p,3) if p else None,'roam_applied':r,
 'recovered':1 if q>0.5 else 0,
 'status':'ok' if p>0 and q>0 and r else 'roam_not_applied' if p>0 and not r
          else 'measured_zero' if p>0 or q>0 else 'measured_zero_both'}))" \
                "$pre" "$post" "$roamed" >> "$ROWS"
            echo "   pre=${pre} post=${post} Mbps (roam_applied=${roamed})"
            emit_results
        else
            skip_row roam_under_load tunnel_never_up
        fi
        ci_bench_stop_vpn || true
        netsim_teardown
    else
        skip_row roam_under_load setup_failed
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
    # in hetero_extreme before anywhere else. Shorter samples than the weekly
    # modes on purpose -- this one sits in the push path.
    TEST_NAME="netsim_percommit"
    IPERF_SEC="${CI_BENCH_IPERF_SEC:-6}"
    run_class hetero_extreme
    ;;
  classes)
    TEST_NAME="netsim_classes"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    for c in homo_good homo_bad hetero_extreme asym_capacity premium_plus_mobile \
             asym_latency one_flapping carrier_pair nat_split mtu_split \
             home_plus_tether dual_mobile sat_plus_cell; do
        run_class "$c" || echo "  (class $c failed, continuing)"
    done
    ;;
  mtu)
    # The MTU axis on its own: three sizes against a 1500 reference, the same
    # sizes under a packet-rate cap (where goodput tracks bytes-per-packet
    # rather than bandwidth), and the black hole that stalls instead of slowing.
    TEST_NAME="netsim_mtu"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    for c in mtu_1500 mtu_1400 mtu_split mtu_pps_1500 mtu_pps_1280 mtu_blackhole; do
        run_class "$c" || echo "  (class $c failed, continuing)"
    done
    ;;
  sched)
    # Scheduler comparison, restricted to the three classes where the
    # schedulers should actually disagree — a sweep over the classes where they
    # agree costs runner minutes and produces three identical lines.
    TEST_NAME="netsim_sched"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    SCHEDS="wlb minrtt"
    if grep -q "define XQC_ENABLE_FEC" \
           "${SCRIPT_DIR}/../../third_party/xquic/include/xquic/xqc_configure.h" 2>/dev/null \
       && grep -q "define XQC_ENABLE_XOR" \
           "${SCRIPT_DIR}/../../third_party/xquic/include/xquic/xqc_configure.h" 2>/dev/null; then
        SCHEDS="$SCHEDS backup_fec"
    else
        echo "::notice::xquic built without FEC+XOR — backup_fec omitted from the sweep"
    fi
    for c in hetero_extreme asym_latency one_flapping; do
        for s in $SCHEDS; do
            run_class "$c" "$s" || echo "  (class $c/$s failed, continuing)"
        done
    done
    ;;
  tiers)
    # Server box as the variable, network held constant. Every tier at healthy
    # for the ceiling, then the four host states on one mid tier — the states
    # are about contention, and repeating them per tier multiplies runtime
    # without adding an axis.
    TEST_NAME="netsim_tiers"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    if ! ci_bench_have_tiers; then
        echo "::warning::transient scopes unavailable — tier rows will be untiered" \
             "and must not be read as instance ceilings"
    fi
    for t in vps_1c1g vps_1c1g_std vps_2c2g vps_2c2g_fast; do
        run_tier "$t" healthy || echo "  (tier $t failed, continuing)"
    done
    for s in noisy_neighbour softirq_storm cpu_capped; do
        run_tier vps_2c2g "$s" || echo "  (host $s failed, continuing)"
    done
    ;;
  quic)
    # Inner QUIC rather than inner TCP. Four classes, chosen for what each can
    # settle rather than for coverage:
    #   homo_good       identical healthy legs -- the control. Anything this row
    #                   shows is a property of the tunnel, not of the network.
    #   asym_capacity   the row the WLB charge/weight fix moved (0.852 -> 1.330
    #                   vs_best_single, run 34026833126). Does inner QUIC see
    #                   the same gain?
    #   hetero_extreme  one leg near dead: the reorder-vs-aggregation case.
    #   nat_split       identical legs aggregating to 64% of one of them with
    #                   the send queue draining every pass. The anomaly this
    #                   whole mode exists to explain.
    #
    # Both schedulers on every class: wlb vs wlb_udp_pin is defined on inner
    # UDP, so this is the first measurement in the harness that can tell them
    # apart at all.
    TEST_NAME="netsim_quic"
    if ! ci_bench_quic_available; then
        echo "::warning::picoquicdemo not found — quic mode will emit" \
             "picoquic_missing rows. Build it with" \
             "scripts/ci_interop/build_picoquic.sh"
    fi
    for c in homo_good asym_capacity hetero_extreme nat_split; do
        for s in wlb wlb_udp_pin; do
            run_quic "$c" "$s" || echo "  (quic $c/$s failed, continuing)"
        done
    done
    ;;
  game)
    # Game proxy: single path, optimized transit, small packets, latency-bound.
    #
    # CI_BENCH_SCHEDULER rather than a per-row argument, because only the
    # server receives the per-row scheduler (every ci_bench_start_client call
    # omits it) and game traffic is bidirectional -- both ends have to agree.
    TEST_NAME="netsim_game"
    CI_BENCH_SCHEDULER=wlb_udp_pin
    # No coalescing on either side: see ci_bench_config_append. A game row is
    # about when packets arrive, and batching changes exactly that.
    ci_bench_config_append '[Advanced]' 'UdpGso = false' 'UdpGro = false'
    export CI_BENCH_OFFLOAD=off
    # PPS tiers, not bandwidth tiers. 500 pps of 50-byte payload is 200 kbit/s;
    # 4000 pps is 1.6 Mbit/s, still far under any emulated rate here, so the
    # axis stays packet rate throughout and never becomes a bandwidth test.
    for t in game_50 game_100 game_150 game_200; do
        for p in 500 2000; do
            run_game "$t" "$p" || echo "  (game $t/$p failed, continuing)"
        done
    done
    ;;
  vps)
    # The constrained box the game profile actually runs on: 1 vCPU, single
    # VirtIO RX/TX queue, RPS disabled, IRQ and NET_RX on CPU0.
    #
    # What is honestly emulated and what is not: AllowedCPUs=0 plus CPUQuota
    # reproduces the THROUGHPUT CEILING of a 1-vCPU instance. The single-queue
    # and RPS-disabled facts need no emulation at all -- veth exposes exactly
    # one rx and one tx queue, rps_cpus reads all zero by default, and
    # /proc/softirqs shows NET_RX entirely on CPU0 -- so the sampler ASSERTS
    # them (samp_net_rx_cpu0_share) rather than pretending to create them.
    # VirtIO interrupt coalescing and hypervisor steal time cannot be
    # reproduced from inside a guest at all and are named as such on the row.
    TEST_NAME="netsim_vps"
    CI_BENCH_SCHEDULER=wlb_udp_pin
    # Same traffic shape as the game mode, so the same offload setting -- and
    # on a 1-vCPU box the syscall saving GSO buys is exactly the trade this
    # mode is meant to expose, not something to quietly take.
    ci_bench_config_append '[Advanced]' 'UdpGso = false' 'UdpGro = false'
    export CI_BENCH_OFFLOAD=off
    if ! ci_bench_have_tiers; then
        echo "::warning::transient scopes unavailable — vps rows will be" \
             "untiered and must not be read as 1-vCPU ceilings"
    fi
    # EXPORTED, not just assigned. collect_host_profile reads CI_BENCH_TIER
    # from a python3 child's environment, so an unexported value is visible to
    # the shell that splices in the systemd-run prefix but invisible to the
    # code that writes the column. Every vps row in run 34036912262 came back
    # host_tier=untiered while the scope had in fact been created -- the
    # tier applied and the artifact denied it, which is the worse of the two
    # failure directions.
    export CI_BENCH_TIER=vps_1c1g_std
    # Exported alongside it so the row can state the ceiling and whether the
    # scope actually took, rather than leaving both to be inferred from a
    # label. ci_bench_have_tiers ran above; reuse its answer.
    if ci_bench_have_tiers; then
        export CI_BENCH_TIER_OK=yes
    else
        export CI_BENCH_TIER_OK=no
    fi
    export CI_BENCH_TIER_NCPU_VAL="${CI_BENCH_TIER_NCPU[vps_1c1g_std]:-unknown}"
    # The box is not empty before mqvpn starts. Every vps row so far handed the
    # whole tier to the server, and the four rows of run 34043133862 came back
    # indistinguishable from the untiered game rows -- 25.0% vs 24.8% CPU,
    # pps_fidelity 1.000 on both -- because 800 kbit/s of game traffic cannot
    # trouble an otherwise idle core. The tier was applied and measured
    # nothing.
    #
    # Field data from a production 1 vCPU / 1 GB instance: ~15% CPU already
    # consumed, 559 MB of 929 MB resident, 378 MB of swap in use, and mqvpn
    # itself sitting at VmRSS 524 kB against VmSwap 68 MB. That last pair is
    # the state worth reproducing -- a forwarder that has to fault its own
    # pages back in before it can forward.
    export CI_BENCH_HOST_STATE="${CI_BENCH_VPS_HOST_STATE:-vps_baseline}"
    ci_bench_host_start "$CI_BENCH_HOST_STATE" vps_1c1g_std
    for t in game_100 game_200; do
        for p in 500 2000; do
            run_game "$t" "$p" || echo "  (vps game $t/$p failed, continuing)"
        done
    done
    ci_bench_host_stop
    unset CI_BENCH_TIER CI_BENCH_TIER_OK CI_BENCH_TIER_NCPU_VAL \
          CI_BENCH_HOST_STATE
    ;;
  combo)
    TEST_NAME="netsim_combo"
    REPEATS="${CI_BENCH_REPEATS:-3}"
    run_combo
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

# Only here does the document get to call itself complete; every earlier write
# carries complete=0 so a consumer can tell a cancelled job's partial results
# from a finished mode.
RESULTS_COMPLETE=1
emit_results

echo ""
echo "scenarios recorded: $(grep -c . "$ROWS")"
echo "Result: ${RESULTS_OUT:-<no rows>}"

# Non-zero here means the harness did not measure what this mode claims to
# cover. The artifact has already been written, so a failing gate still ships
# every row it managed to produce.
_cb_summarise_and_gate
