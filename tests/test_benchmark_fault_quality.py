# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
from pathlib import Path
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1] / 'scripts/ci_stress'
sys.path.insert(0, str(SCRIPTS))
from ci_stress_results import fault_summary


class FaultQualityTests(unittest.TestCase):
    def setUp(self):
        self.rows = [dict(cycle=1, path='A', kind='admin_down', ok=True,
                          fault_start_s=101, recovery_start_s=121, recovery_end_s=131),
                     dict(cycle=2, path='B', kind='admin_down', ok=True,
                          fault_start_s=141, recovery_start_s=161, recovery_end_s=171)]
        self.probes = [dict(sent_monotonic_s=100 + i / 10,
                            monotonic_s=100 + i / 10 + .02, ok=True, rtt_s=.02)
                       for i in range(730)]

    def drop(self, times):
        for probe in self.probes:
            if probe['sent_monotonic_s'] in times:
                probe.update(ok=False, rtt_s=None,
                             monotonic_s=probe['sent_monotonic_s'] + 1.005)

    def result(self):
        return fault_summary(self.rows, 2, probe_text='\n'.join(map(json.dumps, self.probes)),
                             expected_kinds=['admin_down'])

    def test_clean_complete_and_loss_at_kind_boundary(self):
        self.assertEqual(self.result()['fault_quality']['status'], 'pass')
        self.drop({102, 104, 106, 108, 142, 144, 146, 148})
        result = self.result()
        self.assertEqual(result['fault_quality']['status'], 'pass')
        self.assertEqual(result['fault_quality']['kinds']['admin_down']['phases']['fault']['loss_percent'], 2)
        self.assertEqual(result['cycles_ok'], 2)

    def test_per_cycle_failure_cannot_be_averaged_away(self):
        self.drop({102 + i / 2 for i in range(10)})
        self.assertEqual(self.result()['fault_quality']['cycles'][0]['status'], 'pass')
        self.setUp()
        self.drop({102 + i / 2 for i in range(11)})
        result = self.result()
        self.assertTrue(result['fault_quality']['measurement_complete'])
        self.assertEqual(result['cycles_failed'], 1)
        self.assertEqual(result['first_failed_cycle'], 1)
        self.assertIn('fault_loss_budget_exceeded', result['fault_quality']['cycles'][0]['performance_errors'])
        self.assertEqual(result['liveness_cycles_ok'], 2)

    def test_kind_loss_is_stricter_than_individual_cycle(self):
        self.drop({102, 104, 106, 108, 110, 142, 144, 146, 148, 150})
        quality = self.result()['fault_quality']
        self.assertTrue(all(row['status'] == 'pass' for row in quality['cycles']))
        self.assertEqual(quality['status'], 'fail')
        self.assertIn('fault_aggregate_loss_budget_exceeded', quality['kinds']['admin_down']['errors'])

    def test_trailing_outage_and_latency_are_independent_limits(self):
        self.drop({120.4, 120.5, 120.6, 120.7, 120.8, 120.9})
        errors = self.result()['fault_quality']['cycles'][0]['performance_errors']
        self.assertIn('fault_echo_gap_budget_exceeded', errors)
        self.setUp()
        for probe in self.probes:
            probe.update(rtt_s=.3, monotonic_s=probe['sent_monotonic_s'] + .3)
        self.assertEqual(self.result()['fault_quality']['status'], 'pass')
        for probe in self.probes:
            probe.update(rtt_s=.301, monotonic_s=probe['sent_monotonic_s'] + .301)
        errors = self.result()['fault_quality']['cycles'][0]['performance_errors']
        self.assertIn('fault_rtt_budget_exceeded', errors)

    def test_silent_blackhole_uses_its_own_fixed_budget(self):
        for row in self.rows:
            row['kind'] = 'blackhole_up'
        self.drop({102 + i for i in range(10)} | {142 + i for i in range(10)})
        result = fault_summary(self.rows, 2, probe_text='\n'.join(map(json.dumps, self.probes)),
                               expected_kinds=['blackhole_up'])
        self.assertEqual(result['fault_quality']['status'], 'pass')
        self.assertEqual(result['fault_quality']['limits']['blackhole_up']['fault_kind_loss_percent'], 5)

    def test_recovery_outage_fails_even_when_final_ping_would_pass(self):
        self.drop({121 + i / 10 for i in range(22)})
        cycle = self.result()['fault_quality']['cycles'][0]
        self.assertIn('recovery_stable_reply_budget_exceeded', cycle['performance_errors'])
        self.assertIn('recovery_echo_gap_budget_exceeded', cycle['performance_errors'])

    def test_missing_edge_interior_and_duplicate_evidence_fail_closed(self):
        changes = [lambda p: [r for r in p if r['sent_monotonic_s'] != 110],
                   lambda p: [r for r in p if r['sent_monotonic_s'] != 101],
                   lambda p: [r for r in p if r['sent_monotonic_s'] != 120.9],
                   lambda p: p + [dict(p[100])]]
        for change in changes:
            with self.subTest(change=change):
                self.setUp()
                self.probes = change(self.probes)
                result = self.result()
                self.assertFalse(result['fault_quality']['measurement_complete'])
                self.assertEqual(result['fault_quality']['status'], 'fail')

    def test_malformed_nonfinite_and_inconsistent_rtt_fail_closed(self):
        for value in (float('nan'), -1, True, '0.02', 1000**1000):
            with self.subTest(value=value):
                self.setUp()
                self.probes[100]['rtt_s'] = value
                self.assertFalse(self.result()['fault_quality']['measurement_complete'])
        self.setUp()
        self.probes[100]['rtt_s'] = .8
        self.assertFalse(self.result()['fault_quality']['measurement_complete'])
        malformed = fault_summary(self.rows, 2, probe_text='not JSON', expected_kinds=['admin_down'])
        self.assertFalse(malformed['fault_quality']['measurement_complete'])

    def test_legacy_manifest_missing_kind_or_overlapping_windows_is_incomplete(self):
        self.rows[0].pop('fault_start_s')
        self.assertFalse(self.result()['fault_quality']['measurement_complete'])
        self.setUp()
        self.rows[1]['fault_start_s'] = 120
        self.assertFalse(self.result()['fault_quality']['measurement_complete'])
        self.setUp()
        result = fault_summary(self.rows[:1], 2, probe_text='\n'.join(map(json.dumps, self.probes)),
                               expected_kinds=['admin_down', 'carrier_loss'])
        self.assertFalse(result['fault_quality']['measurement_complete'])
        self.assertFalse(result['execution_complete'])

    @unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'requires POSIX bash')
    def test_ci_cannot_reduce_cycles_or_fault_coverage(self):
        cases = [('10', 'admin_down carrier_loss blackhole blackhole_up blackhole_down', 'CI requires'),
                 ('100', 'admin_down carrier_loss blackhole blackhole_up', 'CI requires'),
                 ('100', 'admin_down carrier_loss blackhole blackhole_up blackhole_up', 'duplicate')]
        for count, kinds, error in cases:
            with self.subTest(count=count, kinds=kinds):
                environment = dict(os.environ, CI='true', NUM_CYCLES=count, CI_STRESS_FAULT_KINDS=kinds)
                result = subprocess.run(['bash', str(SCRIPTS / 'ci_stress_failover.sh')],
                                        env=environment, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn(error, result.stderr)
        environment = dict(os.environ, CI='false', GITHUB_ACTIONS='true', NUM_CYCLES='10',
                           CI_STRESS_FAULT_KINDS='admin_down carrier_loss blackhole blackhole_up blackhole_down')
        result = subprocess.run(['bash', str(SCRIPTS / 'ci_stress_failover.sh')],
                                env=environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn('CI requires', result.stderr)

    @unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'requires POSIX bash')
    def test_runner_final_status_obeys_quality_and_retains_liveness(self):
        runner = (SCRIPTS / 'ci_stress_failover.sh').read_text()
        start = runner.index('python3 -c "\nimport json, sys')
        end = runner.index('# ── Archive the VPN logs', start)
        command = 'set -euo pipefail\n' + runner[start:end] + '\ntest "$STATUS" = pass\n'
        cases = [('clean', 'pass', 'pass'), ('regression', 'pass', 'fail'),
                 ('malformed', 'pass', 'fail'), ('clean', 'fail', 'fail')]
        for state, prior, expected in cases:
            with self.subTest(state=state, prior=prior), tempfile.TemporaryDirectory() as folder:
                self.setUp()
                if state == 'regression':
                    self.drop({102 + i / 2 for i in range(11)})
                root = Path(folder)
                (root / 'fault-cycles.jsonl').write_text('\n'.join(map(json.dumps, self.rows)))
                probe_text = 'not JSON' if state == 'malformed' else '\n'.join(map(json.dumps, self.probes))
                (root / 'fault-echo.jsonl').write_text(probe_text)
                environment = dict(os.environ, SCRIPT_DIR=str(SCRIPTS), NUM_CYCLES='2',
                                   CI_STRESS_RAW_DIR=str(root), ECHO_LOG=str(root / 'fault-echo.jsonl'),
                                   CI_STRESS_COMMIT='quality-fixture', TIMESTAMP='fixture',
                                   CYCLES_OK='2', CYCLES_FAILED='0', STATUS=prior, ABORT_REASON='',
                                   KIND_JSON=json.dumps({'admin_down': {'ok': 2, 'failed': 0}}),
                                   OUTPUT_FILE=str(root / 'result.json'))
                for prefix in ('SERVER', 'CLIENT'):
                    for metric in ('INITIAL', 'FINAL', 'MAX'):
                        environment[f'{prefix}_RSS_{metric}'] = '10000'
                # Bash arrays are local to the real runner. Define its exact
                # fixture selection before executing the extracted finalizer.
                result = subprocess.run(['bash', '-c', 'FAULT_KINDS=(admin_down)\n' + command],
                                        env=environment, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, int(expected != 'pass'), result.stderr)
                data = json.loads((root / 'result.json').read_text())
                self.assertEqual(data['status'], expected)
                self.assertEqual(data['liveness_cycles_ok'], 2)
                self.assertEqual(data['liveness_fault_kinds']['admin_down']['ok'], 2)
                self.assertIn('ok', data['fault_kinds']['admin_down'])
                self.assertIn('failed', data['fault_kinds']['admin_down'])


if __name__ == '__main__':
    unittest.main()
