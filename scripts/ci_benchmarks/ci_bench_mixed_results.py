# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
"""Mixed-protocol delivery and overlap evidence for netsim."""
import json
from pathlib import Path
import statistics

from ci_bench_results import iperf_measurement
from ci_bench_metrics import udp_point


def summarise(folder, scenario):
    folder = Path(folder)
    row = {'scenario': scenario, 'mode_family': 'mixed', 'status': 'ok',
           'inner_protocols': ['tcp_bulk_dl', 'quic_h3_dl', 'udp_small_dl', 'udp_small_ul']}
    errors = []
    for name in ('baseline_dl','baseline_ul','tcp','udp_dl','udp_ul'):
        try:
            path = Path((folder/(name+'.path')).read_text().strip())
            if not path.is_absolute():
                path = folder / path
            raw = json.loads(path.read_text())
            sample = iperf_measurement(raw)
            row[name+'_mbps'] = sample['receiver_mbps']
            if name != 'tcp':
                p = udp_point(raw, .8)
                row[name+'_loss_pct'] = p['loss_pct']
                row[name+'_jitter_ms'] = p['jitter_ms']
        except (OSError, ValueError, TypeError) as exc:
            row[name+'_mbps'] = None
            errors.append(name+': '+str(exc))
    try:
        udp_window = json.loads((folder/'udp-window.json').read_text())
        attempts = [json.loads(line) for line in (folder/'quic.jsonl').read_text().splitlines()]
        row['quic_attempts'] = attempts
        overlap = [a for a in attempts if a['end_s'] > udp_window['start_s']
                   and a['start_s'] < udp_window['end_s']]
        good = [a['mbps'] for a in overlap if a['status']=='ok']
        row['quic_goodput_mbps'] = statistics.median(good) if good else None
        row['quic_overlapping_attempts'] = len(overlap)
        row['udp_window'] = udp_window
        if not overlap or not good or any(a['status']!='ok' for a in overlap):
            errors.append('inner_quic_incomplete_during_udp')
        tcp_window = json.loads((folder/'tcp-window.json').read_text())
        if tcp_window['start_s'] > udp_window['start_s'] or tcp_window['end_s'] < udp_window['end_s']:
            errors.append('tcp_did_not_cover_udp_window')
        row['tcp_window'] = tcp_window
        raw_tcp = json.loads((folder/'tcp.json').read_text())
        sample = iperf_measurement(raw_tcp)
        origin = tcp_window['end_s'] - sample['observed_duration_s']
        covered = sum(iv['sum'].get('seconds',0) for iv in raw_tcp.get('intervals',[])
                      if iv.get('sum',{}).get('bits_per_second',0)>0
                      and origin + iv['sum']['end'] >= udp_window['start_s']
                      and origin + iv['sum']['start'] <= udp_window['end_s'])
        row['tcp_positive_overlap_s'] = covered
        if covered < .8 * (udp_window['end_s'] - udp_window['start_s']):
            errors.append('tcp_receiver_did_not_progress_through_udp_window')
    except (OSError, ValueError, TypeError, KeyError) as exc:
        errors.append('load_overlap: '+str(exc))
    findings = []
    if not errors:
        for d in ('dl','ul'):
            added = row['udp_'+d+'_loss_pct'] - row['baseline_'+d+'_loss_pct']
            row['added_loss_'+d+'_pct'] = added
            if added > 1:
                findings.append(f'mixed_small_udp_loss_{d}: {added:.2f} percentage points above isolated baseline')
    row.update(status='measurement_incomplete' if errors else 'ok', mixed_errors=errors,
               findings=findings, raw_evidence_dir=str(folder),
               workload_note='TCP bulk and repeated QUIC objects overlap the small-UDP transfer window; QUIC object gaps are retained')
    return row


if __name__ == '__main__':
    import sys
    print(json.dumps(summarise(sys.argv[1], sys.argv[2]), allow_nan=False))
