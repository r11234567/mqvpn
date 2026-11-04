# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Expected coverage, completeness and performance health for netsim artifacts."""
import argparse
from collections import Counter
import datetime
import json
import math
import os
import statistics
from pathlib import Path
import sys

from ci_bench_results import InvalidMeasurement, number

CLASSES = ("homo_good homo_bad hetero_extreme asym_capacity premium_plus_mobile "
           "asym_latency one_flapping carrier_pair nat_split mtu_split "
           "home_plus_tether dual_mobile sat_plus_cell").split()
MTU = "mtu_1500 mtu_1400 mtu_split mtu_pps_1500 mtu_pps_1280 mtu_blackhole".split()
ACCESS = "eth wifi_good wifi_busy 5g_full 5g_half 5g_edge 5g_throttled starlink geo_sat tether_otg".split()
PAIRS = {"percommit", "classes", "combo", "mtu", "sched"}


def expected_rows(mode, scheduler="wlb", transit="bgp_junk"):
    if mode == "percommit":
        names = ["hetero_extreme"]
    elif mode == "classes":
        names = CLASSES
    elif mode == "paths":
        names = [f'paths_{n}' for n in (1,2,4,8)]
    elif mode == "mixed":
        names = ['homo_good', 'asym_capacity']
    elif mode == "mtu":
        names = MTU
    elif mode == "combo":
        names = [f"combo{i}" for i in range(10)]
    elif mode == "sched":
        return [{"scenario": c, "scheduler": s} for c in
                ("hetero_extreme", "asym_latency", "one_flapping")
                for s in ("wlb", "minrtt", "backup_fec")]
    elif mode == "quic":
        return [{"scenario": c, "scheduler": s} for c in
                ("homo_good", "asym_capacity", "hetero_extreme", "nat_split")
                for s in ("wlb", "wlb_udp_pin")]
    elif mode == "catalog":
        names = [f"{transit}+{a}" for a in ACCESS]
    elif mode == "tiers":
        names = [f"{t}+healthy" for t in ("vps_1c1g", "vps_1c1g_std", "vps_2c2g", "vps_2c2g_fast")]
        names += [f"vps_2c2g+{s}" for s in ("noisy_neighbour", "softirq_storm", "cpu_capped")]
    elif mode in {"game", "vps"}:
        tiers = (50, 100, 150, 200) if mode == "game" else (100, 200)
        names = [f"game_{t}_{p}pps" for t in tiers for p in (500, 2000)]
    elif mode == "special":
        names = ["nat_aging", "corrupt_reorder", "ack_starvation", "roam_under_load"]
    else:
        raise InvalidMeasurement(f"unknown netsim mode {mode!r}")
    return [{"scenario": n, **({"scheduler": scheduler} if mode in PAIRS else {})} for n in names]


def row_key(row, mode):
    return (row.get("scenario"), row.get("scheduler") if mode in PAIRS | {"quic"} else None)


