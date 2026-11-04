import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts/ci_benchmarks'))
from ci_bench_health import tcp_health, findings
from ci_bench_results import iperf_measurement, record_iperf, validate_core, InvalidMeasurement
from ci_bench_metrics import saturation


class HealthTests(unittest.TestCase):
    def test_udp_coverage_requires_all_repeats(self):
        doc = {'test': 'udp_sweep', 'repeats': 3, 'sweep_rates_mbps': [2, 5],
               'results': {c: {'points': [dict(rate=r, repeat=i, throughput=r,
                   loss_pct=0, jitter_ms=0, status='ok') for r in (2, 5)
                   for i in (1, 2, 3)]} for c in ('single', 'wlb', 'minrtt')}}
        validate_core(doc)
        for mutation in ('missing', 'duplicate', 'failed'):
            bad = copy.deepcopy(doc)
            rows = bad['results']['wlb']['points']
            if mutation == 'missing': rows.pop()
            elif mutation == 'duplicate': rows[-1] = copy.deepcopy(rows[0])
            else: rows[-1]['status'] = 'measurement_incomplete'
            with self.assertRaises(InvalidMeasurement): validate_core(bad)

    def test_capacity_requires_every_repeat_at_a_rate(self):
        points = [dict(rate=10, status='ok', loss_pct=0, throughput=10),
                  dict(rate=20, status='ok', loss_pct=0, throughput=20),
                  dict(rate=20, status='ok', loss_pct=8, throughput=18)]
        self.assertEqual(saturation(points)['saturation_mbps'], 10)
        points[-1].update(loss_pct=0, throughput=10)
        self.assertEqual(saturation(points)['saturation_mbps'], 10)

    def test_fair_and_starved_tcp_streams(self):
        doc = {'start': {'test_start': {'num_streams': 4}},
               'end': {'streams': [{'receiver': {'bits_per_second': v}}
                                   for v in [100, 100, 100, 100]]}}
        self.assertEqual(tcp_health(doc)['status'], 'pass')
        doc['end']['streams'][3]['receiver']['bits_per_second'] = 1
        self.assertEqual(tcp_health(doc)['status'], 'fail')
        doc['end']['streams'].pop()
        self.assertEqual(tcp_health(doc)['status'], 'fail')

    def test_completed_aggregate_can_fail_performance(self):
        doc = {'test': 'aggregate', 'results': {'wlb': [
            {'streams': 1, 'single_path_mbps': 100, 'multipath_mbps': 90}]}}
        self.assertFalse(findings(doc))
        doc['results']['wlb'][0]['multipath_mbps'] = 19
        self.assertTrue(findings(doc))

    def test_receiver_must_be_complete_and_consistent(self):
        doc = {'end': {'sum_received': {'seconds': 10, 'bytes': 12500000,
                                        'bits_per_second': 10000000}}}
        self.assertEqual(iperf_measurement(doc, requested=10)['receiver_mbps'], 10)
        for field, value in [('seconds', 9.7), ('bytes', 1), ('bits_per_second', 0)]:
            bad = copy.deepcopy(doc)
            bad['end']['sum_received'][field] = value
            with self.assertRaises(InvalidMeasurement):
                iperf_measurement(bad, requested=10)

    def test_server_error_with_zero_exit_is_not_success(self):
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory)/'sample.json'
            p.write_text(json.dumps({'end': {'sum_received': {
                'seconds': 10, 'bytes': 12500000, 'bits_per_second': 10000000}}}))
            Path(str(p)+'.server.log').write_text('iperf3: error - unable to receive results: Bad file descriptor\n')
            self.assertFalse(record_iperf(p, 10, 0, 0)['measurement_complete'])
