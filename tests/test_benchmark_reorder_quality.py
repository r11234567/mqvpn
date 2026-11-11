# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Reorder success must imply the intended UDP workload was delivered."""
import copy
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/ci_benchmarks/ci_bench_results.py'
spec = importlib.util.spec_from_file_location('reorder_results', SCRIPT)
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


def fixture():
    received = dict(sender=False, seconds=6.08, bytes=15_000_000,
                    bits_per_second=15_000_000 * 8 / 6.08,
                    packets=12500, lost_packets=0, lost_percent=0)
    client = {'end': {'sum_sent': dict(sender=True, seconds=6, bytes=15_000_000),
                      'sum_received': copy.deepcopy(received)}}
    server = {'end': {'sum_received': copy.deepcopy(received)}}
    return client, server


class ReorderQualityTests(unittest.TestCase):
    def test_complete_load_passes(self):
        client, server = fixture()
        self.assertGreater(bench.reorder_udp_measurement(client, server)['receiver_mbps'], 19)

    def test_reported_loss_boundary(self):
        for lost, passes in ((12, True), (13, False)):
            client, server = fixture()
            server['end']['sum_received'].update(lost_packets=lost,
                                                  lost_percent=lost * 100 / 12500)
            if passes:
                bench.reorder_udp_measurement(client, server)
            else:
                with self.assertRaises(bench.InvalidMeasurement):
                    bench.reorder_udp_measurement(client, server)

    def test_slow_but_complete_receiver_fails(self):
        client, server = fixture()
        for data in (client, server):
            data['end']['sum_received'].update(seconds=10, bits_per_second=12_000_000)
        with self.assertRaises(bench.InvalidMeasurement):
            bench.reorder_udp_measurement(client, server)

    def test_missing_tail_cannot_hide_behind_zero_loss(self):
        for ratio, passes in ((0.999, True), (0.9989, False)):
            client, server = fixture()
            for data in (client, server):
                summary = data['end']['sum_received']
                summary.update(bytes=15_000_000 * ratio,
                               bits_per_second=15_000_000 * ratio * 8 / 6.08)
            if passes:
                bench.reorder_udp_measurement(client, server)
            else:
                with self.assertRaises(bench.InvalidMeasurement):
                    bench.reorder_udp_measurement(client, server)

    def test_sender_underload_fails(self):
        client, server = fixture()
        client['end']['sum_sent']['bytes'] = 14_000_000
        with self.assertRaises(bench.InvalidMeasurement):
            bench.reorder_udp_measurement(client, server)

    def test_client_receiver_role_is_required(self):
        for value in (None, True, 0):
            client, server = fixture()
            client['end']['sum_received']['sender'] = value
            with self.subTest(value=value), self.assertRaises(bench.InvalidMeasurement):
                bench.reorder_udp_measurement(client, server)

    def test_server_disagreement_and_missing_fields_fail(self):
        for field in ('bytes', 'packets', 'lost_packets', 'lost_percent', 'sender'):
            client, server = fixture()
            del server['end']['sum_received'][field]
            with self.subTest(field=field), self.assertRaises(bench.InvalidMeasurement):
                bench.reorder_udp_measurement(client, server)
        client, server = fixture()
        client['end']['sum_received']['bytes'] -= 1200
        with self.assertRaises(bench.InvalidMeasurement):
            bench.reorder_udp_measurement(client, server)

    def test_bad_loss_counters_fail(self):
        for field, value in (('packets', True), ('packets', 12.5),
                             ('lost_packets', float('nan')),
                             ('lost_packets', 12600), ('lost_percent', -1),
                             ('lost_percent', 0.08)):
            client, server = fixture()
            server['end']['sum_received'][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(bench.InvalidMeasurement):
                bench.reorder_udp_measurement(client, server)

    def test_failure_and_short_report_fail_on_either_end(self):
        for index in (0, 1):
            reports = fixture()
            reports[index]['error'] = 'interrupted'
            with self.assertRaises(bench.InvalidMeasurement):
                bench.reorder_udp_measurement(*reports)
            reports = fixture()
            reports[index]['end']['sum_received']['seconds'] = 5
            with self.assertRaises(bench.InvalidMeasurement):
                bench.reorder_udp_measurement(*reports)


if __name__ == '__main__':
    unittest.main()
