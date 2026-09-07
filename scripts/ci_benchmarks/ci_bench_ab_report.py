#!/usr/bin/env python3
"""Compare the arms of a netsim A/B that ran inside one workflow dispatch.

Usage: ci_bench_ab_report.py <dir-of-netsim-json> [--baseline <dir>]

Reads every netsim results document under <dir> (recursively), groups the rows
by what identifies a measurement -- mode, scenario, scheduler -- and reports how
the metric moved between arms. Writes GitHub-flavoured markdown to stdout, which
the workflow appends to $GITHUB_STEP_SUMMARY.

Why the comparison lives in the run rather than in a person's head: these
numbers move several percent between runs on shared cloud vCPUs, so an arm
measured on Tuesday is not comparable with an arm measured on Wednesday. Two
arms measured in the same dispatch, on the same commit, are. Doing the
subtraction here also means the run states its own conclusion instead of leaving
a pile of JSON for someone to diff by hand.

Exit status is 0 whatever the numbers say. This reports; it does not gate. A
gate on these would leave the weekly permanently red (docs section 0.4 L).
"""

import argparse
import json
import os
import statistics
import sys

# A move has to clear this to be worth reading, in either direction.
#
# 40, not the 15 this started at. Run 33610604131 measured the floor directly:
# the `catalog` rows are single-path, so the reorder arm can only cost them an
# 8-byte stamp, yet the two arms disagreed by -45% to +71% with a 21.6% stdev,
# and 19 of 50 such rows moved more than 15%. A 15% threshold therefore called
# noise a finding on a third of a control group. Anything below roughly this
# figure needs more repeats, not more interpretation.
#
# The report recomputes the floor from each run's own control rows and prints
# it, so this constant can be checked rather than trusted.
MOVE_PCT = 40.0

# Modes whose rows are single-path measurements. Useful twice over: they need a
# different metric field, and because the configuration under test can barely
# affect them, their spread across arms is this run's own noise floor.
CONTROL_MODES = {"catalog"}

# Metric fields in preference order. A netsim row carries exactly one.
METRIC_FIELDS = ("multipath_mbps", "single_path_mbps", "throughput_mbps")


def load_rows(root):
    """Every row under root, tagged with the arm and mode of its document."""
    rows = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in sorted(filenames):
            if not fn.endswith(".json"):
                continue
            full = os.path.join(dirpath, fn)
            try:
                with open(full) as fh:
                    doc = json.load(fh)
            except (OSError, ValueError) as exc:
                print(f"<!-- skipped {full}: {exc} -->")
                continue
            # `mode` is what makes a document a netsim one. The artifact set
            # also carries the core benchmarks (aggregate, failover, ...),
            # which have a results[] of a different shape and no arm -- letting
            # those in invented a phantom `default` arm and a `?` mode.
            if not isinstance(doc, dict) or "results" not in doc:
                continue
            if not doc.get("mode"):
                continue
            mode = doc["mode"]
            doc_arm = doc.get("arm") or "default"
            for r in doc.get("results") or []:
                if not isinstance(r, dict):
                    continue
                # The row's own arm wins: it is stamped at measurement time,
                # while the document's is written at the end of the mode.
                rows.append((mode, r.get("arm") or doc_arm, r))
    return rows


def key_of(mode, row):
    """What identifies one measurement across arms."""
    return (mode, row.get("scenario") or "?", row.get("scheduler") or "")


def metric(row):
    """The throughput figure this row carries, whatever shape it is.

    Reading only multipath_mbps left every catalog and special row blank, which
    silently dropped the control group -- more than half the measurements -- out
    of the comparison.
    """
    for f in METRIC_FIELDS:
        v = row.get(f)
        if isinstance(v, (int, float)):
            return v
    return None


def fmt(v, spec="{:.1f}"):
    return spec.format(v) if isinstance(v, (int, float)) else "-"


def pct_move(old, new):
    if not isinstance(old, (int, float)) or not isinstance(new, (int, float)):
        return None
    if old == 0:
        return None
    return (new - old) / old * 100.0


def collect(rows):
    """key -> {arm: row}. Later rows win, which only matters if a mode reran."""
    out = {}
    for mode, arm, row in rows:
        out.setdefault(key_of(mode, row), {})[arm] = row
    return out


