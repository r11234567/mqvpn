# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
from pathlib import Path
import os
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'scripts/ci_benchmarks'))
from ci_bench_process import drain


@unittest.skipUnless(sys.platform == 'linux', 'requires Linux process identity')
class ServerLifecycleTests(unittest.TestCase):
    def test_slow_normal_teardown_gets_more_than_two_seconds(self):
        child = subprocess.Popen([sys.executable,'-c','import time; time.sleep(2.2)'])
        try:
            result = drain(child.pid,3)
            self.assertFalse(result['cleanup_requested'])
            self.assertEqual(child.wait(),0)
        finally:
            if child.poll() is None: child.kill()
            child.wait()

    def test_one_shot_server_can_finish_without_being_killed(self):
        child = subprocess.Popen([sys.executable,'-c','import time; time.sleep(.1)'])
        try:
            result = drain(child.pid, 1)
            self.assertFalse(result['cleanup_requested'])
            self.assertEqual(child.wait(),0)
        finally:
            if child.poll() is None: child.kill()
            child.wait()

    def test_wedged_server_is_bounded_and_reported(self):
        read_fd, write_fd = os.pipe()
        child = subprocess.Popen([sys.executable,'-c',
            'import os,signal,time,sys; signal.signal(signal.SIGTERM,signal.SIG_IGN); '
            'os.write(int(sys.argv[1]),b"ready"); time.sleep(30)',str(write_fd)],pass_fds=(write_fd,))
        os.close(write_fd)
        try:
            self.assertEqual(os.read(read_fd,5),b'ready')
            result = drain(child.pid,.15,.15)
            self.assertTrue(result['cleanup_requested'])
            self.assertTrue(result['forced_kill'])
            self.assertEqual(child.wait(),-9)
        finally:
            os.close(read_fd)
            if child.poll() is None: child.kill()
            child.wait()
