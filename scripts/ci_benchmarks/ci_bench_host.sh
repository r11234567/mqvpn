#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# ci_bench_host.sh — Server tier and host-state emulation
#
# The netsim library emulates the network between client and server. This one
# emulates the server's own box: how much CPU and memory it gets, and what else
# is fighting it for the machine.
#
# What these labels are, precisely:
#
#   A tier caps the server's cgroup. `CPUQuota` limits the share of wall time
#   the process may run, so a tier reproduces the *throughput ceiling* of a
#   smaller instance. It does not reproduce that instance's per-operation
#   latency, and it cannot make a fast core act like a slow one — quota
#   throttles a fast core in bursts rather than slowing each instruction. The
#   GB5 figures below are the nominal scores of the instances these tiers stand
#   in for; they are not measured here and must be printed as nominal.
#
#   Host states add competing load. Hypervisor steal time cannot be emulated
#   from inside the guest, so contention from a neighbour process is the
#   closest available proxy and results should say so rather than claim to have
#   measured a noisy hypervisor.
#
# Disk size is not modelled: none of these benchmarks touch the disk.

# Tier -> cgroup properties. Nominal GB5 single-core scores, for the record:
# vps_1c1g 400-600, vps_1c1g_std 1000-1100, vps_2c2g 1400-1500,
# vps_2c2g_fast 1500-2000.
declare -A CI_BENCH_TIER_PROPS=(
    [vps_1c1g]="AllowedCPUs=0 CPUQuota=40% MemoryMax=1G"
    [vps_1c1g_std]="AllowedCPUs=0 CPUQuota=100% MemoryMax=1G"
    [vps_2c2g]="AllowedCPUs=0-1 CPUQuota=140% MemoryMax=2G"
    [vps_2c2g_fast]="AllowedCPUs=0-1 CPUQuota=200% MemoryMax=2G"
)

# Number of CPUs each tier is pinned to, so noisy-neighbour load can be placed
# outside them. Derived from AllowedCPUs above; kept explicit because parsing a
# cpuset range is more code than restating the count.
declare -A CI_BENCH_TIER_NCPU=(
    [vps_1c1g]=1
    [vps_1c1g_std]=1
    [vps_2c2g]=2
    [vps_2c2g_fast]=2
)

CI_BENCH_TIER="${CI_BENCH_TIER:-}"
CI_BENCH_HOST_STATE="${CI_BENCH_HOST_STATE:-healthy}"
CI_BENCH_TIER_UNIT="mqvpn-bench-tier"

# Quota a cpu_capped run drops to mid-measurement, emulating a provider
# throttle landing on a live server.
CI_BENCH_CAPPED_QUOTA="${CI_BENCH_CAPPED_QUOTA:-20%}"

CI_BENCH_HOST_STATES="healthy noisy_neighbour softirq_storm cpu_capped"

_CB_HOST_PIDS=""
_CB_NOISE_NS_A="ci-bench-noise-a"
_CB_NOISE_NS_B="ci-bench-noise-b"

# True when transient scopes with cgroup properties can actually be created.
# Probed once, because the answer costs a process spawn and never changes
# within a run. A non-systemd host, cgroup v1, or a non-root caller all land
# here, and every one of them means "run untiered" rather than "fail".
_CB_HAVE_TIERS=""
ci_bench_have_tiers() {
    if [ -z "$_CB_HAVE_TIERS" ]; then
        _CB_HAVE_TIERS=no
        if command -v systemd-run >/dev/null 2>&1 \
            && [ "$(stat -fc %T /sys/fs/cgroup 2>/dev/null)" = "cgroup2fs" ] \
            && systemd-run --scope --collect --quiet \
                -p CPUQuota=100% true >/dev/null 2>&1; then
            _CB_HAVE_TIERS=yes
        fi
    fi
    [ "$_CB_HAVE_TIERS" = yes ]
}

# Echo the command prefix that runs the server inside a tier's scope, or
# nothing for an untiered run. Callers splice it in unquoted, so it must stay
# free of anything needing word-splitting protection.
ci_bench_tier_prefix() {
    local tier="${1:-}"
    [ -z "$tier" ] && return 0

    local props="${CI_BENCH_TIER_PROPS[$tier]:-}"
    if [ -z "$props" ]; then
        echo "ci_bench_tier_prefix: unknown tier '$tier'" >&2
        return 1
    fi
    if ! ci_bench_have_tiers; then
        echo "ci_bench_tier_prefix: transient scopes unavailable," \
            "running '$tier' untiered" >&2
        return 0
    fi

    local out="systemd-run --scope --collect --quiet --unit=${CI_BENCH_TIER_UNIT}"
    local p
    for p in $props; do
        out="$out -p $p"
    done
    echo "$out"
}

# Stop the tier scope. The pid captured by the caller belongs to systemd-run,
# whose child lives in the scope, so killing that pid alone can leave the
# server running and hold the listen port against the next scenario.
ci_bench_tier_cleanup() {
    ci_bench_have_tiers || return 0
    systemctl stop "${CI_BENCH_TIER_UNIT}.scope" >/dev/null 2>&1 || true
}

