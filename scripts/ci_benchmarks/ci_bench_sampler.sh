#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# ci_bench_sampler.sh — per-second host and interface sampling
#
# Why this exists: every netsim row is one scalar per measurement, so a
# throughput that oscillates and one that holds flat at the same mean are
# indistinguishable in the artifact. Nested congestion control -- outer MPQUIC
# retransmit inflating inner RTT until the inner stack also backs off -- shows
# up as a shape over time and in no single number, so the shape has to be
# recorded.
#
# Why it reads /proc rather than the control socket: netsim_query_control spawns
# `ip netns exec` + `bash` + `timeout nc` per call, three processes a sample. At
# 1 Hz over a 10-second measurement that is thirty process spawns competing for
# two vCPUs with the transfer whose throughput is the number under measurement.
# /proc/net/snmp, /proc/stat, /proc/softirqs and
# /sys/class/net/<dev>/statistics/* are single-file reads, and the network ones
# are namespace-scoped, so one long-lived reader inside the namespace gets the
# same facts for a rounding error of the cost.
#
# The sampler reports its own CPU time in samp_cost_note for that reason. A
# measurement tool that perturbs the measurement is only safe if the
# perturbation is on the record instead of assumed away.
#
# What it cannot see: this is the OUTER tunnel's view. Inner-QUIC RTT is not
# observable here and is INFERRED from outer retransmit correlated against the
# throughput trough. Every consumer of these fields must say "inferred".

# Sampling period. 1 s matches iperf3's own -i 1 interval, so the two series can
# be read against each other without resampling.
CI_BENCH_SAMPLE="${CI_BENCH_SAMPLE:-0}"
CI_BENCH_SAMPLE_INTERVAL="${CI_BENCH_SAMPLE_INTERVAL:-1}"

_CB_SAMP_PID=""
_CB_SAMP_CSV=""

# Results, set by sampler_stop. Globals rather than stdout for the same reason
# measure_pathset uses globals (ci_bench_scenarios.sh:268) -- a subshell would
# take the background pid with it and leave the sampler running into the next
# scenario.
SAMPLED_TICKS=0
SAMPLED_STATUS=off
SAMPLED_JSON=""

