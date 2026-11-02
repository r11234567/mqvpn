# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts/ci_stress'))
from ci_stress_results import resources, fairness


class ResourceEvidenceTests(unittest.TestCase):
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
