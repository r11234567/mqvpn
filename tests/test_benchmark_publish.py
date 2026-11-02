# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Exercise the publisher with local executables, without contacting R2."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(os.name == 'posix' and shutil.which('bash'), 'publisher uses a POSIX shell')
class PublishTests(unittest.TestCase):
    def test_only_top_level_results_are_uploaded_even_with_unreadable_raw(self):
        script = Path(__file__).resolve().parents[1] / 'scripts/ci_publish/publish_to_r2.sh'
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            bindir = root/'bin'; bindir.mkdir()
            results = root/'ci_bench_results'; results.mkdir()
            (results/'result.json').write_text('{"test":"aggregate"}')
            (results/'raw').mkdir()
            raw = results/'raw/private.json'; raw.write_text('{"not":"public"}'); raw.chmod(0)
            aws = bindir/'aws'
            aws.write_text('''#!/usr/bin/env python3
import json, os, sys
args=sys.argv[1:]
with open(os.environ['AWS_FIXTURE_LOG'],'a') as f: f.write(json.dumps(args)+'\\n')
if args[:2]==['s3api','head-object']:
    print('404 Not Found',file=sys.stderr); sys.exit(1)
''')
            aws.chmod(0o700)
            git = bindir/'git'; git.write_text('#!/usr/bin/env bash\nprintf abc123\\n\n'); git.chmod(0o700)
            env = dict(os.environ, PATH=str(bindir)+os.pathsep+os.environ['PATH'],
                       AWS_FIXTURE_LOG=str(root/'calls.jsonl'), R2_ACCOUNT_ID='fixture',
                       R2_ACCESS_KEY_ID='fixture', R2_SECRET_ACCESS_KEY='fixture', R2_BUCKET='fixture')
            r = subprocess.run(['bash', str(script), 'perf-data/weekly', 'weekly'], cwd=root, env=env,
                               capture_output=True, text=True, timeout=15)
            self.assertEqual(r.returncode, 0, r.stdout+r.stderr)
            calls = [json.loads(line) for line in (root/'calls.jsonl').read_text().splitlines()]
            uploads = [c for c in calls if c[:2]==['s3','cp']]
            self.assertEqual(len(uploads), 2, calls)
            self.assertTrue(any(c[2].endswith('result.json') for c in uploads))
            self.assertFalse(any('raw/' in ' '.join(c) or '--recursive' in c for c in calls))
            raw.chmod(0o600)


if __name__ == '__main__':
    unittest.main()