def classify(row, mode):
    row = dict(row)
    row['findings'] = list(row.get('findings') or [])
    quic_regression = False
    row.setdefault('pmtud_requested', os.environ.get('CI_BENCH_PMTUD', 'default_off'))
    row.setdefault('direction', os.environ.get('CI_BENCH_DIRECTION', 'DL'))
    row.setdefault('game_profile', os.environ.get('CI_BENCH_GAME_PROFILE', 'default'))
    status = str(row.get("status", "missing_status"))
    parts = [part.split("=")[-1] for part in status.split()]
    valid_status = all(p in {"ok", "measured_zero", "measured_zero_both"} for p in parts)
    reasons = [] if valid_status else [status]
    harness = not any(p in {"setup_failed", "missing_row", "malformed_row", "duplicate_row",
                            "roam_not_applied", "picoquic_missing"} for p in parts)
    if not valid_status:
        # Keep valid solo measurements while removing fake zero placeholders.
        phase_keys = {"a": "solo_a_mbps", "b": "solo_b_mbps", "mp": "multipath_mbps"}
        phases = dict(p.split("=", 1) for p in status.split() if "=" in p)
        for phase, key in phase_keys.items():
            if phases.get(phase) not in (None, "ok", "measured_zero"):
                row[key] = None
        if not phases and mode != 'mixed':
            for key in list(row):
                if key.endswith("_mbps"):
                    row[key] = None

    required = []
    if mode in PAIRS:
        required = ["solo_a_mbps", "solo_b_mbps", "multipath_mbps"]
    elif mode == "catalog" or row.get("scenario") == "corrupt_reorder":
        required = ["single_path_mbps"]
    elif mode == "paths":
        required = ['single_dl_mbps','single_ul_mbps','multipath_dl_mbps','multipath_ul_mbps']
    elif mode == "mixed":
        required = ['tcp_mbps','quic_goodput_mbps','udp_dl_mbps','udp_ul_mbps','baseline_dl_mbps','baseline_ul_mbps']
        reasons.extend(row.get('mixed_errors') or [])
    elif mode == "tiers":
        required = ["multipath_mbps"]
    elif mode == "quic":
        required = ["quic_goodput_mbps"]
        if row.get("quic_status") != "ok":
            reasons.append(str(row.get("quic_status", "quic_status_missing")))
        if row.get("quic_status") == "picoquic_missing":
            harness = False
        attempts = row.get("quic_attempts", int(os.environ.get("CI_BENCH_QUIC_REPEATS") or 3))
        if isinstance(attempts, bool) or not isinstance(attempts, int) or attempts < 1:
            reasons.append('invalid_quic_attempt_manifest')
            attempts = 1
        baselines = row.get("quic_baselines") or []
        if not isinstance(baselines, list):
            baselines = []
        if len(baselines) != attempts * 3:
            reasons.append('incomplete_quic_baseline_manifest')
        for phase in ('direct_a', 'solo_a', 'solo_b'):
            cells = [p for p in baselines if isinstance(p, dict) and p.get('phase') == phase]
            expected = attempts
            if len(cells) != expected or any(p.get('status') != 'ok' or not isinstance(p.get('goodput_mbps'), (int,float)) or isinstance(p.get('goodput_mbps'), bool) or not math.isfinite(p.get('goodput_mbps', 0)) or p.get('goodput_mbps', 0) <= 0 for p in cells):
                reasons.append('incomplete_quic_baseline_' + phase)
            if any(p.get('status') in {'picoquic_missing','quic_server_failed'} for p in cells):
                harness = False
        if row.get("quic_samples", 0) != attempts or attempts < 1:
            reasons.append("incomplete_quic_repeats")
        mp_values = row.get('quic_goodput_all')
        if (not isinstance(mp_values, list) or len(mp_values) != attempts
                or any(isinstance(v, bool) or not isinstance(v, (int, float))
                       or not math.isfinite(v) or v < 0 for v in mp_values)):
            reasons.append('incomplete_quic_sample_manifest')
        if not reasons:
            # Derive summaries from retained samples. A declared CV of zero
            # or an optimistic median cannot hide a slow repeat or noisy solo.
            row['quic_goodput_mbps'] = statistics.median(mp_values)
            groups = {'mp': mp_values, **{
                phase: [p['goodput_mbps'] for p in baselines if p['phase'] == phase]
                for phase in ('direct_a', 'solo_a', 'solo_b')}}
            variability = {}
            for phase, values in groups.items():
                mean = statistics.mean(values)
                variability[phase] = (statistics.stdev(values) / mean * 100
                                      if len(values) > 1 and mean > 0 else 0)
                if variability[phase] > 25:
                    finding = f'quic_{phase}_repeat_variability_exceeds_25pct'
                    if finding not in row['findings']:
                        row['findings'].append(finding)
            row['quic_goodput_cv_pct'] = variability['mp']
            row['quic_baseline_cv_pct'] = {p: v for p, v in variability.items() if p != 'mp'}
            if attempts < 3 and 'quic_requires_at_least_3_repeats' not in row['findings']:
                row['findings'].append('quic_requires_at_least_3_repeats')
        # Recompute from same-protocol baseline evidence; a completed object
        # alone is not a performance pass. Keep incomplete comparisons unknown.
        if not reasons:
            solo = {phase: statistics.median(p['goodput_mbps'] for p in baselines
                                            if p['phase'] == phase)
                    for phase in ('solo_a', 'solo_b')}
            try:
                rate = number(row.get('quic_goodput_mbps'), 'QUIC goodput')
                ratio = rate / max(solo.values())
                row['quic_vs_best_single'] = ratio
                policy = {'degraded_below_ratio': 0.8, 'regression_below_ratio': 0.5,
                          'min_repeats': 3, 'max_cv_pct': 25}
                row['quic_performance_policy'] = policy
                if ratio < policy['degraded_below_ratio']:
                    message = f'quic_mp_regression: {ratio:.3f}x best same-protocol solo'
                    if message not in row['findings']:
                        row['findings'].append(message)
                    # Severe degradation is a hard verdict only with repeated,
                    # stable MP AND solo measurements; noisy cells stay degraded.
                    mp_values = row.get('quic_goodput_all') or []
                    groups = [mp_values] + [[p['goodput_mbps'] for p in baselines
                                            if p['phase'] == phase]
                                           for phase in ('solo_a', 'solo_b')]
                    def stable(values):
                        return (len(values) == attempts and attempts >= policy['min_repeats']
                                and all(isinstance(v, (int, float)) and not isinstance(v, bool)
                                        and math.isfinite(v) and v > 0 for v in values)
                                and statistics.stdev(values) / statistics.mean(values) * 100
                                <= policy['max_cv_pct'])
                    quic_regression = ratio < policy['regression_below_ratio'] and all(map(stable, groups))
            except InvalidMeasurement:
                pass # Required numeric validation below records the actual error.
    elif mode in {"game", "vps"}:
        required = ["baseline_loss_pct", "tunnel_loss_pct", "pps_fidelity", "ggdl_received", "ggul_received"]
        if row.get("gamegen", "ok") != "ok":
            reasons.append("gamegen_failed")
        for d in ("ggdl", "ggul"):
            if row.get(d + "_status") != "ok" or row.get(d + "_rcvbuf_drops", 0):
                reasons.append(d + "_invalid")
                harness = False
    elif mode == "special":
        required = {"nat_aging": ["before_idle_mbps", "after_idle_mbps"],
                    "ack_starvation": ["dl_idle_mbps", "dl_under_ul_load_mbps"],
                    "roam_under_load": ["pre_roam_mbps", "post_roam_mbps"]}.get(row.get("scenario"), [])

    for key in required:
        try:
            number(row.get(key), key)
        except InvalidMeasurement as exc:
            reasons.append(str(exc))
    if row.get("tier_applied") == "no" or row.get("host_tier_applied") == "no":
        reasons.append("requested_host_tier_not_applied")
        harness = False
    zero = any(p.startswith("measured_zero") for p in parts)
    zero |= any(row.get(k) == 0 for k in required if k.endswith("mbps") or k.endswith("received"))
    if not reasons:
        for metric, value in list(row.items()):
            if (metric == 'cv_pct' or metric.endswith('_cv_pct')) and isinstance(value, (int, float)) and value > 25:
                finding = metric + ': repeat variability exceeds 25%'
                if finding not in row['findings']:
                    row['findings'].append(finding)
        if mode in PAIRS:
            best = max(row['solo_a_mbps'], row['solo_b_mbps'])
            if best > 0 and row['multipath_mbps'] < 0.8 * best:
                row['findings'].append('multipath_below_80pct_best_solo')
        if mode == 'special' and row.get('scenario') == 'ack_starvation':
            idle = row.get('dl_idle_mbps', 0)
            if idle > 0 and row['dl_under_ul_load_mbps'] < idle * 0.8:
                row['findings'].append('download_retention_below_80pct')
    health = "inconclusive" if reasons else "regression" if zero or quic_regression else "degraded" if row.get("findings") else "pass"
    row.update(harness_valid=harness, measurement_complete=not reasons,
               performance_health=health, measurement_errors=reasons)
    return row