# Tighten the live scope's quota, for the cpu_capped state. No-op when the run
# is untiered: there is no scope to retighten, and silently doing nothing is
# better than aborting a scenario over an emulation detail.
ci_bench_tier_throttle() {
    local quota="${1:-$CI_BENCH_CAPPED_QUOTA}"
    ci_bench_have_tiers || return 0
    systemctl set-property --runtime "${CI_BENCH_TIER_UNIT}.scope" \
        "CPUQuota=${quota}" >/dev/null 2>&1 || true
}

# CPU list for competing load: everything the tier does not own. Empty when the
# runner has no spare CPU, which is the signal to skip neighbour load rather
# than pile it onto the server's own cores — that would measure the tier's
# throttling twice and call it interference.
ci_bench_host_free_cpus() {
    local tier="${1:-}"
    local total owned
    total="$(nproc)"
    owned="${CI_BENCH_TIER_NCPU[$tier]:-0}"
    [ "$owned" -ge "$total" ] && return 0
    echo "${owned}-$((total - 1))"
}

# Burn CPU on $1 (a taskset cpu-list). stress-ng if available, otherwise shell
# spinners — the load only has to be real, not calibrated, and adding a package
# dependency for a busy loop is not worth a broken job when the mirror is slow.
_cb_host_burn() {
    local cpus="$1" n="$2"
    if command -v stress-ng >/dev/null 2>&1; then
        taskset -c "$cpus" stress-ng --cpu "$n" --timeout 0 &>/dev/null &
        _CB_HOST_PIDS="$_CB_HOST_PIDS $!"
        return 0
    fi
    local i
    for (( i=0; i<n; i++ )); do
        taskset -c "$cpus" sh -c 'while :; do :; done' &>/dev/null &
        _CB_HOST_PIDS="$_CB_HOST_PIDS $!"
    done
}

# A small-packet flood over a veth pair that has nothing to do with the tunnel,
# to contend for softirq processing. 64-byte datagrams at unlimited rate is the
# highest packet rate iperf3 will produce, and packet rate is what ksoftirqd
# actually costs.
_cb_host_softirq_storm() {
    ip netns add "$_CB_NOISE_NS_A" 2>/dev/null || true
    ip netns add "$_CB_NOISE_NS_B" 2>/dev/null || true
    ip link add cb-noise-a type veth peer name cb-noise-b 2>/dev/null || return 0
    ip link set cb-noise-a netns "$_CB_NOISE_NS_A"
    ip link set cb-noise-b netns "$_CB_NOISE_NS_B"
    ip netns exec "$_CB_NOISE_NS_A" ip addr add 10.90.0.1/24 dev cb-noise-a
    ip netns exec "$_CB_NOISE_NS_B" ip addr add 10.90.0.2/24 dev cb-noise-b
    ip netns exec "$_CB_NOISE_NS_A" ip link set cb-noise-a up
    ip netns exec "$_CB_NOISE_NS_B" ip link set cb-noise-b up

    ip netns exec "$_CB_NOISE_NS_A" iperf3 -s -B 10.90.0.1 &>/dev/null &
    _CB_HOST_PIDS="$_CB_HOST_PIDS $!"
    sleep 0.5
    ip netns exec "$_CB_NOISE_NS_B" \
        iperf3 -c 10.90.0.1 -u -l 64 -b 0 -t 86400 &>/dev/null &
    _CB_HOST_PIDS="$_CB_HOST_PIDS $!"
}

# ci_bench_host_start <state> [tier]
#
# Bring up whatever competing load the state calls for. Returns 0 for an
# unknown state after saying so, so a typo in a matrix entry costs one
# mislabelled row instead of the whole job.
ci_bench_host_start() {
    local state="${1:-healthy}" tier="${2:-}"
    _CB_HOST_PIDS=""

    case "$state" in
        healthy)
            ;;
        noisy_neighbour)
            local cpus
            cpus="$(ci_bench_host_free_cpus "$tier")"
            if [ -z "$cpus" ]; then
                echo "  [host] no CPU outside tier '${tier:-none}';" \
                    "skipping neighbour load" >&2
                return 0
            fi
            _cb_host_burn "$cpus" 2
            ;;
        softirq_storm)
            _cb_host_softirq_storm
            ;;
        cpu_capped)
            # Applied mid-measurement by the caller, not here: the point is a
            # throttle that lands on an already-running transfer.
            ;;
        *)
            echo "  [host] unknown state '$state', running healthy" >&2
            ;;
    esac
}

ci_bench_host_stop() {
    local p
    for p in $_CB_HOST_PIDS; do
        kill "$p" 2>/dev/null || true
        wait "$p" 2>/dev/null || true
    done
    _CB_HOST_PIDS=""

    # stress-ng forks workers that outlive the parent's SIGTERM.
    pkill -f "stress-ng" 2>/dev/null || true

    ip netns del "$_CB_NOISE_NS_A" 2>/dev/null || true
    ip netns del "$_CB_NOISE_NS_B" 2>/dev/null || true
    ip link del cb-noise-a 2>/dev/null || true
}
