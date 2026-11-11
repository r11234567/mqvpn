# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
from pathlib import Path
import os
import shutil
import stat
import subprocess
import tempfile
import unittest


class PmtudArtifactTests(unittest.TestCase):
    @unittest.skipUnless(os.name == 'posix' and all(shutil.which(tool) for tool in ('bash', 'install', 'find')),
                         'requires POSIX evidence tools')
    def test_archive_is_readable_and_excludes_ephemeral_private_key(self):
        script = (Path(__file__).resolve().parents[1]
                  / 'scripts/ci_e2e/run_pmtud_small_mtu_test.sh').read_text()
        start = script.index('    if [ -n "${MQVPN_E2E_LOG_DIR:-}" ]; then')
        end = script.index('\n    fi', start) + len('\n    fi')
        command = 'set -euo pipefail\numask 077\n' + script[start:end]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            work = root / 'work'
            output = root / 'artifacts/ipv4'
            work.mkdir(mode=0o700)
            output.mkdir(parents=True, mode=0o700)
            contents = {'client.log': b'complete client output\n',
                        'server.log': b'complete server output\n',
                        'link.fragments.pcap': b'\xd4\xc3\xb2\xa1\0\0packet evidence',
                        'config.ini': b'[Interface]\nPMTUD = true\n'}
            for name, data in contents.items():
                (work / name).write_bytes(data)
                (work / name).chmod(0o600)
            (work / 'server.key').write_bytes(b'ephemeral test private key')
            (work / 'server.key').chmod(0o600)
            environment = dict(os.environ, WORK_DIR=str(work), MQVPN_E2E_LOG_DIR=str(output))
            result = subprocess.run(['bash', '-c', command], env=environment,
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(stat.S_IMODE(output.stat().st_mode), 0o755)
            self.assertFalse((output / 'server.key').exists())
            for name, data in contents.items():
                self.assertEqual((output / name).read_bytes(), data)
                self.assertEqual(stat.S_IMODE((output / name).stat().st_mode), 0o644)


if __name__ == '__main__':
    unittest.main()