def report(rows, mode, scheduler="wlb", transit="bgp_junk", execution_complete=False):
    expected = expected_rows(mode, scheduler, transit)
    expected_keys = {row_key(r, mode) for r in expected}
    grouped = {}
    errors = []
    for row in rows:
        key = row_key(row, mode)
        if key not in expected_keys:
            errors.append(f"unexpected row: {key}")
        grouped.setdefault(key, []).append(row)
    output = []
    for plan in expected:
        found = grouped.get(row_key(plan, mode), [])
        if not found:
            row = {**plan, "status": "missing_row"}
        elif len(found) != 1:
            row = {**plan, "status": "duplicate_row", "duplicate_count": len(found), "duplicate_records": found}
        else:
            row = found[0]
        output.append(classify(row, mode))
    counts = Counter(r["performance_health"] for r in output)
    harness = not errors and all(r["harness_valid"] for r in output)
    raw = os.environ.get('CI_BENCH_RAW_DIR')
    if raw and (Path(raw) / 'spike-errors.log').exists():
        errors.append('background_emulation_failed')
        harness = False
    measured = execution_complete and harness and all(r["measurement_complete"] for r in output)
    health = "inconclusive" if not measured else "regression" if counts["regression"] else "degraded" if counts["degraded"] else "pass"
    return {"expected_rows": expected, "expected_count": len(expected), "recorded_count": len(rows),
            "execution_complete": execution_complete, "complete": int(measured),
            "harness_valid": harness, "measurement_complete": measured,
            "performance_health": health, "coverage_errors": errors,
            "unexpected_results": [r for r in rows if row_key(r, mode) not in expected_keys],
            "health_counts": dict(counts), "results": output}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("command", choices=("emit", "gate"))
    p.add_argument("rows", type=Path)
    p.add_argument("output", type=Path)
    p.add_argument("--mode", required=True)
    p.add_argument("--scheduler", default="wlb")
    p.add_argument("--transit", default="bgp_junk")
    p.add_argument("--complete", type=int, default=0)
    p.add_argument("--seconds", type=int, default=10)
    p.add_argument("--streams", type=int, default=4)
    p.add_argument("--repeats", type=int, default=3)
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--pps", type=int, default=0)
    p.add_argument("--nat", type=int, default=0)
    p.add_argument("--icmp", type=int, default=0)
    args = p.parse_args()
    rows = []
    if args.rows.exists():
        for index, line in enumerate(args.rows.read_text().splitlines()):
            if line.strip():
                try:
                    def reject_constant(value):
                        raise ValueError(f"non-finite JSON value {value}")
                    row = json.loads(line, parse_constant=reject_constant)
                    if not isinstance(row, dict):
                        raise ValueError("not an object")
                    if not isinstance(row.get("scenario"), str):
                        raise ValueError("scenario is not a string")
                    if args.mode in PAIRS | {"quic"} and not isinstance(row.get("scheduler"), str):
                        raise ValueError("scheduler is not a string")
                    rows.append(row)
                except ValueError:
                    rows.append({"scenario": f"malformed_line_{index + 1}", "status": "malformed_row", "raw_line": line})
    doc = report(rows, args.mode, args.scheduler, args.transit, bool(args.complete))
    doc.update(test="netsim_" + ("catalog_" + args.transit if args.mode == "catalog" else args.mode),
               commit=os.environ.get("CI_BENCH_COMMIT", "unknown"),
               timestamp=datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
               pair_id=os.environ.get('CI_BENCH_PAIR_ID'),
               runner_name=os.environ.get('RUNNER_NAME'),
               direction=os.environ.get('CI_BENCH_DIRECTION', 'DL'),
               pmtud_requested=os.environ.get('CI_BENCH_PMTUD', 'default_off'),
               raw_evidence_dir=os.environ.get('CI_BENCH_RAW_DIR'),
               mode=args.mode, arm=os.environ.get("CI_BENCH_ARM") or "default",
               arm_reorder=os.environ.get("CI_BENCH_REORDER") or "default",
               iperf_sec=args.seconds, iperf_streams=args.streams, repeats=args.repeats,
               caps={"netem_seed": args.seed, "pps_police": args.pps, "nat": args.nat,
                     "nat_model": "linux_conntrack_outer_udp_mapping", "nat_filtering_verified": False,
                     "icmp_fragmentation_needed_drop": args.icmp})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.output.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(doc, indent=2, allow_nan=False) + "\n")
    temporary.replace(args.output)
    if args.command == "gate":
        print(f"expected={doc['expected_count']} recorded={doc['recorded_count']} "
              f"measurement_complete={doc['measurement_complete']} performance_health={doc['performance_health']}")
        for row in doc["results"]:
            if not row["measurement_complete"]:
                print(f"::error::{row['scenario']}: {row['measurement_errors']}")
            elif row['performance_health'] == 'regression':
                print(f"::error::{row['scenario']}: performance regression: {row.get('findings', [])}")
        if not doc["measurement_complete"] or doc["performance_health"] == "regression":
            return 1
        if doc["performance_health"] == "degraded":
            print("::warning::performance findings present; this is not a healthy-performance verdict")
            return int(os.environ.get("CI") == "true"
                       or os.environ.get("CI_BENCH_STRICT_PERF", "1") != "0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
