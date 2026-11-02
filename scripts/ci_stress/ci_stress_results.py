# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Resource evidence for stress runs; missing monitoring is a failure."""
import argparse
import json
import math
from pathlib import Path
import sys


def fault_summary(rows, requested, abort_reason=""):
    if requested < 1 or len(rows) > requested or any(r.get('cycle') != i+1 for i,r in enumerate(rows)):
        raise ValueError('invalid fault cycle manifest')
    failed = [r['cycle'] for r in rows if not r.get('ok')]
    attempted = len(rows)
    return dict(cycles_attempted=attempted, cycles_not_run=requested-attempted,
                first_failed_cycle=failed[0] if failed else None,
                execution_complete=attempted==requested and not abort_reason,
                aborted=bool(abort_reason), abort_reason=abort_reason or None)


def fairness(rows):
    """Delivered per-client fairness; incomplete clients cannot disappear."""
    output = {}
    for direction in ('UL', 'DL'):
        selected = [r for r in rows if r.get('direction') == direction]
        rates = [r['receiver_mbps'] for r in selected
                 if r.get('measurement_complete') and isinstance(r.get('receiver_mbps'), (int,float))
                 and not isinstance(r['receiver_mbps'], bool) and math.isfinite(r['receiver_mbps'])
                 and r['receiver_mbps'] >= 0]
        square = sum(r*r for r in rates)
        output[direction] = {'clients_recorded': len(selected), 'clients_complete': len(rates),
                             'receiver_mbps_min': min(rates) if rates else None,
                             'receiver_mbps_max': max(rates) if rates else None,
                             'receiver_mbps_sum': sum(rates),
                             'jain_index': sum(rates)**2/(len(rates)*square)
                                 if len(rates)==len(selected) and square else None}
    return output


def resources(text, asan=False, interval=10, warmup=30):
    rows = []
    for line in text.splitlines():
        fields = line.split()
        if len(fields) != 3:
            raise ValueError("malformed monitor sample")
        ts, rss, fd = map(int, fields)
        if ts < 0 or rss <= 0 or fd <= 0 or (rows and ts <= rows[-1][0]):
            raise ValueError("invalid/non-increasing monitor sample")
        rows.append((ts, rss, fd))
    if len(rows) < 3:
        raise ValueError(f"insufficient monitor coverage: {len(rows)} samples")
    span = rows[-1][0] - rows[0][0]
    if span < 2 * interval or max(b[0] - a[0] for a, b in zip(rows, rows[1:])) > 3 * interval:
        raise ValueError("monitor time coverage too short or has gaps")
    steady = [r for r in rows if r[0] >= rows[0][0] + warmup]
    result = {"samples": len(rows), "span_s": span, "initial_kb": rows[0][1],
              "final_kb": rows[-1][1], "max_kb": max(r[1] for r in rows),
              "initial_fd": rows[0][2], "final_fd": rows[-1][2],
              "max_fd": max(r[2] for r in rows), "asan": asan,
              "memory_assessment": "inconclusive_asan" if asan else "insufficient_steady_state_samples",
              "errors": []}
    if rows[-1][2] > rows[0][2]:
        result["errors"].append("fd_count_did_not_return")
    if not asan and len(steady) >= 3:
        baseline = steady[0][1]
        growth = max(r[1] for r in steady) - baseline
        result["steady_growth_kb"] = growth
        # A ratio on a tiny freshly allocated process is not a leak test. Use
        # both a relative and absolute budget after warmup, and retain the curve.
        if growth > max(baseline * 0.5, 32 * 1024):
            result["errors"].append("rss_growth_budget_exceeded")
        result["memory_assessment"] = "growth_budget_exceeded" if result["errors"] else "within_growth_budget"
    if len(steady) >= 3:
        xs = [r[0] - steady[0][0] for r in steady]
        ys = [r[1] for r in steady]
        mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
        denominator = sum((x - mx) ** 2 for x in xs)
        result["steady_rss_kb_per_min"] = round(60 * sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denominator, 2)
    result["monitor_valid"] = True
    result["status"] = "fail" if result["errors"] else "pass"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path)
    parser.add_argument("--asan", type=int, default=0)
    parser.add_argument("--interval", type=int, default=10)
    parser.add_argument("--warmup", type=int, default=30)
    args = parser.parse_args()
    try:
        doc = resources(args.file.read_text(), bool(args.asan), args.interval, args.warmup)
    except (OSError, ValueError) as exc:
        doc = {"monitor_valid": False, "status": "fail", "errors": [str(exc)]}
    args.file.with_suffix(args.file.suffix + ".resources.json").write_text(json.dumps(doc, indent=2) + "\n")
    print(json.dumps(doc))
    return int(doc["status"] != "pass")


if __name__ == "__main__":
    sys.exit(main())
