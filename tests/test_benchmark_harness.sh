#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Real iperf3 JSON/schema integration, independent of a VPN build. Needs root.
set -euo pipefail
source "$(dirname "$0")/../scripts/ci_benchmarks/ci_bench_env.sh"
source "$(dirname "$0")/../scripts/ci_benchmarks/ci_bench_quic.sh"
ci_bench_quic_profile homo_good
[ "$QUIC_PROFILE_BYTES" = 134217728 ]
ci_bench_quic_profile hetero_extreme
[ "$QUIC_PROFILE_BYTES" = 2097152 ]
CI_BENCH_QUIC_BYTES_OVERRIDE=12345
ci_bench_quic_profile hetero_extreme
[ "$QUIC_PROFILE_BYTES" = 12345 ]
CI_BENCH_QUIC_BYTES_OVERRIDE=0
if ci_bench_quic_profile homo_good; then echo 'invalid QUIC budget accepted' >&2; exit 1; fi
unset CI_BENCH_QUIC_BYTES_OVERRIDE
echo 'PASS QUIC scenario budgets and explicit overrides'
[ "$(ci_bench_config_bool on)" = true ]
[ "$(ci_bench_config_bool off)" = false ]
if ci_bench_config_bool invalid >/dev/null 2>&1; then exit 1; fi
echo 'PASS arm booleans use the actual config grammar'
[ "$(python3 -c 'import os; print(os.environ["CI_BENCH_SCHEDULER"])')" = "$CI_BENCH_SCHEDULER" ]
echo 'PASS child metadata sees the configured scheduler'
work="$(mktemp -d)"
NS_SERVER="bench-harness-$$"
NS_CLIENT="$NS_SERVER"
CI_BENCH_RAW_DIR="$work/raw"
mkdir -p "$CI_BENCH_RAW_DIR"
trap 'ci_bench_stop_vpn; ip netns pids "$NS_SERVER" | xargs -r kill 2>/dev/null || true; ip netns del "$NS_SERVER"; rm -rf "$work"' EXIT
ip netns add "$NS_SERVER"
ip -n "$NS_SERVER" link set lo up
ci_bench_vpn_evidence client wlb '--path test-a'
first_evidence="$_CB_LAST_EVIDENCE"
printf 'failed handshake\n' >"$first_evidence/vpn.log"
ci_bench_record_vpn_exit client 123 1 "$first_evidence"
ci_bench_vpn_evidence client wlb '--path test-b'
[ "$first_evidence" != "$_CB_LAST_EVIDENCE" ]
grep -q 'failed handshake' "$first_evidence/vpn.log"
python3 - "$first_evidence" <<'PY'
import json, sys
from pathlib import Path
p = Path(sys.argv[1])
assert json.loads((p/'exit.json').read_text())['exit_code'] == 1
assert json.loads((p/'context.json').read_text())['paths'] == '--path test-a'
PY
echo 'PASS failed phase evidence survives subsequent client starts'
# Drive the actual start/stop helpers with a fixture that exits before forming
# a tunnel; neither the next client nor cleanup may discard the first error.
MQVPN="$work/exiting-vpn"
cat >"$MQVPN" <<'SH'
#!/bin/sh
if [ "$1" = --genkey ]; then printf 'fixture-key\n'; exit 0; fi
printf 'fixture early startup failure\n' >&2
exit 23
SH
chmod +x "$MQVPN"
if ci_bench_start_server wlb >/dev/null; then echo 'dead server accepted' >&2; exit 1; fi
if ci_bench_start_client '--path fixture-a' wlb >/dev/null; then echo 'dead client accepted' >&2; exit 1; fi
failed_client_log="$CI_BENCH_CLIENT_LOG"
failed_client_evidence="$_CB_CLIENT_EVIDENCE"
if ci_bench_start_client '--path fixture-b' wlb >/dev/null; then echo 'dead client accepted' >&2; exit 1; fi
ci_bench_stop_vpn
grep -q 'fixture early startup failure' "$failed_client_log"
python3 - "$failed_client_evidence/exit.json" <<'PY'
import json, sys
assert json.load(open(sys.argv[1]))['exit_code'] == 23
PY
echo 'PASS actual start/stop preserves already-dead VPN startup diagnostics'
CI_BENCH_IPERF_TARGET=127.0.0.1
for proto in TCP UDP; do
    for direction in UL DL; do
        sample="$(ci_bench_run_iperf "$proto" "$direction" 2 1 1000000)"
        rate="$(ci_bench_parse_throughput "$sample")"
        [ "$rate" != NA ] || { cat "$sample"; exit 1; }
        python3 - "$sample" "$proto" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d['_mqvpn']['measurement_complete'], d
