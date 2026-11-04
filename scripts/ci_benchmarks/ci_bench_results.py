# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Measurement validity and result schemas for the shell benchmark harness.

Exit failures are evidence, never a zero-loss/zero-rate substitute. This module
uses only the standard library so the exact same validator runs locally and CI.
"""
import argparse
import json
import math
from pathlib import Path
import shutil
import sys


class InvalidMeasurement(ValueError):
    pass


def number(value, label, minimum=0, positive=False):
    if (isinstance(value, bool) or not isinstance(value, (int, float))
            or not math.isfinite(value) or value < minimum
            or (positive and value == 0)):
        raise InvalidMeasurement(f"{label}: invalid number {value!r}")
    return value


def receiver_summary(data):
    """Prefer explicit receiver records; never use sum_sent as goodput."""
    end = data.get("end") or {}
    received = end.get("sum_received")
    if isinstance(received, dict) and received:
        return received
    # Older iperf3 UDP JSON can expose a single receiver summary. A mixed
    # sender summary with loss fields must NOT provide receiver throughput.
    summary = end.get("sum") or {}
    if summary.get("sender") is False:
        return summary
    raise InvalidMeasurement("receiver summary missing or sender-only")


def iperf_measurement(data, requested=None, exit_code=None):
    if not isinstance(data, dict) or data.get("error"):
        raise InvalidMeasurement(f"iperf error: {data.get('error') if isinstance(data, dict) else 'not an object'}")
    meta = data.get("_mqvpn") or {}
    if exit_code is None:
        exit_code = meta.get("exit_code", 0)
    if exit_code != 0:
        raise InvalidMeasurement(f"iperf exited {exit_code}")
    if requested is None:
        requested = meta.get("requested_duration_s")
    summary = receiver_summary(data)
    seconds = number(summary.get("seconds"), "receiver seconds", positive=True)
    received = number(summary.get("bytes"), "receiver bytes", positive=True)
    bps = number(summary.get("bits_per_second"), "receiver bitrate", positive=True)
    if requested is not None:
        number(requested, "requested duration", positive=True)
        # Bound both relative and absolute shortfall: a percentage alone would
        # let a two-hour soak silently lose minutes of receiver coverage.
        if seconds < requested - min(requested * 0.02, 1.0):
            raise InvalidMeasurement(f"short transfer: {seconds:.3f}/{requested}s")
    if abs(bps - received * 8 / seconds) > max(1, bps * 0.02):
        raise InvalidMeasurement('receiver bytes/time disagree with bitrate')
    process = meta.get('server_process') or {}
    if process.get('forced_kill'):
        raise InvalidMeasurement('iperf server required SIGKILL after drain/stop deadlines')
    if meta.get('server_exit_code') not in (None, 0):
        # Only accept our observed SIGTERM of a still-live one-shot server
        # AFTER a successful client and complete receiver result. A natural
        # server error, other signal or missing receiver evidence still fails.
        expected_cleanup = (process.get('cleanup_requested') is True
                            and process.get('signals_sent') == ['SIGTERM']
                            and meta['server_exit_code'] in (1, 143, -15))
        if not expected_cleanup:
            raise InvalidMeasurement(f"iperf server exited {meta['server_exit_code']}")
    return {"receiver_mbps": bps / 1e6, "receiver_bytes": received,
            "observed_duration_s": seconds, "receiver": summary}


def record_iperf(path, duration, exit_code, server_exit_code=None, server_observation=None):
    try:
        def reject_constant(value):
            raise ValueError(f"non-finite JSON value {value}")
        data = json.loads(path.read_text(encoding="utf-8"), parse_constant=reject_constant)
        if not isinstance(data, dict):
            raise ValueError("JSON root is not an object")
    except (OSError, ValueError) as exc:
        # Preserve invalid text independently before replacing the document.
        raw = path.with_suffix(path.suffix + ".invalid.log")
        raw.write_bytes(path.read_bytes())
        data = {"error": f"invalid iperf JSON: {exc}", "invalid_output": str(raw)}
    server_log = Path(str(path) + '.server.log')
    if server_log.exists():
        errors = [line for line in server_log.read_text(errors='replace').splitlines()
                  if line.startswith('iperf3: error')]
        if errors:
            data['error'] = 'server reported: ' + '; '.join(errors)
    meta = {"requested_duration_s": duration, "exit_code": exit_code}
    if server_exit_code is not None:
        meta['server_exit_code'] = server_exit_code
    if server_observation is not None:
        meta['server_process'] = json.loads(server_observation.read_text())
    process = meta.get('server_process') or {}
    meta['server_teardown'] = ('forced_kill' if process.get('forced_kill') else
                               'harness_cleanup' if process.get('cleanup_requested') else
                               'natural_exit' if server_exit_code == 0 else
                               'server_error' if server_exit_code is not None else 'not_observed')
    data['_mqvpn'] = meta
    try:
        parsed = iperf_measurement(data, duration, exit_code)
        meta.update({k: v for k, v in parsed.items() if k != "receiver"})
        meta["measurement_complete"] = True
    except InvalidMeasurement as exc:
        meta.update(measurement_complete=False, failure_reason=str(exc))
    data["_mqvpn"] = meta
    path.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return meta


SCHEDULER_SCENARIOS = {"equal_paths", "asymmetric_bandwidth", "high_jitter",
                       "asymmetric_jitter", "lossy_path", "realistic_mixed",
                       "mobile_dual_lte", "mobile_wifi_lte"}
NTN_SCENARIOS = {"lte_leo_starlink", "lte_leo_high_orbit", "lte_geo", "wifi_leo", "dual_leo"}


def exact_keys(actual, expected, label):
    if set(actual) != set(expected):
        raise InvalidMeasurement(f"{label}: expected {sorted(expected)}, got {sorted(actual)}")


def validate_core(data):
    if data.get('measurement_complete') is False:
        raise InvalidMeasurement('benchmark explicitly incomplete')
    test = data.get("test")
    results = data.get("results")
    checked = 0

    def rate(obj, key):
        nonlocal checked
        number(obj.get(key), key, positive=True)
        checked += 1

    if test == "raw_throughput":
        directions = data.get("directions") or []
        if not directions or len(set(directions)) != len(directions) or not set(directions) <= {"DL", "UL"}:
            raise InvalidMeasurement("invalid direction manifest")
        exact_keys(results or {}, directions, "raw directions")
        for row in results.values():
            for key in ("direct_mbps", "single_path_mbps", "multipath_minrtt_mbps", "multipath_wlb_mbps"):
                rate(row, key)
    elif test == "aggregate":
        exact_keys(results or {}, {"wlb", "minrtt"}, "aggregate schedulers")
        for rows in results.values():
            if len(rows) != 4 or {r.get("streams") for r in rows} != {1, 4, 16, 64}:
                raise InvalidMeasurement("missing or duplicate aggregate stream cells")
            for row in rows:
                rate(row, "single_path_mbps")
                rate(row, "multipath_mbps")
    elif test in {"ntn", "multipath_scheduler"}:
        rows = data.get("scenarios") or []
        expected = NTN_SCENARIOS if test == "ntn" else SCHEDULER_SCENARIOS
        if len(rows) != len(expected):
            raise InvalidMeasurement("missing or duplicate scenario rows")
        exact_keys([r.get("name") for r in rows], expected, "scenario names")
        for row in rows:
            for key in ("single_mbps", "wlb_mbps", "minrtt_mbps"):
                rate(row, key)
    elif test == "udp_sweep":
        exact_keys(results or {}, {"single", "wlb", "minrtt"}, "UDP conditions")
        expected = data.get("sweep_rates_mbps") or []
        if not expected or len(set(expected)) != len(expected):
            raise InvalidMeasurement("invalid UDP rate manifest")
        repeats = data.get('repeats')
        if isinstance(repeats, bool) or not isinstance(repeats, int) or repeats < 3:
            raise InvalidMeasurement('UDP sweep requires at least three repeats')
        cells = {(r, i) for r in expected for i in range(1, repeats+1)}
        for condition in results.values():
            points = condition.get("points") or []
            if len(points) != len(cells) or {(p.get('rate'), p.get('repeat')) for p in points} != cells:
                raise InvalidMeasurement("UDP sweep missing/duplicate points")
            for row in points:
                if row.get('status') != 'ok':
                    raise InvalidMeasurement('UDP repeat did not complete')
                rate(row, "throughput")
                loss = number(row.get("loss_pct"), "loss_pct")
                if loss > 100:
                    raise InvalidMeasurement("loss over 100%")
                number(row.get("jitter_ms"), "jitter_ms")
    elif test == "backup_fec":
        rows = results or []
        expected = {(s, loss) for s in ("wlb", "backup_fec") for loss in (1, 3, 5, 10)}
        actual = {(r.get("scheduler"), r.get("loss_pct")) for r in rows}
        if len(rows) != len(expected) or actual != expected:
            raise InvalidMeasurement("FEC cells missing or duplicate")
        for row in rows:
            rate(row, "throughput_mbps_median")
    elif test == "failover":
        exact_keys(results or {}, {"wlb", "minrtt"}, "failover schedulers")
        for sched in results.values():
            rate(sched, "post_recover_avg_mbps")
            for fault in ("fault_a", "fault_b"):
                row = sched.get(fault) or {}
                for key in ("pre_fault_avg_mbps", "degraded_avg_mbps", "recovery_avg_mbps"):
                    rate(row, key)
                for key in ("ttf_sec", "ttr_sec"):
                    if row.get(key) is None and sched.get('performance_health') == 'regression':
                        continue  # valid censored recovery, separately failed by the performance gate
                    number(row.get(key), key)
    else:
        raise InvalidMeasurement(f"no schema for benchmark {test!r}")
    return checked


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("core", "record", "throughput", "udp"))
    parser.add_argument("file", type=Path)
    parser.add_argument("--duration", type=float)
    parser.add_argument("--exit-code", type=int, default=0)
    parser.add_argument("--archive", type=Path)
    parser.add_argument('--server-exit-code', type=int)
    parser.add_argument('--server-observation', type=Path)
    args = parser.parse_args()
    if args.command == "record":
        record_iperf(args.file, args.duration, args.exit_code, args.server_exit_code, args.server_observation)
        if args.archive:
            args.archive.mkdir(parents=True, exist_ok=True)
            for path in args.file.parent.glob(args.file.name + "*"):
                shutil.copy2(path, args.archive / path.name)
        return 0
    try:
        data = json.loads(args.file.read_text(encoding="utf-8"))
        if args.command == "core":
            checked = validate_core(data)
            print(f"validated {checked} required throughput measurements")
            if data.get('performance_health') == 'regression':
                raise InvalidMeasurement('valid measurement reports a performance regression')
        else:
            sample = iperf_measurement(data)
            if args.command == "throughput":
                print(f"{sample['receiver_mbps']:.3f}")
            else:
                receiver = sample["receiver"]
                loss = number(receiver.get("lost_percent"), "lost_percent")
                jitter = number(receiver.get("jitter_ms"), "jitter_ms")
                packets = number(receiver.get("packets"), "packets", positive=True)
                ooo = receiver.get("out_of_order")
                print(f"{loss:.3f} {jitter:.3f} {ooo if ooo is not None else 'NA'} {packets:.0f} {sample['receiver_mbps']:.3f}")
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print(f"invalid measurement: {exc}", file=sys.stderr)
        if args.command == "core":
            return 1
        print("NA NA NA NA NA" if args.command == "udp" else "NA")
    return 0


if __name__ == "__main__":
    sys.exit(main())
