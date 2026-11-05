# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Performance gates are separate from measurement validity. Fail closed."""
import json
import math
from pathlib import Path
import sys
from ci_bench_results import validate_core


def tcp_health(data):
    streams = data.get('end', {}).get('streams', [])
    expected = data.get('start', {}).get('test_start', {}).get('num_streams')
    if not isinstance(expected, int) or expected < 1 or len(streams) != expected:
        return {'status': 'fail', 'errors': ['missing_tcp_stream_evidence']}
    rates = [s.get('receiver', {}).get('bits_per_second') for s in streams]
    if any(isinstance(v, bool) or not isinstance(v, (int, float))
           or not math.isfinite(v) or v <= 0 for v in rates):
        return {'status': 'fail', 'errors': ['invalid_tcp_stream_rate']}
    jain = sum(rates)**2 / (expected * sum(v*v for v in rates))
    errors = [] if jain >= 0.8 else ['tcp_stream_fairness_below_0.8']
    return {'status': 'fail' if errors else 'pass', 'jain_index': jain,
            'receiver_stream_mbps': [v/1e6 for v in rates], 'errors': errors}


def findings(doc):
    errors = []
    def compare(label, value, baseline):
        if any(isinstance(v, bool) or not isinstance(v, (int, float))
               or not math.isfinite(v) or v <= 0 for v in (value, baseline)):
            errors.append(label + ': missing/invalid positive rate')
        elif value < baseline * 0.8:
            errors.append(label + ': below 80% of same-run single path')
    kind = doc.get('test')
    if kind == 'aggregate':
        for scheduler, rows in doc.get('results', {}).items():
            for row in rows:
                compare(f"{scheduler}/{row.get('streams')}", row.get('multipath_mbps'), row.get('single_path_mbps'))
    elif kind in ('multipath_scheduler', 'ntn'):
        for row in doc.get('scenarios', []):
            for scheduler in ('wlb', 'minrtt'):
                compare(f"{row.get('name')}/{scheduler}", row.get(scheduler+'_mbps'), row.get('single_mbps'))
    return errors


if __name__ == '__main__':
    errors = []
    seen = set()
    for path in Path(sys.argv[1]).glob('*.json'):
        try:
            doc = json.loads(path.read_text())
            seen.add(doc.get('test'))
            if doc.get('test') in {'aggregate', 'multipath_scheduler', 'ntn'}:
                validate_core(doc)
            errors.extend(str(path) + ': ' + e for e in findings(doc))
        except (ValueError, TypeError, KeyError) as exc:
            errors.append(str(path) + ': ' + str(exc))
    for missing in {'aggregate', 'multipath_scheduler', 'ntn'} - seen:
        errors.append('missing required benchmark: ' + missing)
    for error in errors:
        print('::error::' + error)
    sys.exit(bool(errors))