# sampler_start <netns> <dev> [interval]
#
# One background process for the whole measurement. The loop body is builtins
# plus four small reads per tick; no subshell per field, and the namespace is
# entered once rather than per sample.
sampler_start() {
    [ "$CI_BENCH_SAMPLE" = "1" ] || { SAMPLED_STATUS=off; return 0; }

    local ns="$1" dev="$2"
    local interval="${3:-$CI_BENCH_SAMPLE_INTERVAL}"

    sampler_stop_quiet

    _CB_SAMP_CSV="$(mktemp)"
    SAMPLED_STATUS=running
    SAMPLED_JSON=""

    # Resolved once out here so the tick does no path arithmetic. A missing
    # statistics dir leaves those columns at zero rather than killing the
    # sampler: a gap in the series is not a failed measurement.
    local stat_dir="/sys/class/net/${dev}/statistics"

    # /proc is not namespaced the way /sys/class/net is, so the server's status
    # file is readable from inside the netns by pid. Empty when there is no
    # server yet (the baseline leg runs before one starts), which leaves the
    # process columns at zero rather than failing the tick.
    local srv_pid="${CI_BENCH_SAMPLE_PID:-}"

    ip netns exec "$ns" bash -c '
        csv="$1"; stat_dir="$2"; interval="$3"; srv_pid="$4"
        while :; do
            now="$(date +%s.%N)"
            # /proc/stat line 1: cpu user nice system idle iowait irq softirq steal
            read -r _ u n s idl iow irq sirq steal _ < /proc/stat

            # /proc/net/snmp carries a header row and a value row per protocol.
            # After `set -- $rest`: 1=InDatagrams 2=NoPorts 3=InErrors
            # 4=OutDatagrams 5=RcvbufErrors 6=SndbufErrors
            #
            # RcvbufErrors was parsed and thrown away for three runs. Field
            # data put 441 of them on a box that was dropping packets, so it
            # is the receive-side counterpart of SndbufErrors and is now kept.
            udp_in=0; udp_out=0; sndbuf=0; rcvbuf=0
            while read -r label rest; do
                [ "$label" = "Udp:" ] || continue
                set -- $rest
                # Header row has words where numbers go.
                case "$1" in ""|*[!0-9]*) continue ;; esac
                udp_in="$1"; udp_out="$4"; rcvbuf="$5"; sndbuf="$6"
            done < /proc/net/snmp

            # NET_RX per CPU. The VPS profile under test puts every queue and
            # IRQ on CPU0, so CPU0 and the total are both kept to show whether
            # that actually held.
            netrx0=0; netrx=0
            while read -r label rest; do
                [ "$label" = "NET_RX:" ] || continue
                set -- $rest
                netrx0="$1"
                netrx=0
                for v in "$@"; do netrx=$((netrx + v)); done
            done < /proc/softirqs

            txp=0; rxp=0; txb=0; rxb=0
            [ -r "$stat_dir/tx_packets" ] && read -r txp < "$stat_dir/tx_packets"
            [ -r "$stat_dir/rx_packets" ] && read -r rxp < "$stat_dir/rx_packets"
            [ -r "$stat_dir/tx_bytes" ]   && read -r txb < "$stat_dir/tx_bytes"
            [ -r "$stat_dir/rx_bytes" ]   && read -r rxb < "$stat_dir/rx_bytes"

            # Drops on the measured interface, per tick. The end-to-end delta
            # is collected separately, but the series is what shows whether
            # the loss was steady or arrived in bursts -- field data had 1.9%
            # overall that was in fact six short bursts, and a single
            # percentage cannot tell those apart.
            txd=0; rxd=0
            [ -r "$stat_dir/tx_dropped" ] && read -r txd < "$stat_dir/tx_dropped"
            [ -r "$stat_dir/rx_dropped" ] && read -r rxd < "$stat_dir/rx_dropped"

            # Preemption of the forwarding process, and how much of it is
            # swapped out. Both are only meaningful as a series: a single
            # end-of-run VmSwap says the process was paged out, while the
            # series says whether it was paged out DURING the measurement.
            nvcs=0; vmswap=0; vmrss=0; nthr=0
            if [ -n "$srv_pid" ] && [ -r "/proc/$srv_pid/status" ]; then
                while read -r k v _; do
                    case "$k" in
                        nonvoluntary_ctxt_switches:) nvcs="$v" ;;
                        VmSwap:) vmswap="$v" ;;
                        VmRSS:)  vmrss="$v" ;;
                        Threads:) nthr="$v" ;;
                    esac
                done < "/proc/$srv_pid/status"
            fi

            printf "%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n" \
                "$now" "$u" "$n" "$s" "$idl" "$iow" "$irq" "$sirq" "$steal" \
                "$udp_in" "$udp_out" "$sndbuf" "$netrx0" "$netrx" \
                "$txp" "$rxp" "$txb" "$rxb" \
                "$rcvbuf" "$txd" "$rxd" "$nvcs" "$vmswap" "$vmrss" "$nthr" \
                >> "$csv"
            sleep "$interval"
        done
    ' _ "$_CB_SAMP_CSV" "$stat_dir" "$interval" "$srv_pid" &>/dev/null &
    _CB_SAMP_PID=$!
}

sampler_stop_quiet() {
    if [ -n "$_CB_SAMP_PID" ]; then
        kill "$_CB_SAMP_PID" 2>/dev/null || true
        wait "$_CB_SAMP_PID" 2>/dev/null || true
        _CB_SAMP_PID=""
    fi
}

