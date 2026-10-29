#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# ci_bench_quic.sh — inner-QUIC traffic through the tunnel, via picoquicdemo
#
# Why this exists: mqvpn is a QUIC proxy, and every other measurement in this
# harness drives inner TCP over iperf3. The inner protocol most of its traffic
# actually is has never been measured under load, so a TCP-specific pathology
# and a general tunnel one are indistinguishable in the artifact.
#
# What it is for specifically: nested congestion control. An outer MPQUIC
# retransmit inflates the inner connection's RTT, the inner stack's own RTO
# fires on the inflated estimate, and the two control loops back off -- then
# ramp -- together. The signature is an oscillating rate rather than a lower
# one, which no single scalar in a results row can show. Pair this with
# ci_bench_sampler.sh, whose per-second series is where the shape appears.
#
# IMPORTANT on what is and is not observed: picoquicdemo prints one goodput line
# at the end of the transfer, not a time series, and this file does not parse
# qlog. The inner connection's RTT is therefore NOT measured here. What the
# sampler sees is the OUTER tunnel, and the inner behaviour is INFERRED from
# outer retransmit correlated against the throughput trough. Any finding built
# on these fields has to say "inferred" -- claiming an inner-RTT measurement
# from this data would be false.
#
# The picoquic build is pinned (scripts/ci_interop/build_picoquic.sh, picoquic
# e652e454 / v1.1.50.0) and BBR is a comparability invariant of that pin: a
# bump that drops -G bbr must not silently fall back to another controller,
# because the number this produces would stop being comparable with the series.
#
# Lifted from benchmarks/sweep_reorder.sh:313 run_inner_http3() so the CI path
# and the manual sweep share one implementation rather than drifting apart.

# Where the clone lands. Gitignored; CI builds it, and the cache must retain the
# clone's certs/ directory because PICO_CERT/PICO_KEY point into it and
# build_picoquic.sh does not generate them.
CI_BENCH_QUIC_DIR="${CI_BENCH_QUIC_DIR:-}"
PICOQUICDEMO="${PICOQUICDEMO:-}"

# 20 MiB. Large enough that BBR leaves startup and the steady-state oscillation
# (if any) has room to appear; small enough that a collapsed regime still
# finishes inside PICO_TIMEOUT instead of returning NA for the wrong reason.
CI_BENCH_QUIC_BYTES="${CI_BENCH_QUIC_BYTES:-20971520}"
CI_BENCH_QUIC_TIMEOUT="${CI_BENCH_QUIC_TIMEOUT:-90}"
CI_BENCH_QUIC_PORT="${CI_BENCH_QUIC_PORT:-5401}"
CI_BENCH_QUIC_SNI="${CI_BENCH_QUIC_SNI:-test}"

_CB_QUIC_SVR_LOG=""
_CB_QUIC_CLI_LOG=""

# Results of the last transfer. Globals, not stdout: the callers here follow
# measure_pathset's rule (ci_bench_scenarios.sh:268) that a measurement helper
# must not be run in a subshell, because the pids it records would be lost.
QUIC_GOODPUT="NA"
QUIC_STATUS=ok

# ci_bench_quic_available -> 0 if an inner-QUIC transfer can be run
#
# Resolves the binary and its certs. Returns non-zero rather than exiting: a
# mode that cannot find picoquic should emit rows saying so, in the same spirit
# as ci_bench_have_tiers degrading to untiered rather than failing the job.
ci_bench_quic_available() {
    local root="${CI_BENCH_QUIC_DIR:-${SCRIPT_DIR}/../../third_party/picoquic}"

    if [ -z "$PICOQUICDEMO" ]; then
        PICOQUICDEMO="$(find "$root" -name picoquicdemo -type f -perm -u+x \
                        2>/dev/null | head -1 || true)"
    fi
    [ -n "$PICOQUICDEMO" ] && [ -x "$PICOQUICDEMO" ] || return 1
    PICOQUICDEMO="$(realpath "$PICOQUICDEMO")"

    PICO_CERT="${PICO_CERT:-${root}/certs/cert.pem}"
    PICO_KEY="${PICO_KEY:-${root}/certs/key.pem}"
    [ -r "$PICO_CERT" ] && [ -r "$PICO_KEY" ] || return 1

    CI_BENCH_QUIC_PIN="$(git -C "$root" rev-parse --short HEAD 2>/dev/null \
                         || echo unknown)"
    return 0
}

