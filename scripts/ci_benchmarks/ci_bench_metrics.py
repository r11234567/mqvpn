# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Protocol-specific measurements; missing evidence is never a clean sample."""
import argparse
import json
from pathlib import Path
import re
import sys

from ci_bench_results import InvalidMeasurement, iperf_measurement, number


def quic_transfer(text, expected_bytes, exit_code):
    if exit_code:
        raise InvalidMeasurement(f"picoquic exited {exit_code}")
    matches = re.findall(r"^Received (\d+) bytes in ([\d.]+) seconds,?\s+([\d.]+) Mbps\.?", text, re.M)
    if not matches:
        raise InvalidMeasurement("completed receiver summary missing")
    received, seconds, mbps = matches[-1]
    received, seconds, mbps = int(received), float(seconds), float(mbps)
    number(seconds, "QUIC duration", positive=True)
    number(mbps, "QUIC goodput", positive=True)
    # The connection summary includes H3 control/header bytes. The request
    # stream's FIN line identifies the completed object body independently.
    bodies = re.findall(r"^Stream 0 ended after (\d+) bytes\s*$", text, re.M)
    if bodies != [str(expected_bytes)] or received < expected_bytes:
        raise InvalidMeasurement(f"completed QUIC object body missing/wrong: {bodies}, expected {expected_bytes}")
    return {"receiver_bytes": expected_bytes, "reported_connection_bytes": received,
            "observed_duration_s": seconds,
            "receiver_mbps": expected_bytes * 8 / seconds / 1e6,
            "display_mbps": mbps, "status": "ok"}


def udp_point(raw, rate):
    sample = iperf_measurement(raw)
    summary = sample["receiver"]
    loss = number(summary.get("lost_percent"), "UDP loss")
    if loss > 100:
        raise InvalidMeasurement("UDP loss exceeds 100%")
    return {"rate": float(rate), "throughput": sample["receiver_mbps"],
            "loss_pct": loss, "jitter_ms": number(summary.get("jitter_ms"), "UDP jitter"),
            "receiver_bytes": sample["receiver_bytes"],
            "observed_duration_s": sample["observed_duration_s"], "status": "ok"}


def saturation(points, threshold=5):
    """Ascending first-crossing bound, not a claim of exact path capacity."""
    if not points or any(p.get("status") != "ok" for p in points):
        return {"saturation_mbps": None, "saturation_status": "incomplete"}
    last = None
    for p in sorted(points, key=lambda p: p["rate"]):
        if p["loss_pct"] >= threshold:
            return {"saturation_mbps": last, "saturation_status": "bracketed" if last is not None else "below_sweep",
                    "first_exceeded_mbps": p["rate"]}
        last = p["rate"]
    return {"saturation_mbps": last, "saturation_status": "above_sweep",
            "capacity_lower_bound_mbps": last}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("command", choices=("quic", "udp"))
    p.add_argument("file", type=Path)
    p.add_argument("--bytes", type=int, default=0)
    p.add_argument("--exit-code", type=int, default=0)
    p.add_argument("--rate", type=float, default=0)
    args = p.parse_args()
    try:
        if args.command == "quic":
            result = quic_transfer(args.file.read_text(errors="replace"), args.bytes, args.exit_code)
        else:
            result = udp_point(json.loads(args.file.read_text()), args.rate)
    except (OSError, ValueError, TypeError) as exc:
        result = {"status": "measurement_incomplete", "failure_reason": str(exc),
                  "rate": args.rate, "throughput": None, "loss_pct": None, "jitter_ms": None}
    if args.command == 'udp':
        # A failing rate cell must point directly to its unabridged artifact,
        # rather than require guessing among many randomly named raw files.
        result['raw_sample'] = args.file.name
        try:
            meta = json.loads(args.file.read_text()).get('_mqvpn') or {}
            result['evidence_manifest'] = meta.get('evidence_manifest')
        except (OSError, ValueError, AttributeError):
            result['evidence_manifest'] = None
    print(json.dumps(result, allow_nan=False))
    return int(result["status"] != "ok")


if __name__ == "__main__":
    sys.exit(main())
