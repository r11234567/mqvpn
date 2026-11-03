# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Adversarial measurement fixtures: metadata must never make a failed run pass."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/ci_benchmarks/ci_bench_results.py"
spec = importlib.util.spec_from_file_location("bench_results", SCRIPT)
bench = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = bench
spec.loader.exec_module(bench)


def tcp():
    return {"end": {"sum_sent": {"seconds": 10, "bytes": 999999999, "bits_per_second": 800000000},
                    "sum_received": {"seconds": 10, "bytes": 12500000, "bits_per_second": 10000000}}}


def aggregate():
    return {"test": "aggregate", "results": {
        s: [{"streams": n, "single_path_mbps": 100, "multipath_mbps": 80} for n in (1, 4, 16, 64)]
        for s in ("wlb", "minrtt")}}


class MeasurementTests(unittest.TestCase):
    def test_own_cleanup_does_not_invalidate_completed_receiver(self):
        for code in (1,143,-15):
            with self.subTest(code=code):
                data = tcp()
                data['_mqvpn'] = dict(exit_code=0, requested_duration_s=10, server_exit_code=code,
                    server_process=dict(cleanup_requested=True,forced_kill=False,signals_sent=['SIGTERM']))
                self.assertEqual(bench.iperf_measurement(data)['receiver_mbps'],10)
                for key,value in [('exit_code',1),('requested_duration_s',900)]:
                    bad = copy.deepcopy(data); bad['_mqvpn'][key] = value
                    with self.assertRaises(bench.InvalidMeasurement): bench.iperf_measurement(bad)
                for field,value in [('forced_kill',True),('signals_sent',[]),('cleanup_requested',False)]:
                    bad = copy.deepcopy(data); bad['_mqvpn']['server_process'][field] = value
                    with self.assertRaises(bench.InvalidMeasurement): bench.iperf_measurement(bad)

    def test_failed_server_cannot_pass_using_successful_client_summary(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'iperf.json'
            path.write_text(json.dumps(tcp()))
            observation = Path(folder)/'server.json'
            observation.write_text('{"cleanup_requested":false,"forced_kill":false}')
            meta = bench.record_iperf(path,10,0,1,observation)
            self.assertFalse(meta['measurement_complete'])
            self.assertEqual(meta['server_exit_code'],1)
            self.assertFalse(meta['server_process']['cleanup_requested'])
            with self.assertRaises(bench.InvalidMeasurement):
                bench.iperf_measurement(json.loads(path.read_text()))

    def test_receiver_not_sender(self):
        self.assertEqual(bench.iperf_measurement(tcp())["receiver_mbps"], 10)

    def test_nonzero_partial_transfer_is_invalid(self):
        with self.assertRaises(bench.InvalidMeasurement):
            bench.iperf_measurement(tcp(), requested=900)

    def test_timeout_and_error_reject_plausible_rates(self):
        for code in (1, 124, 137):
            with self.subTest(code=code), self.assertRaises(bench.InvalidMeasurement):
                bench.iperf_measurement(tcp(), exit_code=code)
        data = tcp()
        data["error"] = "control socket closed"
        with self.assertRaises(bench.InvalidMeasurement):
            bench.iperf_measurement(data)

    def test_sender_only_is_not_receiver(self):
        data = tcp()
        del data["end"]["sum_received"]
        data["end"]["sum"] = {"sender": True, "lost_percent": 0, "bits_per_second": 10000000}
        with self.assertRaises(bench.InvalidMeasurement):
            bench.iperf_measurement(data)

    def test_record_invalid_preserves_original(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "iperf.json"
            path.write_text("iperf3: connection refused\n")
            bench.record_iperf(path, 10, 1)
            data = json.loads(path.read_text())
            self.assertFalse(data["_mqvpn"]["measurement_complete"])
            self.assertEqual(path.with_suffix(".json.invalid.log").read_text(), "iperf3: connection refused\n")

    def test_nonfinite_bool_and_missing_rejected(self):
        for value in (None, True, float("nan"), float("inf"), -1):
            with self.subTest(value=value), self.assertRaises(bench.InvalidMeasurement):
                bench.number(value, "field")


class CoreSchemaTests(unittest.TestCase):
    def test_valid_regression_is_still_valid_measurement(self):
        self.assertEqual(bench.validate_core(aggregate()), 16)

    def test_all_zero_with_positive_stream_count_fails(self):
        data = aggregate()
        for rows in data["results"].values():
            for row in rows:
                row["single_path_mbps"] = row["multipath_mbps"] = 0
        with self.assertRaises(bench.InvalidMeasurement):
            bench.validate_core(data)

    def test_each_cell_required(self):
        for change in ("missing", "duplicate", "zero", "null"):
            data = aggregate()
            rows = data["results"]["wlb"]
            if change == "missing":
                rows.pop()
            elif change == "duplicate":
                rows[-1] = copy.deepcopy(rows[0])
            else:
                rows[-1]["multipath_mbps"] = 0 if change == "zero" else None
            with self.subTest(change=change), self.assertRaises(bench.InvalidMeasurement):
                bench.validate_core(data)

    def test_udp_offered_rate_cannot_pass_zero_delivery(self):
        data = {"test": "udp_sweep", "sweep_rates_mbps": [200], "results": {
            s: {"points": [{"rate": 200, "throughput": 0, "loss_pct": 0, "jitter_ms": 0}]}
            for s in ("single", "wlb", "minrtt")}}
        with self.assertRaises(bench.InvalidMeasurement):
            bench.validate_core(data)

    def test_unknown_schema_fails_closed(self):
        with self.assertRaises(bench.InvalidMeasurement):
            bench.validate_core({"test": "typo", "results": {"rate": 100}})


if __name__ == "__main__":
    unittest.main()
