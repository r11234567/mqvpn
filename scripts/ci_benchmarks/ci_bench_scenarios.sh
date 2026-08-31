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
import json, sys

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
    netsim_spike_stop
    ci_bench_stop_vpn

    # Ratios are the gate-able numbers: they divide out the runner's speed,
    # which absolute Mbps on a shared vCPU cannot.
    python3 -c "
import json,sys
a,b,mp = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3])
st_a, st_b, st_mp = sys.argv[12], sys.argv[13], sys.argv[14]
mcls_a, mcls_b = sys.argv[15], sys.argv[16]
row = {
  'scenario': sys.argv[4], 'scheduler': sys.argv[5],
  'path_a': sys.argv[6], 'path_b': sys.argv[7],
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
if 'below' in (mcls_a, mcls_b):
    f.append('mtu_below_client_pkt: a leg MTU is under the 1428-byte outer '
             'datagram mqvpn emits; xquic never lowers a path packet size '
             '(xqc_conn.c:2105, xqc_send_ctl.c:1723), so the scheduler keeps '
             'sending what that leg cannot carry')
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

    if [ -n "$cap_pid" ]; then
        kill "$cap_pid" 2>/dev/null || true
        wait "$cap_pid" 2>/dev/null || true
    fi
    ci_bench_host_stop
    ci_bench_stop_vpn
    CI_BENCH_TIER=""

    python3 -c "
import json,sys
row = {
  'scenario': sys.argv[1], 'tier': sys.argv[2], 'host_state': sys.argv[3],
  'net_class': sys.argv[4], 'scheduler': sys.argv[5],
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
