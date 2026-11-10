# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Stress resource and fault quality evidence; missing monitoring fails."""
import argparse
import json
import math
from pathlib import Path
import sys


def fault_summary(rows, requested, abort_reason="", probe_text=None, expected_kinds=None):
    if requested < 1 or len(rows) > requested or any(r.get('cycle') != i+1 for i,r in enumerate(rows)):
        raise ValueError('invalid fault cycle manifest')
    quality = fault_quality(rows, probe_text, expected_kinds) if probe_text is not None else None
    quality_ok = {r['cycle']: r['status'] == 'pass' for r in quality['cycles']} if quality else {}
    failed = [r['cycle'] for r in rows if not r.get('ok') or (quality and not quality_ok[r['cycle']])]
    attempted = len(rows)
    result = dict(cycles_attempted=attempted, cycles_not_run=requested-attempted,
                first_failed_cycle=failed[0] if failed else None,
                execution_complete=attempted==requested and not abort_reason,
                aborted=bool(abort_reason), abort_reason=abort_reason or None)
    if quality is not None:
        result.update(fault_quality=quality, cycles_ok=attempted-len(failed),
                      cycles_failed=len(failed),
                      liveness_cycles_ok=sum(bool(r.get('ok')) for r in rows))
    return result


def _fault_number(value):
    if (isinstance(value, bool) or not isinstance(value, (int, float))
            or not math.isfinite(value) or value < 0):
        raise ValueError('invalid fault probe timing')
    return value


def _echo_window(probes, start, end):
    start, end = _fault_number(start), _fault_number(end)
    if end - start < 1:
        raise ValueError('fault probe window shorter than one second')
    selected = [p for p in probes if start <= p['sent_monotonic_s'] < end]
    sends = [p['sent_monotonic_s'] for p in selected]
    coverage = [p['sent_monotonic_s'] for p in probes
                if start - .15 <= p['sent_monotonic_s'] <= end + .15]
    # Bound coverage at both edges and throughout the window. A stalled
    # generator or dropped log record cannot masquerade as zero packet loss.
    if (not sends or not coverage or coverage[0] > start or coverage[-1] < end
            or abs(len(sends) - (end - start) * 10) > 2
            or sends[0] - start > .15 or end - sends[-1] > .15
            or any(b - a > .15 for a, b in zip(coverage, coverage[1:]))):
        raise ValueError('incomplete 10 Hz fault probe coverage')
    replies = sorted(p['monotonic_s'] for p in selected
                     if p['ok'] and start <= p['monotonic_s'] <= end)
    rtts = sorted(p['rtt_s'] for p in selected if p['ok'])
    lost = sum(not p['ok'] for p in selected)
    stable = None
    for i in range(len(selected) - 2):
        group = selected[i:i+3]
        if all(p['ok'] for p in group):
            stable = max(p['monotonic_s'] for p in group) - start
            break
    return dict(sent=len(selected), lost=lost, loss_percent=100 * lost / len(selected),
                max_echo_gap_s=max(b-a for a,b in zip([start]+replies, replies+[end])),
                rtt_p99_s=rtts[math.ceil(.99 * len(rtts))-1] if rtts else None,
                stable_reply_s=stable)


