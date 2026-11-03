#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Focused, sub-second control-close regression; no mqvpn build is required.
set -euo pipefail
source "$(dirname "$0")/../scripts/ci_benchmarks/ci_bench_env.sh"
command -v tcpdump >/dev/null
NS_SERVER="bench-udp-capture-$$"
burst="$(mktemp --suffix=.json)"
cleanup() {
    ci_bench_kill_ns_processes "$NS_SERVER"
    ip netns del "$NS_SERVER" 2>/dev/null || true
    if [ -n "${CI_BENCH_CAPTURE_TEST_DIR:-}" ]; then
        mkdir -p "$CI_BENCH_CAPTURE_TEST_DIR"
        cp "$burst"* "$CI_BENCH_CAPTURE_TEST_DIR/"
    fi
    rm -f "$burst"*
}
trap cleanup EXIT
ip netns add "$NS_SERVER"
ip -n "$NS_SERVER" link set lo up
printf '{"protocol":"UDP"}\n' >"${burst}.context.json"
ci_bench_control_capture_start "$NS_SERVER" "${burst}.client"; burst_client="$_CB_CAPTURE_PID"
ci_bench_control_capture_start "$NS_SERVER" "${burst}.server"; burst_server="$_CB_CAPTURE_PID"
ip netns exec "$NS_SERVER" python3 - <<'PY'
import socket
listener = socket.socket(); listener.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1)
listener.bind(('127.0.0.1',5201)); listener.listen(1)
client = socket.create_connection(('127.0.0.1',5201))
server,_ = listener.accept()
client.sendall(b'fast-control-close')
assert server.recv(1024)==b'fast-control-close'
server.close(); client.close(); listener.close()
PY
for ((attempt=0; attempt<40; attempt++)); do
    if [ "$(stat -c %s "${burst}.client.control.pcap")" -gt 24 ] &&
       [ "$(stat -c %s "${burst}.server.control.pcap")" -gt 24 ]; then break; fi
    sleep .01
done
[ "$(stat -c %s "${burst}.client.control.pcap")" -gt 24 ]
[ "$(stat -c %s "${burst}.server.control.pcap")" -gt 24 ]
ci_bench_control_capture_stop "$burst_client" "${burst}.client"
ci_bench_control_capture_stop "$burst_server" "${burst}.server"
python3 - "$burst" "$CI_BENCH_DIR" <<'PY'
import json,sys
from pathlib import Path
sys.path.insert(0,sys.argv[2])
from ci_bench_iperf_evidence import capture_status
prefix = Path(sys.argv[1]); status = capture_status(prefix)
Path(str(prefix)+'.capture-status.json').write_text(json.dumps(status,indent=2)+'\n')
assert status['status']=='complete',status
PY
echo 'PASS rapid control close reaches both pcaps before capture shutdown'