def emit_ab(by_key, arms):
    """The A/B table: one row per measurement, one column pair per arm."""
    base, *others = arms
    print(f"## A/B: `{base}` vs {', '.join('`' + a + '`' for a in others)}")
    print()
    print(
        "Multipath Mbps and vs_best_single, per measurement. `move` is the "
        f"change in multipath Mbps against `{base}`; rows are sorted worst "
        "first, and anything beyond ±{:.0f}% is called out below.".format(MOVE_PCT)
    )
    print()

    head = ["mode", "scenario", "sched"]
    for a in arms:
        head += [f"{a} Mbps", f"{a} vsb"]
    head += ["move"]
    print("| " + " | ".join(head) + " |")
    print("|" + "|".join(["---"] * len(head)) + "|")

    ranked = []
    for key, per_arm in by_key.items():
        if base not in per_arm:
            continue
        b = metric(per_arm[base])
        worst = None
        for a in others:
            if a in per_arm:
                m = pct_move(b, metric(per_arm[a]))
                if m is not None and (worst is None or m < worst):
                    worst = m
        ranked.append((worst if worst is not None else 0.0, key, per_arm))
    ranked.sort(key=lambda t: t[0])

    calls = []
    for move, key, per_arm in ranked:
        mode, scenario, sched = key
        cells = [mode, scenario, sched or "-"]
        for a in arms:
            r = per_arm.get(a) or {}
            cells += [fmt(metric(r)), fmt(r.get("vs_best_single"), "{:.3f}")]
        has_move = any(a in per_arm for a in others)
        cells += [f"{move:+.1f}%" if has_move else "-"]
        print("| " + " | ".join(cells) + " |")
        if has_move and abs(move) >= MOVE_PCT:
            calls.append((move, mode, scenario, sched))
    print()

    if calls:
        print(f"### Moved more than {MOVE_PCT:.0f}%")
        print()
        for move, mode, scenario, sched in calls:
            where = f"`{mode}/{scenario}" + (f"/{sched}" if sched else "") + "`"
            verb = "worse" if move < 0 else "better"
            print(f"- {where}: {move:+.1f}% ({verb} on the non-`{base}` arm)")
        print()
    else:
        print(
            f"No measurement moved more than {MOVE_PCT:.0f}% between arms — on "
            "this evidence the varied setting is not what drives these numbers."
        )
        print()


def emit_findings(by_key, arms):
    """Findings that appear on one arm but not another, which is the useful
    half: a finding present in both arms is not caused by the varied setting."""
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        sets = {a: set(per_arm[a].get("findings") or []) for a in arms if a in per_arm}
        if len(sets) < 2:
            continue
        names = {a: {f.split(":", 1)[0] for f in s} for a, s in sets.items()}
        common = set.intersection(*names.values())
        for a, ns in names.items():
            only = sorted(ns - common)
            if only:
                rows.append((mode, scenario, sched, a, only))
    if not rows:
        return
    print("## Findings that differ between arms")
    print()
    print(
        "A finding raised on both arms is not attributable to the varied "
        "setting, so only the asymmetric ones are listed."
    )
    print()
    print("| mode | scenario | sched | only on arm | findings |")
    print("|---|---|---|---|---|")
    for mode, scenario, sched, arm, only in sorted(rows):
        print(f"| {mode} | {scenario} | {sched or '-'} | `{arm}` | {', '.join(only)} |")
    print()


