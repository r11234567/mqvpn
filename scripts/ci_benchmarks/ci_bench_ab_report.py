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
    # Deterministic order with the control arm first, so `move` always reads
    # "against the control".
    seen = {a for _m, a, _r in rows}
    arms = [a for a in ("reorder_off", "default", "reorder_on") if a in seen]
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
    return 0


if __name__ == "__main__":
    sys.exit(main())
