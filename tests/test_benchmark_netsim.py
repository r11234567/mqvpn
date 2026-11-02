# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest

DIRECTORY = Path(__file__).resolve().parents[1] / "scripts/ci_benchmarks"
sys.path.insert(0, str(DIRECTORY))
import ci_bench_netsim_results as netsim


def pair(name="hetero_extreme"):
    return {"scenario": name, "scheduler": "wlb", "status": "ok",
            "solo_a_mbps": 100, "solo_b_mbps": 50, "multipath_mbps": 120}


class NetsimTests(unittest.TestCase):
    def test_missing_setup_rows_cannot_disappear(self):
        doc = netsim.report([pair("homo_good")], "classes", execution_complete=True)
        self.assertEqual(doc["expected_count"], 13)
        self.assertEqual(len(doc["results"]), 13)
        self.assertFalse(doc["measurement_complete"])
        self.assertEqual(sum(r["status"] == "missing_row" for r in doc["results"]), 12)

    def test_no_rows_still_produces_full_manifest(self):
        doc = netsim.report([], "mtu", execution_complete=True)
        self.assertEqual(len(doc["results"]), 6)
        self.assertEqual(doc["complete"], 0)

    def test_duplicate_cannot_replace_missing(self):
        doc = netsim.report([pair(), pair()], "percommit", execution_complete=True)
        self.assertEqual(doc["results"][0]["status"], "duplicate_row")
        self.assertFalse(doc["harness_valid"])

    def test_quic_substatus_and_repeats_are_required(self):
        row = {"status": "ok", "quic_goodput_mbps": 100, "quic_status": "ok", "quic_samples": 3,
               "quic_baselines": [{"phase": p, "status": "ok", "goodput_mbps": 50}
                                  for p in ("direct_a", "solo_a", "solo_b") for _ in range(3)]}
        self.assertTrue(netsim.classify(row, "quic")["measurement_complete"])
        for status, count in (("quic_no_goodput", 0), ("ok", 1), ("quic_partial_failure", 2)):
            with self.subTest(status=status):
                bad = dict(row, quic_status=status, quic_samples=count)
                self.assertFalse(netsim.classify(bad, "quic")["measurement_complete"])

    def test_quic_same_protocol_baselines_are_mandatory(self):
        row = {"status": "ok", "quic_goodput_mbps": 100, "quic_status": "ok", "quic_samples": 3}
        self.assertFalse(netsim.classify(row, "quic")["measurement_complete"])

    def test_quic_repeat_count_is_self_describing_on_replay(self):
        row = {"status": "ok", "quic_goodput_mbps": 100, "quic_status": "ok", "quic_samples": 1,
               "quic_attempts": 1, "quic_baselines": [{"phase": p, "status": "ok", "goodput_mbps": 50}
                   for p in ("direct_a", "solo_a", "solo_b")]}
        self.assertTrue(netsim.classify(row, "quic")["measurement_complete"])
        row['quic_baselines'] = [None]
        self.assertFalse(netsim.classify(row, "quic")["measurement_complete"])

    def test_path_scaling_manifest(self):
        self.assertEqual([r['scenario'] for r in netsim.expected_rows('paths')],
                         ['paths_1', 'paths_2', 'paths_4', 'paths_8'])

    def test_invalid_phase_is_null_not_zero_and_ratios_are_inconclusive(self):
        row = dict(pair(), status="a=ok b=measurement_incomplete mp=ok", solo_b_mbps=0)
        out = netsim.classify(row, "classes")
        self.assertIsNone(out["solo_b_mbps"])
        self.assertEqual(out["solo_a_mbps"], 100)
        self.assertFalse(out["measurement_complete"])

    def test_valid_zero_is_a_performance_failure(self):
        row = dict(pair(), status="a=ok b=ok mp=measured_zero", multipath_mbps=0)
        out = netsim.classify(row, "classes")
        self.assertTrue(out["measurement_complete"])
        self.assertEqual(out["performance_health"], "regression")

    def test_partial_execution_never_complete(self):
        self.assertFalse(netsim.report([pair()], "percommit")["measurement_complete"])
        self.assertTrue(netsim.report([pair()], "percommit", execution_complete=True)["measurement_complete"])

    def test_findings_are_visible_health_not_silent_pass(self):
        row = dict(pair(), findings=["mp_regression: test"])
        doc = netsim.report([row], "percommit", execution_complete=True)
        self.assertTrue(doc["measurement_complete"])
        self.assertEqual(doc["performance_health"], "degraded")

    def test_scheduled_manifest_matches_119_existing_cells(self):
        modes = "classes combo special mtu sched tiers quic game vps".split()
        count = sum(len(netsim.expected_rows(m)) for m in modes)
        count += sum(len(netsim.expected_rows("catalog", transit=t)) for t in
                     ("iplc", "bgp_opt", "bgp_plain", "bgp_junk", "carrier_qos"))
        self.assertEqual(count, 119)

    def test_cli_gate_and_atomic_artifact_survive_malformed_rows(self):
        with tempfile.TemporaryDirectory() as folder:
            rows = Path(folder) / "rows.jsonl"
            output = Path(folder) / "result.json"
            rows.write_text(json.dumps(pair()) + '\n{"scenario": NaN}\n')
            result = subprocess.run([sys.executable, str(DIRECTORY / "ci_bench_netsim_results.py"),
                                     "gate", str(rows), str(output), "--mode", "percommit", "--complete", "1"],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 1, result.stderr)
            doc = json.loads(output.read_text())
            self.assertFalse(doc["harness_valid"])
            self.assertEqual(doc["results"][0]["multipath_mbps"], 120)
            self.assertEqual(doc["unexpected_results"][0]["status"], "malformed_row")


if __name__ == "__main__":
    unittest.main()
