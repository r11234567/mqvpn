# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Receiver interval failover measurement with monotonic fault events and echoes."""
import argparse
import json
from pathlib import Path
import re
import socket
import subprocess
import sys
import threading
import time


def interval(line):
    m = re.search(r"\[SUM\]\s+([\d.]+)-([\d.]+)\s+sec\s+.*?\s+([\d.]+)\s+([KMG]?)bits/sec", line)
    if not m:
        return None
    a, b, rate, unit = m.groups()
    a, b = float(a), float(b)
    if not 0 < b - a <= 0.8:  # exclude the final whole-transfer summary
        return None
    return {"iperf_start_s": a, "iperf_end_s": b,
            "mbps": float(rate) * {"": 1e-6, "K": .001, "M": 1, "G": 1000}[unit]}


def sustained(samples, start, end, threshold, seconds=1.5):
    first = None
    previous = None
    for r in samples:
        t = r['monotonic_end_s']
        if t < start or t > end:
            continue
        if r['mbps'] < threshold or (previous is not None and t - previous > 1):
            first = None
        if r['mbps'] >= threshold:
            first = t if first is None else first
            if t - first >= seconds:
                return round(first - start, 3)
        previous = t
    return None


def analyse(samples, events, echoes, origin, ended):
    result = {"receiver_intervals": samples, "events": events, "echoes": echoes,
              "measurement_version": 2, "receiver_direction": "DL",
              "interval_seconds": .5, "sustained_window_seconds": 1.5,
              "echo_deadline_s": 1.0, "echo_offered_pps": 10,
              "timing_method": "monotonic event time vs flushed receiver interval arrival",
              "timing_resolution_s": .5,
              "timing_caveat": "interval arrival includes scheduler/stdio delay; not sub-interval path detection time"}
    errors, regressions = [], []
    if not echoes:
        errors.append('echo_generator_evidence_missing')
    expected = ['a_down', 'a_up', 'b_down', 'b_up']
    if [e['event'] for e in events] != expected or any(e['exit_code'] for e in events):
        errors.append('fault_or_recovery_not_applied')
    times = {e['event']: e['monotonic_s'] for e in events}

    def mean(a, b):
        phase = [r for r in samples if a <= r['monotonic_end_s'] <= b]
        if len(phase) < 3:
            errors.append('insufficient_receiver_phase_coverage')
            return None
        return sum(r['mbps'] for r in phase) / len(phase)

    for path, pre in [('a', origin + 2), ('b', times.get('a_up', ended) + 5)]:
        down, up = times.get(path + '_down', ended), times.get(path + '_up', ended)
        finish = times.get('b_down', ended) if path == 'a' else ended - 5
        before = mean(pre, down - .5)
        degraded = mean(down + 1, up - .5)
        recovery = mean(up + 1, finish)
        row = {'pre_fault_avg_mbps': before, 'degraded_avg_mbps': degraded,
               'recovery_avg_mbps': recovery,
               'ttf_sec': sustained(samples, down, up, 75),
               'ttr_sec': sustained(samples, up, finish, before * .9) if before else None}
        responses = [e['monotonic_s'] for e in echoes if e['ok'] and down <= e['monotonic_s'] <= finish]
        # Bound outages at fault/recovery boundaries as well as between replies.
        row['max_echo_gap_s'] = max((b - a for a, b in zip([down] + responses, responses + [finish])), default=finish-down)
        row['echo_success_during_fault'] = sum(e['ok'] and down <= e['monotonic_s'] < up for e in echoes)
        if not row['echo_success_during_fault']:
            regressions.append(path + '_no_echo_while_faulted')
        if row['ttf_sec'] is None or row['ttr_sec'] is None:
            regressions.append(path + '_receiver_threshold_not_recovered')
        result['fault_' + path] = row
    result['post_recover_avg_mbps'] = mean(times.get('b_up', ended) + 10, ended)
    result['measurement_errors'] = errors
    result['performance_errors'] = regressions
    result['measurement_complete'] = not errors
    result['performance_health'] = 'inconclusive' if errors else 'regression' if regressions else 'pass'
    result['status'] = 'fail' if errors or regressions else 'pass'
    return result


def echo(args):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    if args.command == 'echo':
        sock.bind((args.target, 5422))
        while True:
            data, peer = sock.recvfrom(256)
            sock.sendto(data, peer)
    # Independent 10 Hz sends with a one-second response deadline. A blocking
    # 80 ms receive would call a queued but healthy high-RTT path dead and
    # change the offered workload whenever responses were late.
    sock.setblocking(False)
    pending = {}
    sent, total = 0, args.duration * 10
    next_send = time.monotonic()
    while sent < total or pending:
        now = time.monotonic()
        if sent < total and now >= next_send:
            payload = str(sent).encode()
            sock.sendto(payload, (args.target, 5422))
            pending[payload] = now
            sent += 1
            next_send += .1
        while True:
            try: data, _ = sock.recvfrom(256)
            except BlockingIOError: break
            started = pending.pop(data, None)
            if started is not None:
                received = time.monotonic()
                print(json.dumps({'monotonic_s': received, 'ok': received-started <= 1,
                                  'rtt_s': received-started, 'sent_monotonic_s': started}), flush=True)
        for data, started in list(pending.items()):
            if now - started >= 1:
                del pending[data]
                print(json.dumps({'monotonic_s': now, 'ok': False, 'rtt_s': None,
                                  'sent_monotonic_s': started}), flush=True)
        time.sleep(.005)


