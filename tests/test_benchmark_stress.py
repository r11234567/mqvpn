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

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/ci_stress'))
from ci_stress_results import resources, fairness, fault_summary


class ResourceEvidenceTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'requires POSIX bash')
    def test_multi_client_rejects_server_only_error_and_missing_log(self):
        scripts = Path(__file__).resolve().parents[1] / 'scripts'
        runner = (scripts / 'ci_stress/ci_stress_multi_client.sh').read_text()
        # Exercise the runner's recording and traffic-counting statements.
        # The fixture keeps valid client JSON and a zero server exit status.
        start = runner.index('        python3 "${SCRIPT_DIR}/../ci_benchmarks/ci_bench_results.py" record "$sample"')
        end = runner.index('        python3 - "$sample" "$i" "$direction"', start)
        command = ('set -euo pipefail\ntraffic_ok=0\ntraffic_failed=0\n'
                   + runner[start:end]
                   + '\nprintf "%s %s\\n" "$traffic_ok" "$traffic_failed"\n')
        for direction in ('UL', 'DL'):
            for server_state in ('clean', 'error', 'missing'):
                with self.subTest(direction=direction, server_state=server_state), tempfile.TemporaryDirectory() as folder:
                    root = Path(folder)
                    sample = root / f'iperf-client-1-{direction}.json'
                    sample.write_text(json.dumps({'end': {'sum_received': {
                        'seconds': 10, 'bytes': 12500000,
                        'bits_per_second': 10000000}}}))
                    server_log = root / f'iperf-server-1-{direction}.log'
                    if server_state != 'missing':
                        server_log.write_text('iperf3: error - unable to receive results: Bad file descriptor\n'
                                              if server_state == 'error' else 'Server listening on 5301\n')
                    else:
                        Path(str(sample) + '.server.log').write_text('Unrelated default log\n')
                    observation = root / 'server-process.json'
                    observation.write_text(json.dumps(dict(cleanup_requested=False,
                                                          forced_kill=False,
                                                          signals_sent=[])))
                    environment = dict(os.environ, SCRIPT_DIR=str(scripts / 'ci_stress'),
                                       CI_STRESS_RAW_DIR=str(root), sample=str(sample),
                                       server_observation=str(observation), TRAFFIC_SEC='10',
                                       rc='0', server_rc='0', i='1', direction=direction)
                    result = subprocess.run(['bash', '-c', command], env=environment,
                                            capture_output=True, text=True, timeout=10)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    expected = '1 0' if server_state == 'clean' else '0 1'
                    self.assertEqual(result.stdout.splitlines()[-1], expected)
                    meta = json.loads(sample.read_text())['_mqvpn']
                    self.assertEqual(meta['measurement_complete'], server_state == 'clean')
                    self.assertEqual(meta['server_exit_code'], 0)
                    self.assertEqual(meta['server_log'], str(server_log))

    @unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'requires POSIX bash')
    def test_long_running_rejects_server_only_error_and_missing_log(self):
        scripts = Path(__file__).resolve().parents[1] / 'scripts'
        runner = (scripts / 'ci_stress/ci_stress_long_running.sh').read_text()
        start = runner.index('cp "$IPERF_JSON" "${IPERF_JSON}.client.stdout.log"')
        end = runner.index('ci_stress_monitor_stop', start)
        command = ('set -euo pipefail\nBUSINESS_FAILED=0\n' + runner[start:end]
                   + '\nprintf "%s\\n" "$BUSINESS_FAILED"\n')
        for server_state in ('clean', 'error', 'missing'):
            with self.subTest(server_state=server_state), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                sample = root / 'iperf.json'
                sample.write_text(json.dumps({'end': {'sum_received': {
                    'seconds': 10, 'bytes': 12500000, 'bits_per_second': 10000000}}}))
                if server_state != 'missing':
                    (root / 'iperf-server.log').write_text(
                        'iperf3: error - unable to receive results: Bad file descriptor\n'
                        if server_state == 'error' else 'Server listening on 5201\n')
                environment = dict(os.environ, SCRIPT_DIR=str(scripts / 'ci_stress'),
                                   CI_STRESS_RAW_DIR=str(root), IPERF_JSON=str(sample),
                                   DURATION='10', IPERF_EXIT='0')
                result = subprocess.run(['bash', '-c', command], env=environment,
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.splitlines()[-1],
                                 '0' if server_state == 'clean' else '1')
                meta = json.loads(sample.read_text())['_mqvpn']
                self.assertEqual(meta['measurement_complete'], server_state == 'clean')
                original = json.loads(Path(str(sample) + '.client.stdout.log').read_text())
                self.assertEqual(original['end']['sum_received']['bytes'], 12500000)
                self.assertNotIn('_mqvpn', original)

    @unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'requires POSIX bash')
    def test_interrupted_runner_emits_failed_incomplete_result(self):
        source = Path(__file__).resolve().parents[1] / 'scripts/ci_stress'
        for action, reason, code in [('kill -HUP $$', 'signal_HUP', 129),
                                     ('kill -TERM $$', 'signal_TERM', 143),
                                     ('exit 0', 'shell_exit_0', 1)]:
            with self.subTest(reason=reason), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                for name in ('ci_stress_failover.sh', 'ci_stress_results.py'):
                    shutil.copyfile(source / name, root / name)
                (root / 'ci_stress_env.sh').write_text('''
CI_STRESS_RESULTS="$PWD/results"
CI_STRESS_RAW_DIR="$CI_STRESS_RESULTS/raw"
CI_STRESS_COMMIT=interruption-fixture
MQVPN=unused
ci_stress_cleanup() { touch "$CI_STRESS_RESULTS/cleanup-ran"; }
ci_stress_check_deps() {
    mkdir -p "$CI_STRESS_RAW_DIR"
    echo '{"cycle":1,"ok":true}' > "$CI_STRESS_RAW_DIR/fault-cycles.jsonl"
    ''' + action + '''
}
''')
                result = subprocess.run(['bash', str(root / 'ci_stress_failover.sh')],
                                        cwd=root, capture_output=True, timeout=10)
                self.assertEqual(result.returncode, code, result.stderr.decode())
                files = list((root / 'results').glob('failover_storm_*.json'))
                self.assertEqual(len(files), 1)
                doc = json.loads(files[0].read_text())
                self.assertEqual(doc['status'], 'fail')
                self.assertEqual(doc['abort_reason'], reason)
                self.assertEqual(doc['cycles_attempted'], 1)
                self.assertEqual(doc['cycles_not_run'], 99)
                self.assertFalse(doc['execution_complete'])
                self.assertTrue((root / 'results/cleanup-ran').exists())

    def test_process_death_does_not_create_unattempted_fault_failures(self):
        rows = [dict(cycle=i,ok=i<21) for i in range(1,22)]
        result = fault_summary(rows,100,'vpn_client_exited_during_cycle_21')
        self.assertEqual(result['cycles_attempted'],21)
        self.assertEqual(result['cycles_not_run'],79)
        self.assertEqual(result['first_failed_cycle'],21)
        self.assertFalse(result['execution_complete'])
        self.assertTrue(result['aborted'])
        self.assertTrue(fault_summary([dict(cycle=1,ok=True)],1)['execution_complete'])
        with self.assertRaises(ValueError):
            fault_summary([dict(cycle=2,ok=True)],1)

    def test_fairness_preserves_incomplete_client_count(self):
        rows = [{'direction':'UL','receiver_mbps':2,'measurement_complete':True} for _ in range(3)]
        self.assertEqual(fairness(rows)['UL']['jain_index'],1)
        rows.append({'direction':'UL','measurement_complete':False})
        result = fairness(rows)['UL']
        self.assertEqual(result['clients_recorded'],4)
        self.assertEqual(result['clients_complete'],3)
        self.assertIsNone(result['jain_index'])

    def test_empty_short_and_gapped_monitor_fail(self):
        for text in ('', '0 100 10\n', '0 100 10\n10 100 10\n',
                     '0 100 10\n10 100 10\n100 100 10\n'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                resources(text)

    def test_invalid_and_duplicate_samples_fail(self):
        for text in ('0 100 10\n10 0 10\n20 100 10\n',
                     '0 100 10\n10 100 10\n10 100 10\n', 'junk\n'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                resources(text)

    def test_fd_leak_fails_even_under_asan(self):
        doc = resources('0 100 10\n10 200 10\n20 300 11\n', asan=True)
        self.assertEqual(doc['status'], 'fail')
        self.assertIn('fd_count_did_not_return', doc['errors'])

    def test_asan_growth_is_inconclusive_not_no_leak_claim(self):
        doc = resources('0 100 10\n10 200 10\n20 100000 10\n', asan=True)
        self.assertEqual(doc['memory_assessment'], 'inconclusive_asan')

    def test_post_warmup_growth_fails(self):
        text = ''.join(f'{i*10} {10000 if i < 5 else 100000} 10\n' for i in range(8))
        doc = resources(text)
        self.assertEqual(doc['status'], 'fail')
        self.assertGreater(doc['steady_rss_kb_per_min'], 0)

    def test_allocator_warmup_is_not_compared_to_startup(self):
        text = ''.join(f'{i*10} {100 if i < 2 else 60000} 10\n' for i in range(8))
        self.assertEqual(resources(text)['status'], 'pass')


if __name__ == '__main__':
    unittest.main()