assert d['_mqvpn']['receiver_bytes'] > 0, d
assert d['_mqvpn']['observed_duration_s'] >= 1.8, d
PY
        if [ "$proto" = UDP ]; then
            quality="$(ci_bench_parse_udp_quality "$sample")"
            [ "${quality%% *}" != NA ] || { cat "$sample"; exit 1; }
        fi
        echo "PASS real $proto $direction receiver=$rate Mbps"
        rm -f "$sample"
    done
done
[ "$(find "$CI_BENCH_RAW_DIR" -maxdepth 1 -name '*.server-process.json' | wc -l)" -eq 4 ]
echo 'PASS raw evidence survives caller cleanup'
# Reproduce iperf's real MTU warning. stderr must remain separate from JSON.
ip -n "$NS_SERVER" link set lo mtu 1400
sample="$(CI_BENCH_IPERF_LEN=1400 ci_bench_run_iperf UDP DL 2 1 2000000)"
[ "$(ci_bench_parse_throughput "$sample")" != NA ]
grep -q 'warning: UDP block size' "${sample}.client.stderr.log"
python3 - "$sample" "$CI_BENCH_RAW_DIR" <<'PY'
import json,sys
from pathlib import Path
p = Path(sys.argv[1]); archive = Path(sys.argv[2])
doc = json.loads(p.read_text())
assert doc['_mqvpn']['measurement_complete'], doc
assert json.loads(Path(str(p)+'.client.stdout.log').read_text())['end']['sum_received']['bytes']>0
manifest = json.loads((archive/(p.name+'.evidence.json')).read_text())
assert any(f['name'].endswith('.client.stderr.log') for f in manifest['files'])
PY
echo 'PASS real iperf warning preserves valid JSON and full stderr'
if command -v tcpdump >/dev/null; then
    tcpdump -n -r "${sample}.client.control.pcap" >"$work/decoded-control.log" 2>&1
    grep -q '5201' "$work/decoded-control.log"
    echo 'PASS full TCP control packets retained in pcap'
fi
# Inject a client control failure with a long stderr stream. The real one-shot
# server remains owned by this call and must still be cleaned up and reaped.
real_iperf="$(command -v iperf3)"
mkdir "$work/bin"
cat >"$work/bin/iperf3" <<SH
#!/bin/sh
if [ "\$1" = -s ]; then exec "$real_iperf" "\$@"; fi
python3 - <<'PY'
import sys
print('BEGIN-CONTROL-FAILURE',file=sys.stderr)
for i in range(12000): print('full failure detail %d' % i,file=sys.stderr)
print('END-CONTROL-FAILURE',file=sys.stderr)
print('{"error":"unable to read from stream socket","end":{}}')
PY
exit 1
SH
chmod +x "$work/bin/iperf3"
sample="$(PATH="$work/bin:$PATH" ci_bench_run_iperf UDP DL 1 1 1000000)"
[ "$(ci_bench_parse_throughput "$sample")" = NA ]
python3 - "$sample" "$CI_BENCH_RAW_DIR" <<'PY'
import hashlib,json,sys
from pathlib import Path
p = Path(sys.argv[1]); archive = Path(sys.argv[2])
d = json.loads(p.read_text()); assert not d['_mqvpn']['measurement_complete']
stderr = archive/(p.name+'.client.stderr.log')
assert len(stderr.read_text().splitlines())==12002
assert stderr.read_text().endswith('END-CONTROL-FAILURE\n')
assert json.loads((archive/(p.name+'.client.stdout.log')).read_text())['error']
manifest = json.loads((archive/(p.name+'.evidence.json')).read_text())
entry = next(f for f in manifest['files'] if f['name']==stderr.name)
assert entry['sha256']==hashlib.sha256(stderr.read_bytes()).hexdigest()
for suffix in ('.server.log','.context.json','.server-process.json',
               '.network-before.log','.network-after-client.log'):
    assert (archive/(p.name+suffix)).exists(), suffix
PY
echo 'PASS control failure retains all 12002 stderr lines and separate stdout'
