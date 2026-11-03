# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Bounded drain of an owned one-shot traffic server, preserving cleanup evidence."""
import argparse
import json
import math
import os
from pathlib import Path
import signal
import time


def identity(pid):
    try:
        fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        return None if fields[0] == 'Z' else fields[19]
    except (OSError, IndexError):
        return None


def drain(pid, grace=30, stop_grace=2):
    if pid <= 0 or not math.isfinite(grace) or not math.isfinite(stop_grace) or grace <= 0 or stop_grace <= 0:
        raise ValueError('positive owned pid and grace required')
    original = identity(pid)
    start = time.monotonic()
    def alive():
        return original is not None and identity(pid) == original
    while alive() and time.monotonic() - start < grace:
        time.sleep(.05)
    cleanup = alive()
    forced = False
    signals_sent = []
    if cleanup:
        try:
            os.kill(pid, signal.SIGTERM)
            signals_sent.append('SIGTERM')
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + stop_grace
        while alive() and time.monotonic() < deadline:
            time.sleep(.05)
        if alive():
            try:
                os.kill(pid, signal.SIGKILL)
                forced = True
                signals_sent.append('SIGKILL')
            except ProcessLookupError:
                pass
    return dict(pid=pid, cleanup_requested=cleanup, forced_kill=forced,
                signals_sent=signals_sent, grace_s=grace, stop_grace_s=stop_grace,
                drain_elapsed_s=time.monotonic()-start)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('pid', type=int)
    parser.add_argument('output', type=Path)
    parser.add_argument('--grace', type=float, default=30)
    parser.add_argument('--stop-grace', type=float, default=2)
    args = parser.parse_args()
    args.output.write_text(json.dumps(drain(args.pid,args.grace,args.stop_grace)) + '\n')