def run(args):
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    samples, events = [], []
    ready = threading.Event()

    def cmd(ns, *argv):
        return ['ip', 'netns', 'exec', ns, *argv]

    def execute(argv):
        r = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=10, text=True)
        with (out / 'actions.log').open('a') as log:
            log.write(repr(argv) + '\n' + r.stdout)
        if r.returncode:
            raise RuntimeError('fault command exited ' + str(r.returncode))

    server_log = (out / 'iperf-server.log').open('w')
    server = subprocess.Popen(cmd(args.server_ns, 'iperf3', '-s', '-1', '-B', args.target), stdout=server_log, stderr=subprocess.STDOUT)
    echo_log = (out / 'echo-server.log').open('w')
    echo_server = subprocess.Popen(cmd(args.server_ns, sys.executable, __file__, 'echo', '--target', args.target), stdout=echo_log, stderr=subprocess.STDOUT)
    client = probe = None
    probe_log = (out / 'echo.jsonl').open('w')
    try:
        time.sleep(1)
        if server.poll() is not None or echo_server.poll() is not None:
            raise RuntimeError('receiver failed to start')
        probe = subprocess.Popen(cmd(args.client_ns, sys.executable, __file__, 'probe', '--target', args.target,
                                     '--duration', str(args.duration + 20)), stdout=probe_log, stderr=subprocess.STDOUT)
        client = subprocess.Popen(cmd(args.client_ns, 'iperf3', '-c', args.target, '-R', '-P', '4',
                                      '-t', str(args.duration), '-i', '.5', '--forceflush'),
                                  stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)

        def read_receiver():
            with (out / 'iperf-client.log').open('w') as log:
                for line in client.stdout:
                    log.write(line); log.flush()
                    r = interval(line)
                    if r:
                        r['monotonic_end_s'] = time.monotonic()
                        samples.append(r)
                        ready.set()
        reader = threading.Thread(target=read_receiver, daemon=True)
        reader.start()
        if not ready.wait(30):
            raise RuntimeError('no receiver interval before fault schedule')
        origin = samples[0]['monotonic_end_s'] - samples[0]['iperf_end_s']
        schedule = [('a_down', 20), ('a_up', 40), ('b_down', 55), ('b_up', 75)]
        for event, offset in schedule:
            time.sleep(max(0, origin + offset - time.monotonic()))
            a = event.startswith('a')
            devc, devs = (args.dev_a_client, args.dev_a_server) if a else (args.dev_b_client, args.dev_b_server)
            up = event.endswith('up')
            rc = 0
            observed = time.monotonic()
            try:
                execute(cmd(args.client_ns, 'ip', 'link', 'set', devc, 'up' if up else 'down'))
                execute(cmd(args.server_ns, 'ip', 'link', 'set', devs, 'up' if up else 'down'))
                if up:
                    delay = '10ms' if a else '30ms'
                    for ns, dev in [(args.client_ns, devc), (args.server_ns, devs)]:
                        execute(cmd(ns, 'tc', 'qdisc', 'replace', 'dev', dev, 'root', 'netem', 'delay', delay, 'rate', '150mbit'))
                    if not a:
                        execute(cmd(args.client_ns, 'ip', 'route', 'replace', args.underlay_subnet,
                                    'via', args.gateway_b, 'dev', devc, 'metric', '200'))
            except (RuntimeError, subprocess.TimeoutExpired) as exc:
                rc = 1
                with (out / 'actions.log').open('a') as log: log.write(str(exc) + '\n')
            events.append({'event': event, 'monotonic_s': observed, 'completed_monotonic_s': time.monotonic(), 'exit_code': rc})
        client.wait(timeout=max(5, origin + args.duration + 20 - time.monotonic()))
        reader.join(timeout=2)
        ended = time.monotonic()
        # Stop/reap the probe before reading a possibly buffered final record.
        probe.terminate(); probe.wait(timeout=5); probe_log.flush()
        echoes = [json.loads(line) for line in (out / 'echo.jsonl').read_text().splitlines()]
        result = analyse(samples, events, echoes, origin, ended)
        result['iperf_exit_code'] = client.returncode
        result['observed_duration_s'] = ended - origin
        if client.returncode or ended - origin < args.duration * .9 or reader.is_alive():
            result['status'] = 'fail'
            result['measurement_errors'].append('iperf_failed_or_short')
            result['measurement_complete'] = False
            result['performance_health'] = 'inconclusive'
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
        result = {'status': 'fail', 'measurement_errors': [str(exc)], 'events': events, 'receiver_intervals': samples}
    finally:
        for process in (client, probe, server, echo_server):
            if process is not None:
                if process.poll() is None: process.terminate()
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired: process.kill(); process.wait()
        for log in (server_log, echo_log, probe_log): log.close()
    (out / 'measurement.json').write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
    return int(result['status'] != 'pass')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('command', choices=('run', 'echo', 'probe'))
    p.add_argument('--target', required=True)
    p.add_argument('--duration', type=int, default=100)
    p.add_argument('--output', type=Path)
    for key in ('server-ns', 'client-ns', 'dev-a-client', 'dev-a-server', 'dev-b-client', 'dev-b-server', 'underlay-subnet', 'gateway-b'):
        p.add_argument('--' + key)
    args = p.parse_args()
    return run(args) if args.command == 'run' else echo(args)


if __name__ == '__main__':
    sys.exit(main())
