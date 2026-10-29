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

CI_BENCH_HOST_STATES="healthy noisy_neighbour softirq_storm cpu_capped vps_baseline"

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

# The CPU list the tier itself owns, parsed out of AllowedCPUs. The inverse of
# the above, for load that belongs ON the server's cores rather than beside
# them. Falls back to CPU 0, which is where an untiered run lands anyway.
ci_bench_host_tier_cpus() {
    local tier="${1:-}" props="" kv
    # An empty subscript is an error under bash's associative arrays, not a
    # miss, so the untiered case is answered before the lookup.
    [ -n "$tier" ] && props="${CI_BENCH_TIER_PROPS[$tier]:-}"
    for kv in $props; do
        case "$kv" in AllowedCPUs=*) echo "${kv#AllowedCPUs=}"; return 0 ;; esac
    done
    echo 0
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

# The load a real VPS is already carrying before mqvpn starts.
#
# Every tiered row so far handed the whole tier to mqvpn, which assumes a box
# bought to run nothing else. The observed baseline on a production 1 vCPU /
# 1 GB instance was materially different, and the difference is the whole
# reason the field numbers and the benchmark numbers disagree:
#
#   CPU     ~15% already consumed by existing services
#   Memory  559 MB of 929 MB already resident
#   Swap    378 MB of 1.5 GB already in use
#
# The memory figure is what matters most. mqvpn on that box had VmRSS 524 kB
# against VmSwap 68 MB -- almost the entire process paged out -- and a
# forwarder that must fault pages back in before it can forward is a forwarder
# that drops. That state is unreachable on a runner with free memory, so it has
# to be arranged.
#
# CPU_PCT is approximated by duty-cycling rather than by a cgroup quota: the
# competing load must sit OUTSIDE mqvpn's own tier scope (a quota on the scope
# would throttle mqvpn instead of competing with it), and a plain spinner on
# the same cpuset would take far more than 15%.
# Exported, not merely assigned: collect_host_profile reads these from a
# python3 child's environment, and an unexported value is invisible there. That
# exact omission made every vps row of run 34036912262 claim host_tier=untiered
# while the scope had in fact been created.
export CI_BENCH_BASELOAD_CPU_PCT="${CI_BENCH_BASELOAD_CPU_PCT:-15}"
export CI_BENCH_BASELOAD_MEM_MB="${CI_BENCH_BASELOAD_MEM_MB:-559}"
export CI_BENCH_BASELOAD_SWAP_MB="${CI_BENCH_BASELOAD_SWAP_MB:-378}"

_cb_host_vps_baseline() {
    local cpus="$1"
    local pct="$CI_BENCH_BASELOAD_CPU_PCT"

    # Duty-cycled busy loop: ~pct% of one core, on the same cpuset the tier
    # owns, because a neighbour on a different core is not competition for a
    # 1-vCPU box.
    #
    # In python rather than shell: a shell duty cycle needs a `date` spawn per
    # iteration to know when to stop burning, and at a 10 ms period those
    # spawns cost more than the load being emulated. A 10 ms period is short
    # enough that the forwarder meets the contention many times per second
    # rather than in one long block.
    taskset -c "$cpus" python3 -c '
import sys, time
duty = float(sys.argv[1]) / 100.0
period = 0.01
on, off = period * duty, period * (1.0 - duty)
while True:
    end = time.monotonic() + on
    while time.monotonic() < end:
        pass
    if off > 0:
        time.sleep(off)
' "$pct" &>/dev/null &
    _CB_HOST_PIDS="$_CB_HOST_PIDS $!"

    # Resident memory, then swap. Two separate allocations because they are two
    # separate facts: the first squeezes the page cache and mqvpn's own
    # working set, the second forces the box to actually be swapping rather
    # than merely full.
    #
    # Held by a process that touches its pages once and then sleeps, so the
    # kernel is free to choose IT as the swap victim -- which is the realistic
    # shape, an idle service paged out while an active one runs.
    _cb_host_hold_mem "$CI_BENCH_BASELOAD_MEM_MB" resident
    _cb_host_hold_mem "$CI_BENCH_BASELOAD_SWAP_MB" swap
}

# Allocate and touch <mb> MiB, then hold it. mode=swap additionally madvises
# the region cold so the kernel prefers it as a swap victim.
_cb_host_hold_mem() {
    local mb="$1" mode="${2:-resident}"
    [ "${mb:-0}" -gt 0 ] 2>/dev/null || return 0
    python3 -c '
import mmap, sys, time
mb, mode = int(sys.argv[1]), sys.argv[2]
try:
    buf = mmap.mmap(-1, mb << 20)
except (OSError, ValueError):
    raise SystemExit(0)
# Touch every page so the pages are really committed, not just reserved.
for off in range(0, mb << 20, 4096):
    buf[off] = 1
if mode == "swap":
    try:
        # MADV_COLD (20) where available: marks the pages as reclaim
        # candidates without freeing them, so they migrate to swap under
        # pressure rather than being dropped.
        buf.madvise(20)
    except (AttributeError, OSError):
        pass
while True:
    time.sleep(3600)
' "$mb" "$mode" &>/dev/null &
    _CB_HOST_PIDS="$_CB_HOST_PIDS $!"
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
        vps_baseline)
            # Deliberately INSIDE the tier's cpuset, unlike noisy_neighbour:
            # this is the box's own existing services, which on a 1-vCPU
            # instance necessarily share the one core mqvpn runs on.
            _cb_host_vps_baseline "$(ci_bench_host_tier_cpus "$tier")"
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
