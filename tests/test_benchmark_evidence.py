# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
from pathlib import Path
import hashlib
import json
import sys
import tempfile
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts/ci_benchmarks'))
from ci_bench_iperf_evidence import finalize


class EvidenceTests(unittest.TestCase):
    def test_failure_archives_entire_streams_and_vpn_logs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            sample = root/'sample.json'
            sample.write_text(json.dumps(dict(_mqvpn=dict(measurement_complete=False,
                exit_code=1,server_exit_code=0,failure_reason='control socket failure'))))
            body = b'BEGIN\n' + b'full diagnostic line\n'*15000 + b'END-OF-FAILURE\n'
            for suffix in ('.client.stdout.log','.client.stderr.log','.server.log'):
                Path(str(sample)+suffix).write_bytes(body)
            vpn = root/'vpn.log'; vpn.write_bytes(body)
            output = root/'archive'
            finalize(sample,output,str(vpn),str(vpn))
            manifest = json.loads((output/'sample.json.evidence.json').read_text())
            self.assertEqual(manifest['copy_errors'],[])
            for suffix in ('.client.stdout.log','.client.stderr.log','.server.log',
                           '.vpn-client.full.log','.vpn-server.full.log'):
                name = sample.name+suffix
                self.assertEqual((output/name).read_bytes(),body)
                cell = next(f for f in manifest['files'] if f['name']==name)
                self.assertEqual(cell['bytes'],len(body))
                self.assertEqual(cell['sha256'],hashlib.sha256(body).hexdigest())

    def test_missing_vpn_log_is_explicit_not_silently_complete(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); sample = root/'sample.json'
            sample.write_text('{"_mqvpn":{"measurement_complete":false}}')
            finalize(sample,root/'out',str(root/'missing.log'))
            manifest = json.loads((root/'out/sample.json.evidence.json').read_text())
            self.assertEqual(manifest['copy_errors'][0]['role'],'client')