def fault_quality(rows, probe_text, expected_kinds=None):
    """Fixed application-service budgets, independent of path liveness."""
    fast = ('admin_down', 'carrier_loss')
    silent = ('blackhole', 'blackhole_up', 'blackhole_down')
    limits = {
        kind: dict(fault_cycle_loss_percent=5 if kind in fast else 10,
                   fault_kind_loss_percent=2 if kind in fast else 5,
                   fault_max_echo_gap_s=.5 if kind in fast else 1,
                   fault_rtt_p99_s=.3 if kind in fast else .5,
                   recovery_cycle_loss_percent=2, recovery_kind_loss_percent=1,
                   recovery_max_echo_gap_s=.5, recovery_rtt_p99_s=.3,
                   recovery_stable_reply_s=2)
        for kind in fast + silent}
    probes, probe_errors = [], []
    try:
        for line in probe_text.splitlines():
            if not line.strip():
                continue
            p = json.loads(line)
            sent = _fault_number(p['sent_monotonic_s'])
            received = _fault_number(p['monotonic_s'])
            if type(p['ok']) is not bool or received < sent:
                raise ValueError('invalid fault probe outcome')
            if p['ok']:
                rtt = _fault_number(p['rtt_s'])
                if rtt > 1 or abs(received - sent - rtt) > .01:
                    raise ValueError('inconsistent fault probe RTT')
            elif (received - sent < .999
                  or (p['rtt_s'] is not None and _fault_number(p['rtt_s']) <= 1)):
                raise ValueError('failed probe within response deadline')
            probes.append(p)
        probes.sort(key=lambda p: p['sent_monotonic_s'])
        if any(a['sent_monotonic_s'] == b['sent_monotonic_s']
               for a,b in zip(probes, probes[1:])):
            raise ValueError('duplicate fault probe sample')
    except (ValueError, KeyError, TypeError, OverflowError) as exc:
        probe_errors.append(str(exc))

    cycles = []
    previous_end = None
    for row in rows:
        measurement_errors, performance_errors = list(probe_errors), []
        cycle = dict(cycle=row['cycle'], path=row.get('path'), kind=row.get('kind'))
        kind = row.get('kind')
        try:
            if kind not in limits or row.get('path') not in ('A', 'B'):
                raise ValueError('missing or unknown fault kind/path')
            start, restored, end = (_fault_number(row.get(key)) for key in
                                    ('fault_start_s', 'recovery_start_s', 'recovery_end_s'))
            if not start < restored < end:
                raise ValueError('invalid fault/recovery window ordering')
            if previous_end is not None and start < previous_end:
                raise ValueError('overlapping fault cycle windows')
            previous_end = end
            for phase, a, b in (('fault', start, restored), ('recovery', restored, end)):
                metrics = _echo_window(probes, a, b)
                cycle[phase] = metrics
                budget = limits[kind]
                if metrics['loss_percent'] > budget[phase + '_cycle_loss_percent']:
                    performance_errors.append(phase + '_loss_budget_exceeded')
                if metrics['max_echo_gap_s'] > budget[phase + '_max_echo_gap_s'] + 1e-6:
                    performance_errors.append(phase + '_echo_gap_budget_exceeded')
                if metrics['rtt_p99_s'] is None or metrics['rtt_p99_s'] > budget[phase + '_rtt_p99_s']:
                    performance_errors.append(phase + '_rtt_budget_exceeded')
            stable = cycle['recovery']['stable_reply_s']
            if stable is None or stable > budget['recovery_stable_reply_s']:
                performance_errors.append('recovery_stable_reply_budget_exceeded')
        except (ValueError, TypeError, OverflowError) as exc:
            measurement_errors.append(str(exc))
        cycle.update(measurement_errors=measurement_errors, performance_errors=performance_errors,
                     status='fail' if measurement_errors or performance_errors else 'pass')
        cycles.append(cycle)

    kinds = {}
    for kind in sorted(set(expected_kinds or []) | {r.get('kind') for r in rows
                                                   if isinstance(r.get('kind'), str)}):
        selected = [r for r in cycles if r['kind'] == kind]
        errors = []
        if not selected or (expected_kinds is not None
                            and {r['path'] for r in selected} != {'A', 'B'}):
            errors.append('fault_kind_path_coverage_missing')
        phases = {}
        for phase in ('fault', 'recovery'):
            metrics = [r[phase] for r in selected if phase in r]
            sent = sum(r['sent'] for r in metrics)
            lost = sum(r['lost'] for r in metrics)
            loss = 100 * lost / sent if sent else None
            phases[phase] = dict(sent=sent, lost=lost, loss_percent=loss, cycles_measured=len(metrics),
                                worst_cycle_loss_percent=max((r['loss_percent'] for r in metrics), default=None),
                                worst_echo_gap_s=max((r['max_echo_gap_s'] for r in metrics), default=None),
                                worst_rtt_p99_s=max((r['rtt_p99_s'] for r in metrics
                                                     if r['rtt_p99_s'] is not None), default=None),
                                worst_stable_reply_s=max((r['stable_reply_s'] for r in metrics
                                                          if r['stable_reply_s'] is not None), default=None))
            if kind in limits and (loss is None or loss > limits[kind][phase + '_kind_loss_percent']):
                errors.append(phase + '_aggregate_loss_budget_exceeded')
        kinds[kind] = dict(cycles=len(selected), failed_cycles=sum(r['status'] != 'pass' for r in selected),
                           phases=phases, errors=errors)
    complete = bool(rows) and not probe_errors and all(not r['measurement_errors'] for r in cycles)
    complete = complete and all('fault_kind_path_coverage_missing' not in k['errors'] for k in kinds.values())
    passed = complete and all(r['status'] == 'pass' for r in cycles) and all(not k['errors'] for k in kinds.values())
    return dict(status='pass' if passed else 'fail', measurement_complete=complete,
                performance_health='inconclusive' if not complete else 'pass' if passed else 'regression',
                probe_pps=10, probe_deadline_s=1, timing_resolution_s=.1,
                limits=limits, measurement_errors=probe_errors, cycles=cycles, kinds=kinds)


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