def emit_wlb(by_key, arms):
    """The scheduler's own counters, where they were collected."""
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        for a in arms:
            r = per_arm.get(a)
            if not r:
                continue
            state = r.get("wlb_instr")
            if state is None:
                continue
            rows.append((mode, scenario, sched, a, r, state))
    if not rows:
        return

    print("## WLB scheduler counters")
    print()
    states = {}
    for *_x, state in rows:
        states[state] = states.get(state, 0) + 1
    if set(states) - {"ok"}:
        print("Collection status across rows: "
              + ", ".join(f"`{k}` x{v}" for k, v in sorted(states.items())))
        print()
    ok = [t for t in rows if t[5] == "ok"]
    if not ok:
        print(
            "No row produced counters, so the pin/round question is still "
            "unanswered. `no_lines` means the log carried none: check that the "
            "scheduler is WLB and that the embedder forwards xquic's REPORT "
            "channel."
        )
        print()
        return

    print(
        "`pin_share` and `sched_share` are the minority path's share against "
        "the 1/n a balanced scheduler would give; `pkts/round` large means WRR "
        "rounds are not turning over, so the weights behind the split are "
        "stale; `weight_ratio` is the spread of the LATE weights."
    )
    print()
    print("| mode | scenario | sched | arm | pins | sched | pin_share | "
          "sched_share | pkts/round | weight_ratio |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for mode, scenario, sched, arm, r, _s in sorted(ok):
        print("| {} | {} | {} | `{}` | {} | {} | {} | {} | {} | {} |".format(
            mode, scenario, sched or "-", arm,
            r.get("wlb_pins"), r.get("wlb_sched"),
            fmt(r.get("wlb_pin_minshare"), "{:.3f}"),
            fmt(r.get("wlb_sched_minshare"), "{:.3f}"),
            fmt(r.get("wlb_pkts_per_round")),
            fmt(r.get("wlb_weight_ratio"), "{:.2f}")))
    print()


def emit_supply(by_key, arms):
    """Where the send side stopped, from xquic's |send_supply| counters.

    The one question the throughput columns cannot answer: when a row comes in
    below what its legs measure alone, was the send side short of packets, or
    was capacity available and left unused? Those have opposite remedies, and
    every artifact before this one recorded neither.
    """
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        for a in arms:
            r = per_arm.get(a)
            if not r:
                continue
            state = r.get("send_supply")
            if state is None:
                continue
            rows.append((mode, scenario, sched, a, r, state))
    if not rows:
        return

    print("## Send-side supply")
    print()
    states = {}
    for *_x, state in rows:
        states[state] = states.get(state, 0) + 1
    if set(states) - {"ok"}:
        print("Collection status across rows: "
              + ", ".join(f"`{k}` x{v}" for k, v in sorted(states.items())))
        print()
    ok = [t for t in rows if t[5] == "ok"]
    if not ok:
        print(
            "No row produced counters. `no_lines` means the log carried none: "
            "check that the embedder forwards xquic's REPORT channel and that "
            "the build is new enough to emit `|send_supply|`."
        )
        print()
        return

    print(
        "`drain` is the share of scheduling passes that emptied the send queue "
        "— near 1.0 means the paths were never the constraint and the limit is "
        "upstream of the scheduler. `clamp` is the share of the passes that "
        "*stopped* which stopped because mqvpn's own 8 MiB `so_sndbuf` ceiling "
        "refused a packet the path's congestion window would have taken — a "
        "configuration limit, not congestion. `backlog` is the mean depth left "
        "behind per stop, capped at 512 by xquic."
    )
    print()
    print(
        "`clamp` replaces a `headroom` column that read 0.000 in every row of "
        "runs 34026833126 and 34036912262: it re-asked the cwnd question the "
        "scheduler had just answered, so it could never fire."
    )
    print()
    print("| mode | scenario | sched | arm | verdict | drain | clamp | "
          "backlog | passes |")
    print("|---|---|---|---|---|---|---|---|---|")
    for mode, scenario, sched, arm, r, _s in sorted(ok):
        print("| {} | {} | {} | `{}` | {} | {} | {} | {} | {} |".format(
            mode, scenario, sched or "-", arm,
            r.get("supply_verdict") or "-",
            fmt(r.get("supply_drain_ratio"), "{:.3f}"),
            fmt(r.get("supply_clamp_share"), "{:.3f}"),
            fmt(r.get("supply_backlog_per_stop")),
            r.get("supply_passes")))
    print()

    verdicts = {}
    for *_x, r, _s in ok:
        v = r.get("supply_verdict")
        if v:
            verdicts[v] = verdicts.get(v, 0) + 1
    if verdicts:
        print("Verdicts: "
              + ", ".join(f"`{k}` x{v}" for k, v in sorted(verdicts.items())))
        print()


def emit_quic(by_key, arms):
    """Inner-QUIC rows: goodput, rate shape, encapsulation cost.

    Every other table in this report describes inner TCP. These rows are the
    only ones that say anything about the protocol mqvpn actually proxies, and
    the only ones where `wlb` and `wlb_udp_pin` can differ at all -- the two
    schedulers are identical for inner TCP (flow_sched.c:61 pins TCP either
    way), so a wlb/udp_pin comparison drawn from any other mode is comparing a
    thing with itself.
    """
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        for a in arms:
            r = per_arm.get(a)
            if r and r.get("mode_family") == "quic":
                rows.append((mode, scenario, sched, a, r))
    if not rows:
        return

    print("## Inner QUIC")
    print()
    print(
        "`goodput` is the inner H3 transfer rate (blank = the transfer did not "
        "complete; see `quic_status`). `shape` is the outer wire rate over "
        "time: `oscillating` means it swung at least 2x between its 10th and "
        "90th percentile AND the swing recurred at a detectable period, which "
        "is the signature of inner and outer congestion control backing off "
        "together. **The inner RTT is inferred from the outer series, not "
        "measured** -- no qlog is parsed. `B/pkt tx` and `B/pkt rx` are the "
        "veth's own tx_bytes/tx_packets and rx_bytes/rx_packets, so they "
        "include the outer UDP/IP headers and their numerator and denominator "
        "cover the same frames. The earlier version of these two columns "
        "divided xquic's STREAM|DATAGRAM byte counter by its all-packets "
        "counter and reported 0.2 bytes per packet on the reverse direction, "
        "which is impossible. There is still no wire/app ratio: `get_status` "
        "exposes no tun-side byte counter to divide by."
    )
    print()
    print("| scenario | sched | arm | goodput | n | cv% | samples | shape | "
          "ratio | period | B/pkt tx | B/pkt rx | status |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for _mode, scenario, sched, arm, r in sorted(rows, key=lambda t: t[:4]):
        samples = r.get("quic_goodput_all")
        print("| {} | {} | `{}` | {} | {} | {} | {} | {} | {} | {} | {} | {} "
              "| {} |".format(
                  scenario, sched or "-", arm,
                  fmt(r.get("quic_goodput_mbps")),
                  r.get("quic_samples") if r.get("quic_samples") is not None
                  else "-",
                  fmt(r.get("quic_goodput_cv_pct"), "{:.0f}"),
                  "/".join(f"{v:.0f}" for v in samples) if samples else "-",
                  r.get("osc_verdict") or "-",
                  fmt(r.get("osc_peak_trough_ratio"), "{:.2f}"),
                  r.get("osc_autocorr_period_s")
                  if r.get("osc_autocorr_period_s") else "-",
                  fmt(r.get("samp_wire_bytes_per_pkt_tx")),
                  fmt(r.get("samp_wire_bytes_per_pkt_rx")),
                  r.get("quic_status") or "-"))
    print()

    # The comparison this mode exists for. Same scenario, two schedulers, one
    # run -- so the 13% cross-run drift measured on this run's own control rows
    # does not enter it.
    pairs = {}
    for _mode, scenario, sched, arm, r in rows:
        g = r.get("quic_goodput_mbps")
        if isinstance(g, (int, float)):
            pairs.setdefault((scenario, arm), {})[sched] = r
    both = {k: v for k, v in pairs.items() if len(v) >= 2}
    if both:
        print("### `wlb` vs `wlb_udp_pin` on inner QUIC")
        print()
        print("Measured in one run, so this is a within-run comparison. This "
              "is the first table in the harness where the two schedulers can "
              "differ at all: they are defined apart only on inner UDP "
              "(`flow_sched.c:61`), and inner QUIC is UDP.")
        print()
        print("`n` and `cv%` are per cell. A large move backed by n=1, or by "
              "two cells whose own CV is of the same order as the move, is not "
              "yet a result.")
        print()
        print("| scenario | arm | wlb | n/cv% | wlb_udp_pin | n/cv% | "
              "udp_pin move |")
        print("|---|---|---|---|---|---|---|")

        def cell(r):
            if r is None:
                return "-", "-"
            return (fmt(r.get("quic_goodput_mbps")),
                    "{}/{}".format(
                        r.get("quic_samples")
                        if r.get("quic_samples") is not None else "-",
                        fmt(r.get("quic_goodput_cv_pct"), "{:.0f}")))

        for (scenario, arm), v in sorted(both.items()):
            rb, rp = v.get("wlb"), v.get("wlb_udp_pin")
            bg = rb.get("quic_goodput_mbps") if rb else None
            pg = rp.get("quic_goodput_mbps") if rp else None
            bv, bn = cell(rb)
            pv, pn = cell(rp)
            mv = pct_move(bg, pg)
            print("| {} | `{}` | {} | {} | {} | {} | {} |".format(
                scenario, arm, bv, bn, pv, pn,
                f"{mv:+.1f}%" if mv is not None else "-"))
        print()

        # A row where only one scheduler produced a number is the strongest
        # reading in this table and the one a goodput column cannot show: the
        # other scheduler did not merely go slower, it failed to move 20 MiB
        # inside the timeout. Naming them keeps that out of the "-" cells.
        incomplete = []
        for _mode, scenario, sched, arm, r in rows:
            st = r.get("quic_status")
            if st and st != "ok":
                incomplete.append((scenario, sched, arm, st))
        if incomplete:
            print("Cells that did not complete a transfer:")
            print()
            for scenario, sched, arm, st in sorted(incomplete):
                print(f"- `{scenario}` / `{sched}` / `{arm}`: {st}")
            print()
            print("`quic_no_goodput` means the 20 MiB transfer did not finish "
                  "inside `CI_BENCH_QUIC_TIMEOUT` (90 s default) — a severe "
                  "rate, not a crashed transfer.")
            print()


def emit_game(by_key, arms):
    """Game-proxy rows: latency cost and packet fidelity, not throughput.

    Throughput is deliberately absent. At 500-4000 packets per second of
    50-byte payload the offered load is 0.2-1.6 Mbit/s against emulated links
    of 100 Mbit/s, so a bandwidth figure here would only ever restate the
    offered rate. What binds is added latency and whether the packets arrived.
    """
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        for a in arms:
            r = per_arm.get(a)
            if r and r.get("mode_family") == "game":
                rows.append((r.get("rtt_tier_ms") or 0,
                             r.get("game_pps_tier") or 0, scenario, a, r))
    if not rows:
        return

    print("## Game proxy (single path, small packets)")
    print()
    print(
        "Single path, `wlb_udp_pin`, 50-byte payloads at a fixed packet rate. "
        "`added p99` is the tunnel's p99 jitter minus the SAME emulated tier "
        "measured without the tunnel, so it is a within-run difference and is "
        "not affected by the cross-run drift that makes absolute figures "
        "unreadable. Note it is a **jitter** percentile, not a per-packet RTT "
        "percentile -- iperf3 reports jitter, not a latency distribution. "
        "`fidelity` is packets delivered over packets offered; below 1.0 is a "
        "packet-handling limit, never a bandwidth one at these rates."
    )
    print()
    print("| RTT tier | pps | arm | base p99 | tun p99 | **added p99** | "
          "base loss | tun loss | ooo% | fidelity | B/pkt tx | sndbuf | "
          "status |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for rtt, pps, _scenario, arm, r in sorted(rows, key=lambda t: t[:4]):
        print("| {} ms | {} | `{}` | {} | {} | **{}** | {} | {} | {} | {} | "
              "{} | {} | {} |".format(
                  rtt, pps, arm,
                  fmt(r.get("baseline_jitter_p99_ms"), "{:.2f}"),
                  fmt(r.get("tunnel_jitter_p99_ms"), "{:.2f}"),
                  fmt(r.get("added_jitter_p99_ms"), "{:+.2f}"),
                  fmt(r.get("baseline_loss_pct"), "{:.2f}"),
                  fmt(r.get("tunnel_loss_pct"), "{:.2f}"),
                  fmt(r.get("out_of_order_pct"), "{:.3f}"),
                  fmt(r.get("pps_fidelity"), "{:.3f}"),
                  fmt(r.get("samp_wire_bytes_per_pkt_tx")),
                  r.get("samp_sndbuf_errors")
                  if r.get("samp_sndbuf_errors") is not None else "-",
                  r.get("status") or "-"))
    print()
    print(
        "`B/pkt tx` is the outer datagram's cost on the wire, from the veth's "
        "own tx_bytes/tx_packets -- headers included, numerator and "
        "denominator over the same frames. It is the encapsulated cost of "
        "carrying one small inner packet, which is the figure that matters "
        "for a game workload. It is NOT a ratio against the inner payload: "
        "no tun-side byte counter exists to form one."
    )
    print()

    # Whether the offload setting actually took, rather than whether it was
    # requested. gso_factor is the only thing that can tell the difference.
    req = [r for *_x, r in rows if r.get("offload_requested")]
    if req:
        bad = [r for r in req if r.get("offload_applied") == "no"]
        gsos = sorted({r.get("gso_factor") for r in req
                       if r.get("gso_factor") is not None})
        if bad:
            print(
                "**UDP offload was requested off and did not take.** "
                f"{len(bad)} of {len(req)} rows still show batching "
                "(`gso_factor` above 1.0), which means the `[Advanced]` block "
                "never reached the process. Arrival timing on those rows is "
                "not what the mode intended to measure."
            )
        else:
            print(
                "UDP offload off on these rows (`UdpGso=false UdpGro=false`), "
                "confirmed by `gso_factor` "
                + ", ".join(f"{g:.2f}" for g in gsos)
                + " -- one datagram per syscall, so no batch is held back "
                  "waiting to form. This matters for arrival timing: with "
                  "batching on, a receiver sees a group of packets land "
                  "together, and an inner protocol that infers loss from "
                  "gaps can read that as a stall."
            )
        print()

    # A reorder column that reads zero is ambiguous unless the engine's state
    # is stated beside it: mqvpn's reorder engine is off by default, so zero
    # can mean "nothing was reordered" or "nothing was counting".
    engines = {r.get("reorder_engine") or "unknown" for *_x, r in rows}
    print("Reorder engine state across these rows: "
          + ", ".join(f"`{e}`" for e in sorted(engines)) + ".")
    print()
    if all(r.get("out_of_order_pct") is None for *_x, r in rows):
        print(
            "**`ooo%` is empty because nothing measured it.** The parser looks "
            "for an `out_of_order` key in iperf3's JSON; iperf3 does not emit "
            "one -- reordering appears only in its verbose text output. The "
            "column was specified on the assumption that "
            "`end.sum.out_of_order` existed, without checking the schema, and "
            "read `null` in every row of every run since. It is left in place, "
            "empty and labelled, rather than quietly removed: reordering is "
            "the failure mode this mode exists to measure, and a generator "
            "that can report it is the outstanding work."
        )
        print()


def emit_drops(rows):
    """Where packets were lost, by interface.

    The table exists to separate three losses that a single end-to-end
    percentage cannot: the emulated network dropped it, the qdisc refused it,
    or the tunnel device's own ring overflowed while the forwarder was off-CPU.
    A production incident was diagnosed on exactly that split -- mqvpn0 TX
    dropped 1.9% against a qdisc that had dropped nothing -- and the harness
    could not previously see any of it.
    """
    # Any drop-related field is enough to render the row. Gating on one
    # specific key would silently drop rows whose tunnel never came up (no
    # tun_* at all) or whose sampler was off, and an empty table is more
    # honest than a missing one.
    keys = ("tun_srv_tx_dropped", "tun_srv_tx_drop_pct", "iface_tx_dropped",
            "iface_tx_drop_pct", "tun_cli_tx_drop_pct", "samp_tx_dropped")
    have = [t for t in rows
            if any(t[-1].get(k) is not None for k in keys)]
    if not have:
        return

    print("### Where packets were dropped")
    print()
    print(
        "`veth` is the emulated network's own interface; `TUN srv`/`TUN cli` "
        "are mqvpn's tunnel devices at each end. A drop on the TUN with a "
        "clean qdisc means the ring filled -- the forwarder was not reading "
        "fast enough -- rather than anything the network did. `bursts` counts "
        "runs of consecutive one-second ticks that carried a drop, so a steady "
        "loss and a few short stalls do not read alike."
    )
    print()
    print("| mode | scenario | arm | veth tx drop% | TUN srv tx drop% | "
          "TUN srv qdisc drops | TUN srv qlen | TUN cli tx drop% | "
          "bursts | worst tick |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for mode, scenario, arm, r in sorted(have, key=lambda t: t[:3]):
        print("| {} | {} | `{}` | {} | {} | {} | {} | {} | {} | {} |".format(
            mode, scenario, arm,
            fmt(r.get("iface_tx_drop_pct"), "{:.3f}"),
            fmt(r.get("tun_srv_tx_drop_pct"), "{:.3f}"),
            r.get("tun_srv_qdisc_drops")
            if r.get("tun_srv_qdisc_drops") is not None else "-",
            r.get("tun_srv_txqueuelen")
            if r.get("tun_srv_txqueuelen") is not None else "-",
            fmt(r.get("tun_cli_tx_drop_pct"), "{:.3f}"),
            r.get("samp_drop_bursts")
            if r.get("samp_drop_bursts") is not None else "-",
            r.get("samp_drop_worst_tick")
            if r.get("samp_drop_worst_tick") is not None else "-"))
    print()

    qlens = {t[-1].get("tun_srv_txqueuelen") for t in have
             if t[-1].get("tun_srv_txqueuelen") is not None}
    if qlens:
        print(
            "TUN `txqueuelen` is "
            + ", ".join(str(q) for q in sorted(qlens))
            + ". mqvpn never sets it (`src/platform/linux/tun.c` calls "
              "`TUNSETIFF` and stops), so this is the kernel default. It is "
              "the depth of the ring a drop count above lands in."
        )
        print()


def emit_proc(rows):
    """The forwarding process itself: threads, preemption, and swap."""
    have = [t for t in rows if t[-1].get("proc_state") == "ok"
            or t[-1].get("samp_proc_threads") is not None]
    if not have:
        return

    print("### The forwarding process")
    print()
    print(
        "`nonvol ctxsw/s` counts the times per second the kernel took the CPU "
        "away from mqvpn rather than mqvpn yielding it. On a single-threaded "
        "forwarder pinned to one core, each of those is an interval during "
        "which nothing is forwarded and the TUN ring fills -- which is the "
        "mechanism behind the drop table above, and is invisible in any "
        "throughput figure. `swap` is the process's own paged-out footprint: "
        "a forwarder that must fault pages back in before it can forward pays "
        "that latency on the packet path."
    )
    print()
    print("| mode | scenario | arm | threads | nonvol ctxsw/s | RSS MB | "
          "swap MB | swap peak MB |")
    print("|---|---|---|---|---|---|---|---|")

    def mb(kb):
        return "-" if kb is None else "{:.1f}".format(kb / 1024.0)

    for mode, scenario, arm, r in sorted(have, key=lambda t: t[:3]):
        print("| {} | {} | `{}` | {} | {} | {} | {} | {} |".format(
            mode, scenario, arm,
            r.get("proc_threads") or r.get("samp_proc_threads") or "-",
            fmt(r.get("samp_nonvol_ctxsw_per_s")),
            mb(r.get("proc_vmrss_kb")),
            mb(r.get("proc_vmswap_kb")),
            mb(r.get("samp_vmswap_peak_kb"))))
    print()

    notes = {t[-1].get("proc_swap_note") for t in have
             if t[-1].get("proc_swap_note")}
    for n in sorted(notes):
        print(f"- {n}")
    if notes:
        print()


def emit_vps(by_key, arms):
    """Constrained-host rows: CPU, softirq, and what was not emulated."""
    rows = []
    for (mode, scenario, sched), per_arm in by_key.items():
        for a in arms:
            r = per_arm.get(a)
            if r and r.get("samp_cpu_util_pct") is not None:
                rows.append((mode, scenario, a, r))
    if not rows:
        return

    print("## Host load")
    print()
    print(
        "Sampled once a second from `/proc/stat`, `/proc/softirqs` and the "
        "interface counters inside the server namespace. `NET_RX cpu0` is the "
        "share of receive softirqs that landed on CPU0 -- an assertion that "
        "the box matched the target profile, not a measurement of anything. "
        "`sndbuf`/`rcvbuf` count datagrams the kernel refused because the "
        "socket buffer was full, in each direction."
    )
    print()
    print(
        "**`cpu%` is not comparable between a tiered and an untiered row.** It "
        "comes from `/proc/stat`, which is not cpuset-aware, so a fully "
        "saturated single-CPU tier on a 4-vCPU runner reads `25%` -- the same "
        "figure an idle-ish untiered row shows. `softirq peak%` is the worst "
        "single tick rather than the average, because a packet-handling limit "
        "shows up as a spike that a 20-second mean erases."
    )
    print()
    print("| mode | scenario | arm | tier | load | cpu% | softirq% | "
          "softirq peak% | NET_RX cpu0 | tx pps | rxq/txq | sndbuf | rcvbuf |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for mode, scenario, arm, r in sorted(rows, key=lambda t: t[:3]):
        q = "{}/{}".format(r.get("host_rx_queues", "-"),
                           r.get("host_tx_queues", "-"))
        print("| {} | {} | `{}` | {} | {} | {} | {} | {} | {} | {} | {} | {} "
              "| {} |".format(
                  mode, scenario, arm, r.get("host_tier") or "-",
                  r.get("host_state") or "-",
                  fmt(r.get("samp_cpu_util_pct")),
                  fmt(r.get("samp_softirq_pct"), "{:.2f}"),
                  fmt(r.get("samp_softirq_peak_pct"), "{:.2f}"),
                  fmt(r.get("samp_net_rx_cpu0_share"), "{:.3f}"),
                  fmt(r.get("samp_tx_pps")),
                  q,
                  r.get("samp_sndbuf_errors")
                  if r.get("samp_sndbuf_errors") is not None else "-",
                  r.get("samp_rcvbuf_errors")
                  if r.get("samp_rcvbuf_errors") is not None else "-"))
    print()

    emit_drops(rows)
    emit_proc(rows)

    notes = {r.get("host_not_emulated") for *_x, r in rows
             if r.get("host_not_emulated")}
    for n in sorted(notes):
        print(f"**Not emulated:** {n}")
        print()

    costs = {r.get("samp_cost_note") for *_x, r in rows if r.get("samp_cost_note")}
    if costs:
        print("Sampler overhead, measured rather than assumed: "
              + "; ".join(sorted(costs)) + ".")
        print()


def emit_noise_floor(by_key, arms):
    """What this run can resolve, measured from the run itself.

    The control modes are single-path, so the configuration being varied can
    barely reach them; whatever spread they show between arms is the harness
    disagreeing with itself. Printing it next to the A/B is the difference
    between "reorder cost 20%" and "20% is inside this run's noise".

    Solo baselines get the same treatment. vs_best_single and
    aggregation_efficiency both divide by them, so an unstable baseline makes
    both of the gate-able ratios unstable -- run 33610604131 measured the same
    emulated leg at 24.4 and 108.2 Mbps in its two arms and duly published
    vs_best_single 2.103 on one of them.
    """
    if len(arms) < 2:
        return
    base = arms[0]

    ctl, solo = [], []
    for (mode, _sc, _sch), per_arm in by_key.items():
        if base not in per_arm:
            continue
        for a in arms[1:]:
            if a not in per_arm:
                continue
            if mode in CONTROL_MODES:
                m = pct_move(metric(per_arm[base]), metric(per_arm[a]))
                if m is not None:
                    ctl.append(abs(m))
            for leg in ("solo_a_mbps", "solo_b_mbps"):
                m = pct_move(per_arm[base].get(leg), per_arm[a].get(leg))
                if m is not None:
                    solo.append(abs(m))

    if not ctl and not solo:
        return

    print("## What this run can resolve")
    print()

    def line(label, vals, why):
        if not vals:
            return
        vals = sorted(vals)
        med = statistics.median(vals)
        p90 = vals[int(len(vals) * 0.9)] if len(vals) > 1 else vals[0]
        over = sum(1 for v in vals if v > MOVE_PCT)
        print(f"- **{label}** (n={len(vals)}): median |move| {med:.1f}%, "
              f"p90 {p90:.1f}%, max {vals[-1]:.1f}%; {over} exceeded the "
              f"{MOVE_PCT:.0f}% reporting threshold. {why}")

    line("control rows (single-path)", ctl,
         "The varied setting can barely touch these, so this is the floor.")
    line("solo baselines, same leg across arms", solo,
         "vs_best_single and aggregation_efficiency divide by these.")
    print()
    print(f"Treat a move below roughly {MOVE_PCT:.0f}% as unresolved at this "
          "repeat count rather than as a result. Raising "
          "`CI_BENCH_IPERF_SEC`/`REPEATS`, or the `iperf_streams` dispatch "
          "input, is what buys resolution — not re-reading the same rows.")
    print()


def emit_coverage(rows, by_key, arms):
    modes = sorted({m for m, _a, _r in rows})
    print("## Coverage")
    print()
    print(f"- modes: {len(modes)} — {', '.join(modes) if modes else '(none)'}")
    print(f"- arms: {', '.join('`' + a + '`' for a in arms) or '(none)'}")
    print(f"- measurements: {len(by_key)}")
    bad = sum(1 for _m, _a, r in rows if r.get("status") not in (None, "ok"))
    print(f"- rows whose status was not ok: {bad}")
    print()


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("directory")
    args = ap.parse_args(argv)

    rows = load_rows(args.directory)
    if not rows:
        print("No netsim results found — nothing to compare.")
        return 0

    by_key = collect(rows)
    # Deterministic order with the baseline arm first, so `move` always reads
    # "against the baseline". The baseline is whichever arm represents the
    # shipped default -- for a streams sweep that is the harness default of 4,
    # which is the whole point of comparing 16 against it.
    seen = {a for _m, a, _r in rows}
    preferred = ("default", "reorder_off", "streams4", "streams16", "streams64",
                 "reorder_on")
    arms = [a for a in preferred if a in seen]
    arms += sorted(seen - set(arms))

    emit_coverage(rows, by_key, arms)
    emit_noise_floor(by_key, arms)
    if len(arms) >= 2:
        emit_ab(by_key, arms)
        emit_findings(by_key, arms)
    else:
        print(f"Single arm (`{arms[0]}`) — no A/B to report. Pass two arms to "
              "the dispatch to get one.")
        print()
    emit_wlb(by_key, arms)
    emit_supply(by_key, arms)
    emit_quic(by_key, arms)
    emit_game(by_key, arms)
    emit_vps(by_key, arms)
    return 0


if __name__ == "__main__":
    sys.exit(main())
