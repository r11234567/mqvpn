#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Sourced by scenarios. Independent ports prevent one-shot iperf collisions.
mixed_clock() { python3 -c 'import time; print(time.monotonic())'; }

mixed_iperf() {
    local name="$1" proto="$2" direction="$3" duration="$4" streams="$5" rate="${6:-}"
    local jf
    jf="$(ci_bench_run_iperf "$proto" "$direction" "$duration" "$streams" "$rate")"
    cp "$jf" "$mixdir/$name.json"
    printf '%s.json\n' "$name" >"$mixdir/$name.path"
    rm -f "$jf"
}

run_mixed() {
    local class="$1" spec="${NETSIM_CLASS[$1]}" dev0 dev1
    local duration="${CI_BENCH_MIXED_SEC:-20}" mixdir
    [[ "$duration" =~ ^[1-9][0-9]*$ ]] || return 1
    mixdir="$(mktemp -d "${CI_BENCH_RAW_DIR}/mixed-${class}.XXXXXX")"
    ci_bench_stop_vpn
    netsim_setup 2 >/dev/null || return 1
    netsim_apply_path 0 "${spec%%|*}" 4242 || return 1
    netsim_apply_path 1 "${spec##*|}" 4252 || return 1
    dev0="$(netsim_veth_cli 0)"; dev1="$(netsim_veth_cli 1)"
    start_server_with_ctrl "$CI_BENCH_SCHEDULER" >/dev/null || return 1
    ci_bench_start_client "--path $dev0 --path $dev1" "$CI_BENCH_SCHEDULER" >/dev/null || return 1
    ci_bench_wait_tunnel "$TUNNEL_WAIT_SEC" >/dev/null || return 1
    # Isolated small-UDP controls on exactly the same connected path set.
    CI_BENCH_IPERF_LEN=50 IPERF3_PORT=5422 mixed_iperf baseline_dl UDP DL "$duration" 1 800000
    CI_BENCH_IPERF_LEN=50 IPERF3_PORT=5423 mixed_iperf baseline_ul UDP UL "$duration" 1 800000
    local tcp_start udp_start udp_end tcp_pid quic_pid dl_pid ul_pid
    tcp_start="$(mixed_clock)"
    (
        trap - EXIT TERM INT
        IPERF3_PORT=5421 CI_BENCH_IPERF_INTERVAL=1 mixed_iperf tcp TCP DL "$((duration+8))" 4
        printf '{"start_s":%s,"end_s":%s}\n' "$tcp_start" "$(mixed_clock)" >"$mixdir/tcp-window.json"
    ) >"$mixdir/tcp.log" 2>&1 &
    tcp_pid=$!
    (
        trap - EXIT TERM INT
        local end started deadline=$((SECONDS+duration+4))
        while [ "$SECONDS" -lt "$deadline" ]; do
            started="$(mixed_clock)"
            ci_bench_quic_transfer "$TUNNEL_SERVER_IP"
            end="$(mixed_clock)"
            python3 - "$started" "$end" "$QUIC_STATUS" "$QUIC_GOODPUT" "$QUIC_DURATION" >>"$mixdir/quic.jsonl" <<'PY'
import json,sys
start,end,status,mbps,duration=sys.argv[1:]
end=float(end)
print(json.dumps({'command_start_s':float(start), 'start_s':end-float(duration) if duration!='NA' else float(start),
 'end_s':end, 'status':status, 'mbps':None if mbps=='NA' else float(mbps),
 'timing_note':'completed receiver duration aligned to command completion; includes small teardown delay'}))
PY
            [ "$QUIC_STATUS" = ok ] || break
        done
    ) >"$mixdir/quic.log" 2>&1 &
    quic_pid=$!
    sleep 3
    udp_start="$(mixed_clock)"
    (trap - EXIT TERM INT; CI_BENCH_IPERF_LEN=50 IPERF3_PORT=5422 mixed_iperf udp_dl UDP DL "$duration" 1 800000) >"$mixdir/udp-dl.log" 2>&1 &
    dl_pid=$!
    (trap - EXIT TERM INT; CI_BENCH_IPERF_LEN=50 IPERF3_PORT=5423 mixed_iperf udp_ul UDP UL "$duration" 1 800000) >"$mixdir/udp-ul.log" 2>&1 &
    ul_pid=$!
    wait "$dl_pid" || true; wait "$ul_pid" || true
    udp_end="$(mixed_clock)"
    printf '{"start_s":%s,"end_s":%s}\n' "$udp_start" "$udp_end" >"$mixdir/udp-window.json"
    # Missing/failed child records invalidate the row in the reducer.
    wait "$tcp_pid" || true; wait "$quic_pid" || true
    python3 "${SCRIPT_DIR}/ci_bench_mixed_results.py" "$mixdir" "$class" >>"$ROWS"
    emit_results
    ci_bench_stop_vpn
    netsim_teardown
}
