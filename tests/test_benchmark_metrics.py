# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Exercise false-success and timing boundaries in the actual measurement code."""
from pathlib import Path
import json
import subprocess
import tempfile
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/ci_benchmarks'))
from ci_bench_metrics import quic_transfer, saturation, udp_point
from ci_bench_results import InvalidMeasurement
from ci_bench_failover_measure import interval, sustained, analyse
from ci_bench_ab_report import key_of
from ci_bench_mixed_results import summarise as mixed_summary


class ProtocolMetricsTests(unittest.TestCase):
    def test_mixed_load_requires_receiver_progress_and_quic_overlap(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ('baseline_dl','baseline_ul','udp_dl','udp_ul','tcp'):
                duration = 12 if name=='tcp' else 4
                raw = {'end':{'sum_received':{'seconds':duration,'bytes':1000000,'bits_per_second':2000000,
                                             'lost_percent':0,'jitter_ms':1}},
                       'intervals':[{'sum':{'start':i,'end':i+1,'seconds':1,'bits_per_second':2000000}} for i in range(duration)]}
                (root/(name+'.json')).write_text(json.dumps(raw))
                (root/(name+'.path')).write_text(name+'.json')
            (root/'udp-window.json').write_text('{"start_s":3,"end_s":8}')
            (root/'tcp-window.json').write_text('{"start_s":0,"end_s":14}')
            (root/'quic.jsonl').write_text('{"start_s":4,"end_s":6,"status":"ok","mbps":20}\n')
            self.assertEqual(mixed_summary(root,'homo_good')['status'],'ok')
            (root/'quic.jsonl').write_text('{"start_s":0,"end_s":1,"status":"ok","mbps":20}\n')
            self.assertIn('inner_quic_incomplete_during_udp',mixed_summary(root,'homo_good')['mixed_errors'])

    def test_report_renders_asymmetric_findings_quic_and_host_rows(self):
        script = Path(__file__).resolve().parents[1]/'scripts/ci_benchmarks/ci_bench_ab_report.py'
        with tempfile.TemporaryDirectory() as folder:
            for arm in ('default', 'alternate'):
                row = {'scenario':'homo_good','scheduler':'wlb','mode_family':'quic',
                       'quic_goodput_mbps':100, 'quic_samples':3, 'quic_status':'ok',
                       'samp_cpu_util_pct':10, 'findings':['test_warning: diagnostic'] if arm=='alternate' else []}
                (Path(folder)/(arm+'.json')).write_text(json.dumps({'mode':'quic','arm':arm,'results':[row],
                    'measurement_complete':arm=='default', 'performance_health':'pass' if arm=='default' else 'inconclusive'}))
            result = subprocess.run([sys.executable,'-X','utf8',str(script),folder],capture_output=True,text=True,encoding='utf-8')
            self.assertEqual(result.returncode,0,result.stderr)
            self.assertIn('test_warning',result.stdout)
            self.assertIn('inconclusive',result.stdout)

    def test_comparison_directions_cannot_overwrite_each_other(self):
        a = {'scenario':'mtu_split', 'scheduler':'wlb', 'direction':'DL'}
        b = dict(a, direction='UL')
        self.assertNotEqual(key_of('mtu', a), key_of('mtu', b))
        self.assertEqual(key_of('quic', a)[0], 'quic')

    def test_game_default_and_tuned_bundles_are_comparable(self):
        a = {'scenario':'game_50_500pps', 'scheduler':'wlb', 'direction':'DL'}
        b = dict(a, scheduler='wlb_udp_pin')
        self.assertEqual(key_of('game', a), key_of('game', b))

    def test_quic_complete_object_uses_received_bytes_and_elapsed(self):
        doc = quic_transfer('Stream 0 ended after 1000000 bytes\nReceived 1000062 bytes in 2.000 seconds, 4.000 Mbps.\n', 1000000, 0)
        self.assertEqual(doc['receiver_mbps'], 4)
        self.assertEqual(doc['reported_connection_bytes'], 1000062)

    def test_quic_partial_timeout_and_upload_only_are_not_success(self):
        for log, count, rc in [
                ('Received 10 bytes in 1 seconds, 0.1 Mbps.', 100, 0),
                ('Received 100 bytes in 1 seconds, 0.1 Mbps.', 100, 124),
                ('Sent 100 bytes in 1 seconds, 0.1 Mbps.', 100, 0),
                ('Received 100 bytes in 0 seconds, 0.1 Mbps.', 100, 0)]:
            with self.subTest(log=log, rc=rc), self.assertRaises(InvalidMeasurement):
                quic_transfer(log, count, rc)

    def test_udp_offered_rate_never_becomes_delivered_rate(self):
        raw = {'end': {'sum_sent': {'seconds': 10, 'bytes': 125000000, 'bits_per_second': 100000000},
                       'sum_received': {'seconds': 10, 'bytes': 62500000, 'bits_per_second': 50000000,
                                        'lost_percent': 50, 'jitter_ms': 3}}}
        point = udp_point(raw, 100)
        self.assertEqual(point['throughput'], 50)
        self.assertEqual(point['loss_pct'], 50)
        del raw['end']['sum_received']
        with self.assertRaises(InvalidMeasurement): udp_point(raw, 100)

    def test_saturation_bounds_and_incomplete(self):
        good = [{'rate': n, 'status': 'ok', 'loss_pct': 0} for n in (10, 20)]
        self.assertEqual(saturation(good)['saturation_status'], 'above_sweep')
        self.assertEqual(saturation(good + [{'rate': 30, 'status': 'ok', 'loss_pct': 5}])['saturation_mbps'], 20)
        self.assertEqual(saturation([{'rate': 10, 'status': 'ok', 'loss_pct': 6}])['saturation_status'], 'below_sweep')
        self.assertIsNone(saturation(good + [{'status': 'measurement_incomplete'}])['saturation_mbps'])

    def test_only_receiver_interval_not_summary_or_stream_is_parsed(self):
        self.assertEqual(interval('[SUM]   0.00-0.50 sec  6.00 MBytes  100 Mbits/sec')['mbps'], 100)
        self.assertEqual(interval('[SUM]   0.50-1.00 sec  6.00 MBytes  0.1 Gbits/sec')['mbps'], 100)
        self.assertIsNone(interval('[SUM] 0.00-100.00 sec 6 GBytes 100 Mbits/sec receiver'))
        self.assertIsNone(interval('[ 5] 0.00-0.50 sec 6 MBytes 100 Mbits/sec'))

    def test_threshold_requires_sustained_delivery_and_no_sample_gap(self):
        rows = [{'monotonic_end_s': i/2, 'mbps': 100} for i in range(12)]
        self.assertEqual(sustained(rows, 1, 4, 75), 0)
        self.assertIsNone(sustained(rows, 1, 2, 75))
        self.assertIsNone(sustained(rows[:2] + rows[9:], 0, 6, 75))

    def test_missing_fault_events_and_receiver_phases_fail(self):
        result = analyse([], [], [], 0, 100)
        self.assertEqual(result['status'], 'fail')
        self.assertIn('fault_or_recovery_not_applied', result['measurement_errors'])

    def test_measured_application_outage_is_regression_not_missing_measurement(self):
        samples = [{'monotonic_end_s': i/2, 'mbps': 100} for i in range(201)]
        events = [{'event': name, 'monotonic_s': t, 'exit_code': 0}
                  for name,t in [('a_down',20),('a_up',40),('b_down',55),('b_up',75)]]
        echoes = [{'monotonic_s': i/2, 'ok': False} for i in range(201)]
        result = analyse(samples, events, echoes, 0, 100)
        self.assertTrue(result['measurement_complete'])
        self.assertEqual(result['performance_health'], 'regression')
        self.assertEqual(result['measurement_errors'], [])
        for e in echoes: e['ok'] = True
        self.assertEqual(analyse(samples, events, echoes, 0, 100)['performance_health'], 'pass')


if __name__ == '__main__':
    unittest.main()