# sampler_stop -> SAMPLED_TICKS / SAMPLED_STATUS / SAMPLED_JSON
#
# Differences the counters and reduces the series. Deltas, not absolutes: every
# counter here is cumulative since boot, and a cumulative number says nothing
# about the ten seconds under measurement.
sampler_stop() {
    [ "$CI_BENCH_SAMPLE" = "1" ] || { SAMPLED_STATUS=off; return 0; }

    # Ask the sampler how much CPU it used BEFORE reaping it, so the cost note
    # is measured rather than asserted. Fields 14-17 of /proc/PID/stat are
    # utime, stime, cutime, cstime; the `sleep` children are reaped by the loop
    # so they land in cutime/cstime.
    local samp_cpu="unknown"
    if [ -n "$_CB_SAMP_PID" ] && [ -r "/proc/${_CB_SAMP_PID}/stat" ]; then
        samp_cpu="$(awk '{print $14 + $15 + $16 + $17}' \
                    "/proc/${_CB_SAMP_PID}/stat" 2>/dev/null || echo unknown)"
    fi

    sampler_stop_quiet

    if [ -z "$_CB_SAMP_CSV" ] || [ ! -s "$_CB_SAMP_CSV" ]; then
        SAMPLED_STATUS=no_samples
        SAMPLED_JSON=',"sampler":"no_samples"'
        rm -f "$_CB_SAMP_CSV" 2>/dev/null || true
        _CB_SAMP_CSV=""
        return 0
    fi

    SAMPLED_JSON="$(python3 -c '
import json, os, sys

csv_path, samp_cpu, interval = sys.argv[1], sys.argv[2], float(sys.argv[3])

# Column order written by the tick loop above.
(T, U, N, S, IDL, IOW, IRQ, SIRQ, STEAL,
 UIN, UOUT, SNDB, NRX0, NRX, TXP, RXP, TXB, RXB,
 RCVB, TXD, RXD, NVCS, VMSWAP, VMRSS, NTHR) = range(25)
NCOL = 25

rows = []
with open(csv_path) as fh:
    for line in fh:
        p = line.strip().split(",")
        if len(p) != NCOL:
            continue
        try:
            rows.append([float(p[0])] + [int(x) for x in p[1:]])
        except ValueError:
            continue


def emit(d):
    print("," + ",".join(json.dumps(k) + ":" + json.dumps(v)
                         for k, v in d.items()))
    raise SystemExit(0)


if len(rows) < 2:
    # One tick cannot be differenced. Distinct from no_samples: the sampler did
    # run, the measurement was just shorter than the interval.
    emit({"sampler": "too_few_ticks", "samp_ticks": len(rows)})

first, last = rows[0], rows[-1]
span = last[T] - first[T]

out = {"sampler": "ok", "samp_ticks": len(rows),
       "samp_interval_s": interval, "samp_span_s": round(span, 2)}

# CPU: /proc/stat jiffies are summed across cores, so busy/(busy+idle) is
# already normalised and needs no nproc term.
busy = sum(last[i] - first[i] for i in (U, N, S, IRQ, SIRQ, STEAL))
idle = sum(last[i] - first[i] for i in (IDL, IOW))
tot = busy + idle
if tot > 0:
    out["samp_cpu_util_pct"] = round(100.0 * busy / tot, 1)
    out["samp_softirq_pct"] = round(100.0 * (last[SIRQ] - first[SIRQ]) / tot, 2)
    # Steal is recorded but must not be read as hypervisor contention: a
    # GitHub runner is itself a guest, so this is the runner being descheduled,
    # not the emulated VPS tier.
    out["samp_steal_pct"] = round(100.0 * (last[STEAL] - first[STEAL]) / tot, 2)

if span > 0:
    out["samp_net_rx_per_s"] = round((last[NRX] - first[NRX]) / span, 1)
    out["samp_tx_pps"] = round((last[TXP] - first[TXP]) / span, 1)
    out["samp_rx_pps"] = round((last[RXP] - first[RXP]) / span, 1)
    out["samp_udp_out_per_s"] = round((last[UOUT] - first[UOUT]) / span, 1)

# Share of NET_RX softirqs that landed on CPU0. This asserts the emulated
# profile matched the target ("NET_RX: CPU0"); it measures nothing by itself.
nrx_all = last[NRX] - first[NRX]
if nrx_all > 0:
    out["samp_net_rx_cpu0_share"] = round((last[NRX0] - first[NRX0]) / nrx_all, 3)

# SndbufErrors: the kernel refused a datagram because the socket buffer was
# full. A direct send-side-blocking signal, and absolute because one is already
# interesting -- a rate would hide that.
out["samp_sndbuf_errors"] = last[SNDB] - first[SNDB]
# RcvbufErrors: the receive counterpart. Parsed since the sampler was written
# and discarded until now; a production box that was dropping packets showed
# 441 of these, so it is the socket-level evidence that the reader could not
# keep up with the wire.
out["samp_rcvbuf_errors"] = last[RCVB] - first[RCVB]

# Interface drops during the measurement, and their shape.
#
# The absolute delta answers "did the box drop anything"; the series answers
# "was it steady or bursty", which a single percentage cannot. Field data on a
# real VPS showed 1.9% TX dropped that turned out to be six short bursts --
# the difference between a capacity problem and a scheduling one.
dtxd, drxd = last[TXD] - first[TXD], last[RXD] - first[RXD]
out["samp_tx_dropped"] = dtxd
out["samp_rx_dropped"] = drxd
dtxp_all = (last[TXP] - first[TXP]) + dtxd
if dtxp_all > 0:
    out["samp_tx_drop_pct"] = round(100.0 * dtxd / dtxp_all, 3)

# Preemption of the forwarding process. A single-threaded forwarder on one
# core stops forwarding entirely while it is off-CPU, and the TUN ring fills
# during the gap -- which is the mechanism behind a drop count, invisible in
# any throughput figure.
dnv = last[NVCS] - first[NVCS]
if last[NVCS] or first[NVCS]:
    out["samp_nonvol_ctxsw"] = dnv
    if span > 0:
        out["samp_nonvol_ctxsw_per_s"] = round(dnv / span, 1)
if last[NTHR]:
    out["samp_proc_threads"] = last[NTHR]
# Swap footprint at the end of the measurement, with the peak over it. A
# process paged out mid-measurement pays page-in latency on the forwarding
# path; the peak is what says it happened during, not before.
if last[VMSWAP] or last[VMRSS]:
    out["samp_vmswap_kb"] = last[VMSWAP]
    out["samp_vmrss_kb"] = last[VMRSS]
    out["samp_vmswap_peak_kb"] = max(r[VMSWAP] for r in rows)

# Bytes per wire packet, from the counters the interface itself keeps.
#
# This exists because two earlier attempts to compute it from the xquic
# per-path stats were both wrong, in the same way. get_status carries
# path_send_bytes/path_recv_bytes (xqc_app_bytes_send/recv) and
# path_pkt_send_count/path_pkt_recv_count (ctl_send_count/ctl_recv_count),
# and the two are NOT a matched pair: the byte counters accumulate only for
# STREAM|DATAGRAM frames -- the xquic header says so outright, "only accounts
# for stream and datagram packets" (xqc_send_ctl.h:142) -- while ctl_recv_count
# increments for every datagram received (xqc_send_ctl.c:1153). On an
# ACK-dominated reverse direction the numerator goes to nearly zero while the
# denominator counts every packet, which is how overhead_bytes_per_pkt_rx came
# back as 0.2 bytes per packet in run 34043133862. A packet cannot be under
# one byte. (The tx side read plausibly only by luck: a bulk sender puts a
# STREAM frame in nearly every packet, so its two counters happen to track.)
#
# tx_bytes/tx_packets on the veth have no such mismatch -- the kernel counts
# both for the same frames -- and they include the outer UDP/IP headers, which
# is what actually costs capacity. They measure the OUTER tunnel datagram, so
# they answer "what did one wire packet cost", not "what did the inner payload
# cost"; the ratio between the two needs an inner-side counter that still does
# not exist.
dtxb, dtxp = last[TXB] - first[TXB], last[TXP] - first[TXP]
drxb, drxp = last[RXB] - first[RXB], last[RXP] - first[RXP]
if dtxp > 0:
    out["samp_wire_bytes_per_pkt_tx"] = round(dtxb / dtxp, 1)
if drxp > 0:
    out["samp_wire_bytes_per_pkt_rx"] = round(drxb / drxp, 1)
if dtxp > 0 or drxp > 0:
    out["samp_wire_pkt_note"] = (
        "veth tx_bytes/tx_packets: outer datagram incl. UDP+IP headers, "
        "matched numerator and denominator")

# Per-tick wire throughput, the series the oscillation metric reads. Mbit/s to
# match every other throughput field in the artifact.
mbps, pps, drops, sirqs = [], [], [], []
for a, b in zip(rows, rows[1:]):
    dt = b[T] - a[T]
    if dt <= 0:
        continue
    mbps.append(round((b[TXB] - a[TXB]) * 8.0 / 1e6 / dt, 2))
    pps.append(round((b[TXP] - a[TXP]) / dt, 1))
    drops.append((b[TXD] - a[TXD]) + (b[RXD] - a[RXD]))
    # Softirq share per tick. The end-to-end average hides a spike, and a
    # spike is the thing a packet-handling-bound box does under a burst.
    tick_busy = sum(b[i] - a[i] for i in (U, N, S, IRQ, SIRQ, STEAL))
    tick_idle = sum(b[i] - a[i] for i in (IDL, IOW))
    tick_tot = tick_busy + tick_idle
    sirqs.append(round(100.0 * (b[SIRQ] - a[SIRQ]) / tick_tot, 2)
                 if tick_tot > 0 else 0.0)
if mbps:
    out["samp_tx_mbps_series"] = mbps
if pps:
    out["samp_tx_pps_series"] = pps
if any(drops):
    # Only emitted when something was dropped: an all-zero series on every
    # clean row would be noise, and its absence is unambiguous beside
    # samp_tx_dropped, which is always present.
    out["samp_drop_series"] = drops
    nz = [i for i, d in enumerate(drops) if d]
    # Bursts, not a rate: consecutive ticks with drops are one event.
    bursts = 1 + sum(1 for x, y in zip(nz, nz[1:]) if y - x > 1)
    out["samp_drop_bursts"] = bursts
    out["samp_drop_worst_tick"] = max(drops)
if sirqs:
    out["samp_softirq_series"] = sirqs
    out["samp_softirq_peak_pct"] = max(sirqs)

if samp_cpu != "unknown":
    try:
        hz = float(os.sysconf("SC_CLK_TCK"))
        secs = int(samp_cpu) / hz
        out["samp_cost_note"] = (
            "sampler used %.2fs cpu over %.1fs (%.2f%% of one core)"
            % (secs, span, (100.0 * secs / span) if span > 0 else 0.0))
    except (ValueError, OSError):
        pass

emit(out)
' "$_CB_SAMP_CSV" "$samp_cpu" "$CI_BENCH_SAMPLE_INTERVAL" 2>/dev/null \
        || echo ',"sampler":"parse_failed"')"

    SAMPLED_STATUS=ok
    SAMPLED_TICKS="$(wc -l < "$_CB_SAMP_CSV" 2>/dev/null || echo 0)"
    rm -f "$_CB_SAMP_CSV" 2>/dev/null || true
    _CB_SAMP_CSV=""
}

# Brace-less JSON fragment WITH a leading comma, matching collect_wlb_instr and
# collect_send_supply so the row builder can concatenate all three.
collect_sampler() {
    [ "$CI_BENCH_SAMPLE" = "1" ] || return 0
    printf '%s' "$SAMPLED_JSON"
}
