# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""One namespace reader: aggregate path counters and retain each leg's series."""
import argparse
import json
from pathlib import Path
import time


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('csv', type=Path)
    p.add_argument('--interfaces', required=True)
    p.add_argument('--interval', type=float, default=1)
    p.add_argument('--pid', default='')
    args = p.parse_args()
    if args.interval <= 0:
        raise ValueError('sample interval must be positive')
    names = args.interfaces.split()
    if not names or len(set(names)) != len(names):
        raise ValueError('invalid interface manifest')
    counters = ('tx_packets', 'rx_packets', 'tx_bytes', 'rx_bytes', 'tx_dropped', 'rx_dropped')
    with args.csv.open('w', buffering=1) as csv, Path(str(args.csv) + '.paths.jsonl').open('w', buffering=1) as paths:
        while True:
            now = time.monotonic()
            cpu = list(map(int, Path('/proc/stat').read_text().splitlines()[0].split()[1:9]))
            udp = {}
            lines = Path('/proc/net/snmp').read_text().splitlines()
            for header, values in zip(lines, lines[1:]):
                if header.startswith('Udp:') and values.startswith('Udp:'):
                    udp = dict(zip(header.split()[1:], map(int, values.split()[1:])))
                    break
            nrx = next((list(map(int, line.split()[1:])) for line in Path('/proc/softirqs').read_text().splitlines()
                        if line.strip().startswith('NET_RX:')), [0])
            legs = {name: {key: int((Path('/sys/class/net')/name/'statistics'/key).read_text()) for key in counters}
                    for name in names}
            totals = {key: sum(leg[key] for leg in legs.values()) for key in counters}
            proc = {}
            if args.pid:
                for line in Path('/proc', args.pid, 'status').read_text().splitlines():
                    key, value = line.split(':', 1)
                    if key in ('nonvoluntary_ctxt_switches', 'VmSwap', 'VmRSS', 'Threads'):
                        proc[key] = int(value.split()[0])
            row = [now, *cpu, udp.get('InDatagrams', 0), udp.get('OutDatagrams', 0), udp.get('SndbufErrors', 0),
                   nrx[0], sum(nrx), *(totals[k] for k in counters[:4]), udp.get('RcvbufErrors', 0),
                   totals['tx_dropped'], totals['rx_dropped'], *(proc.get(k, 0) for k in
                   ('nonvoluntary_ctxt_switches', 'VmSwap', 'VmRSS', 'Threads'))]
            csv.write(','.join(map(str, row)) + '\n')
            paths.write(json.dumps({'monotonic_s': now, 'interfaces': legs}) + '\n')
            time.sleep(max(0, args.interval - (time.monotonic() - now)))


if __name__ == '__main__':
    main()