# ci_bench_quic_transfer <server-bind-ip>
#   -> QUIC_GOODPUT (Mbps, or the sentinel "NA") / QUIC_STATUS
#
# Pass TUNNEL_SERVER_IP to measure through the tunnel, or IP_A_SERVER_ADDR to
# measure the bare path for a baseline (the pattern
# ci_bench_raw_throughput.sh:73 uses).
ci_bench_quic_transfer() {
    local target="$1"
    QUIC_GOODPUT="NA"; QUIC_STATUS=ok

    if ! ci_bench_quic_available; then
        QUIC_STATUS=picoquic_missing
        return 0
    fi

    _CB_QUIC_SVR_LOG="$(mktemp)"
    _CB_QUIC_CLI_LOG="$(mktemp)"

    # -1 closes after one connection (one measurement is one bulk GET); -D
    # keeps the payload off disk at both ends, which matters on a 2-vCPU runner
    # where disk writes would land in the throughput being measured. qlog and
    # binlog stay off by omitting -q/-b/-l for the same reason.
    ip netns exec "$NS_SERVER" "$PICOQUICDEMO" \
        -p "$CI_BENCH_QUIC_PORT" \
        -c "$PICO_CERT" -k "$PICO_KEY" \
        -G bbr -1 -D \
        >"$_CB_QUIC_SVR_LOG" 2>&1 &
    local svr_pid=$!
    sleep 1
    if ! kill -0 "$svr_pid" 2>/dev/null; then
        QUIC_STATUS=quic_server_failed
        rm -f "$_CB_QUIC_SVR_LOG" "$_CB_QUIC_CLI_LOG"
        return 0
    fi

    # "/<bytes>" asks the demo H3 server to generate that many bytes on the fly,
    # so no web root or sized file is needed. -n <sni> is mandatory: a NULL SNI
    # makes the GET fail. `timeout` bounds a stalled or crawling transfer the
    # way ci_bench_run_iperf does -- without it one collapsed path hangs the
    # mode until the job timeout, which is how a weekly netsim job once burned
    # 60 minutes while its siblings finished in six.
    timeout -k 5 "$CI_BENCH_QUIC_TIMEOUT" \
        ip netns exec "$NS_CLIENT" "$PICOQUICDEMO" \
        -G bbr -D -n "$CI_BENCH_QUIC_SNI" \
        "$target" "$CI_BENCH_QUIC_PORT" "/${CI_BENCH_QUIC_BYTES}" \
        >"$_CB_QUIC_CLI_LOG" 2>&1 || true

    # Kill the server before waiting on it: with -1 it blocks until its first
    # connection, so a client that never got through would leave a bare `wait`
    # hanging forever.
    kill "$svr_pid" 2>/dev/null || true
    wait "$svr_pid" 2>/dev/null || true

    # The client prints BOTH "Received ... Mbps." (download) and "Sent ... Mbps."
    # (upload). Match the Received line only -- taking the last Mbps token on
    # the page would silently report the ACK-direction rate as the throughput.
    local gp
    gp="$(awk '
        /^Received [0-9]+ bytes in .* Mbps/ {
            for (i = 1; i <= NF; i++)
                if ($i == "Mbps" || $i == "Mbps.") { print $(i - 1); break }
        }' "$_CB_QUIC_CLI_LOG" 2>/dev/null | tail -1 || true)"

    if [ -z "$gp" ]; then
        # NA, never 0. A failed transfer and a genuinely zero-throughput one are
        # different findings, and the report filters the sentinel instead of
        # averaging it into a result.
        QUIC_GOODPUT="NA"
        QUIC_STATUS=quic_no_goodput
    else
        QUIC_GOODPUT="$gp"
    fi

    rm -f "$_CB_QUIC_SVR_LOG" "$_CB_QUIC_CLI_LOG"
    return 0
}
