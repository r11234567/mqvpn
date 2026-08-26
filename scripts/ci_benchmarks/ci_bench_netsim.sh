#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
#
# ci_bench_netsim.sh — realistic-path emulation library.
#
# See docs/network_emulation_matrix.md for the design and the rationale behind
# every profile value. In short: a real path is an ACCESS leg (radio/CPE)
# followed by a TRANSIT leg (peering/backbone), so this builds each path as a
# CHAIN of namespaces with one qdisc per hop rather than a single veth pair:
#
#   NS_CLIENT ──access──> NS_HOP_i ──transit──> NS_SERVER
#
# That is what makes `6 backbones x 10 access legs` cost 16 profile entries
# instead of 192, and it is the only shape in which the interesting failures
# are reachable at all: bufferbloat needs a queue per hop, CGNAT needs two
# NAT points, and flapping transit must not reset access state.
#
# Delay values in the tables are ONE-WAY. Each leg is shaped on both of its
# ends (upstream and downstream), so a path's RTT is
# 2 x (access_delay + transit_delay). The existing bench_apply_netem uses the
# same convention.
#
# Sourced, not executed. Requires root (netns + tc).

# ── Topology naming ─────────────────────────────────────────────────────────
# Kept distinct from ci_bench_* and bench-* so a netsim run can never collide
# with a stale namespace from the plain benchmarks or the stress suite.
NETSIM_NS_SERVER="netsim-server"
NETSIM_NS_CLIENT="netsim-client"
NETSIM_NS_HOP_PREFIX="netsim-hop"

# Per-path second octet. Slots are 0-indexed.
NETSIM_OCTETS=(10 20 30 40 50 60 70 80)
NETSIM_MAX_PATHS=${#NETSIM_OCTETS[@]}

# The server's service address: a /32 on lo in NS_SERVER, reachable through
# every path. One address behind N paths is both what mqvpn expects (the client
# dials one endpoint) and what a real deployment looks like.
NETSIM_SERVER_ADDR="10.99.0.1"

# Ethernet MTU, kept on the client side of a PMTUD black hole so the sender has
# a reason to emit packets the far side cannot forward.
NETSIM_FULL_MTU=1500

netsim_hop_ns()          { echo "${NETSIM_NS_HOP_PREFIX}$1"; }
netsim_veth_cli()        { echo "ns${1}-c"; }   # in NS_CLIENT, faces the hop
netsim_veth_hop_cli()    { echo "ns${1}-hc"; }  # in the hop, faces the client
netsim_veth_hop_srv()    { echo "ns${1}-hs"; }  # in the hop, faces the server
netsim_veth_srv()        { echo "ns${1}-s"; }   # in NS_SERVER, faces the hop
netsim_cli_ip()          { echo "10.${NETSIM_OCTETS[$1]}.1.2"; }
netsim_hop_cli_ip()      { echo "10.${NETSIM_OCTETS[$1]}.1.1"; }
netsim_hop_srv_ip()      { echo "10.${NETSIM_OCTETS[$1]}.2.1"; }
netsim_srv_ip()          { echo "10.${NETSIM_OCTETS[$1]}.2.2"; }
# The address a roaming client appears to move TO. Same subnet as the hop's
# real server-facing address, so it needs no extra route to be reachable.
netsim_hop_srv_ip_alt()  { echo "10.${NETSIM_OCTETS[$1]}.2.9"; }

# ── Transit profiles (the six backbone classes) ────────────────────────────
#
# Two independent axes, and keeping them separate is the point of the table:
#
#   QUALITY  — loss, jitter shape, queue depth
#   CAPACITY — the rate cap, i.e. how small the pipe is
#
# The premium tiers are quality-rich and capacity-poor: a private line is
# flawless and tiny, an optimized-BGP port is near-flawless and moderate. The
# commodity tiers invert it: plain BGP gives you the widest pipe and pays for
# it in loss and peak-hour degradation; junk transit and a throttled carrier
# advertise capacity they cannot deliver. Rates below are calibrated so that
# against a ~380mbit plain-BGP reference, a private line yields ~50 and an
# optimized port ~150 — the "great network, small pipe" feel, which no single
# quality-only or capacity-only model reproduces.
#
# `limit` is in PACKETS and is sized against each profile's bandwidth-delay
# product: queue depth is what separates a well-managed line from a bloated
# one, so leaving it at netem's default 1000 would erase the difference
# between `iplc` and `bgp_junk`.
declare -gA NETSIM_TRANSIT=(
  # Private line (IPLC/IEPL): flawless and TINY. Aggregation has the most to
  # offer here precisely because one line cannot carry the load alone — the
  # scenario a customer buys multipath for.
  [iplc]="delay 65ms 1ms distribution normal rate 50mbit limit 500"

  # Optimized BGP: excellent quality, moderate pipe. Bigger than a private line
  # but still a fraction of what a plain transit port gives you — that gap is
  # the whole point of the tier and the reason it is priced the way it is.
  [bgp_opt]="delay 40ms 2ms distribution normal loss 0.01% rate 150mbit limit 900"

  # Plain BGP, off-peak. The WIDE pipe: it can nearly saturate, and its
  # weakness is quality (loss, peak-hour degradation), not capacity. This is
  # the reference the two tiers above are read against — a 380mbit-class port
  # where IPLC gets 50 and optimized BGP gets 150.
  [bgp_plain]="delay 80ms 8ms distribution normal loss 0.1% rate 380mbit limit 3200"
  [bgp_plain_peak]="delay 110ms 25ms distribution normal loss 0.8% rate 180mbit limit 4000"

  # Cogent-class junk. The load-bearing detail is `loss gemodel` rather than
  # `loss N%`: real bad lines lose in BURSTS, and burst loss is what defeats
  # congestion control. Uniform loss at the same average is a much easier
  # network and would flatter the scheduler. limit ~5x BDP = bloated.
  [bgp_junk]="delay 120ms 45ms distribution paretonormal loss gemodel 3% 20% 88% 0.5% rate 80mbit limit 6000"

  # Flapping BGP: base is bgp_plain; netsim_spike_start injects the storm.
  [bgp_flappy]="delay 80ms 8ms distribution normal loss 0.1% rate 300mbit limit 2500"

  # Carrier QoS (the China-carrier shape): the pipe looks huge but the PACKET
  # rate is capped, so bitrate collapses at small MTU. netsim_apply_pps_cap
  # adds the policer. This is the only profile whose optimization signal is
  # packet size rather than congestion control.
  [carrier_qos]="delay 150ms 90ms distribution paretonormal loss gemodel 2% 15% 80% 0.3% rate 900mbit limit 10000"

  # Not a product tier: a deliberately unconstrained path, so a benchmark whose
  # subject is the *server* has no network ceiling in the way. Used by the tier
  # sweep — against bgp_opt's 150mbit the faster tiers would all flatten out at
  # the link rate and the tier axis would read as "no difference".
  [lan]="delay 0.2ms rate 2000mbit limit 2000"
)

# Storm state for bgp_flappy / starlink, applied with `tc qdisc change` so the
# path mutates in place instead of being torn down and rebuilt (a rebuild would
# reset conntrack and the qdisc's queue, which is not what a flap does).
declare -gA NETSIM_SPIKE=(
  [bgp_flappy]="delay 600ms 250ms distribution paretonormal loss 6% rate 6mbit limit 8000"
  [starlink]="delay 300ms 60ms distribution paretonormal loss 2% rate 100mbit"
)

# Profiles that want a UDP packet-rate cap on top of their netem.
declare -gA NETSIM_PPS_CAP=(
  [carrier_qos]=8000
  [5g_throttled]=4000
)

# ── Access legs (client side) ──────────────────────────────────────────────
# Ten legs stand in for the ~32 requested combinations because signal x
# congestion x priority collapse onto the same three observables: delay, the
# shape of the jitter tail, and whether loss is bursty.
declare -gA NETSIM_ACCESS=(
  [eth]="delay 0.2ms rate 1000mbit"
  [wifi_good]="delay 3ms 2ms distribution normal loss 0.05% rate 400mbit"
  [wifi_busy]="delay 18ms 30ms distribution paretonormal loss 1.2% rate 60mbit limit 3000"
  [5g_full]="delay 18ms 6ms distribution normal loss 0.05% rate 350mbit"
  [5g_half]="delay 45ms 25ms distribution paretonormal loss 0.4% rate 90mbit"
  [5g_edge]="delay 95ms 60ms distribution paretonormal loss gemodel 4% 25% 80% 1% rate 12mbit limit 4000"
  [5g_throttled]="delay 130ms 85ms distribution paretonormal loss gemodel 5% 20% 85% 1.5% rate 8mbit limit 5000"
  [starlink]="delay 45ms 30ms distribution paretonormal loss 1% rate 200mbit"
  [geo_sat]="delay 320ms 25ms distribution normal loss 0.5% rate 20mbit limit 2000"
  # Phone tethering over PAN/USB-OTG. The duplicate/corrupt terms are the
  # "driver jank" — a tethering stack that occasionally re-sends or mangles a
  # frame — and the MTU is set separately by netsim_set_mtu.
  [tether_otg]="delay 45ms 25ms distribution paretonormal loss 0.4% rate 90mbit duplicate 0.3% corrupt 0.05%"
)

# MTU per access leg where it differs from 1500. An explicit `:mtu` in a path
# spec overrides this.
declare -gA NETSIM_LEG_MTU=(
  [tether_otg]=1400
)

# ── Client NAT behaviours ──────────────────────────────────────────────────
# Applied at the access hop, which is where a CPE or carrier NAT actually sits.
# `cgnat` additionally NATs at the transit hop, so the client is behind two
# translations — the shape a mobile subscriber usually has.
#
# These are the four RFC 3489 behaviours plus no-NAT. What separates them for a
# QUIC tunnel is whether the source port stays put: `symmetric` allocates a new
# port per destination, so a path that re-resolves or migrates looks like a
# different peer to the server and has to be re-validated.
declare -gA NETSIM_NAT=(
  [public]=""                                  # routed, no translation
  [full_cone]="--random-fully --persistent"    # one stable mapping for all peers
  [port_restricted]=""                         # plain masquerade (conntrack default)
  [symmetric]="--random"                       # fresh port per destination
  [cgnat]=""                                   # masquerade twice; see below
)

# netsim_apply_nat <slot> <nat_type>
#
# Idempotent per slot: the rule is added once at path-build time. `|| true`
# throughout because a kernel without a given masquerade flag must degrade to a
# plainer NAT rather than abort the whole scenario — netsim_detect_caps reports
# what was actually available.
netsim_apply_nat() {
    local slot="$1" nat="${2:-public}"
    local hop; hop="$(netsim_hop_ns "$slot")"
    local out; out="$(netsim_veth_hop_srv "$slot")"

    [ "$nat" = "public" ] && return 0
    if [ -z "${NETSIM_NAT[$nat]+x}" ]; then
        echo "netsim: unknown NAT type '$nat'" >&2
        return 1
    fi
    # Degrade to 'public' rather than failing the scenario; detect_caps said so.
    [ "$NETSIM_HAVE_NAT" = 1 ] || return 0

    # shellcheck disable=SC2086  # flags are intentionally word-split
    ip netns exec "$hop" iptables -t nat -A POSTROUTING -o "$out" \
        -j MASQUERADE ${NETSIM_NAT[$nat]} 2>/dev/null ||
        ip netns exec "$hop" iptables -t nat -A POSTROUTING -o "$out" \
            -j MASQUERADE 2>/dev/null || true

    # A second translation at the transit hop: the client's packets are
    # rewritten twice before they reach the server.
    if [ "$nat" = "cgnat" ]; then
        ip netns exec "$NETSIM_NS_SERVER" sysctl -qw net.ipv4.ip_forward=1 2>/dev/null || true
        ip netns exec "$hop" iptables -t nat -A POSTROUTING -o "$out" \
            -j MASQUERADE 2>/dev/null || true
    fi

    # UDP conntrack ages out in 30 s by default, which IS the "silent killer"
    # every mobile user meets. Pin it explicitly so the value is part of the
    # scenario rather than a distro default that might differ on the runner.
    ip netns exec "$hop" sysctl -qw net.netfilter.nf_conntrack_udp_timeout=30 \
        2>/dev/null || true
}

# netsim_roam <slot>
#
# Move the client's apparent source address, the way a handover does: replace
# the hop's SNAT with one to a different source and drop the conntrack entries
# holding the old mapping. Without the flush the live UDP flow keeps its
# original translation, the server never sees a new peer address, and the test
# passes while having exercised nothing — the same trap the D1 rebind e2e
# documents.
#
# Returns non-zero if the tools are missing, so a caller can label the run
# honestly instead of reporting a roam that never happened.
netsim_roam() {
    local slot="$1"
    local hop; hop="$(netsim_hop_ns "$slot")"
    local out; out="$(netsim_veth_hop_srv "$slot")"
    local alt; alt="$(netsim_hop_srv_ip_alt "$slot")"

    [ "$NETSIM_HAVE_NAT" = 1 ] || return 1
    command -v conntrack >/dev/null 2>&1 || return 1

    ip netns exec "$hop" ip addr add "${alt}/24" dev "$out" 2>/dev/null || true

    # Flush whatever translation is in place, then pin the new source. SNAT
    # rather than MASQUERADE: masquerade follows the interface's primary
    # address, which is precisely what must not change here.
    ip netns exec "$hop" iptables -t nat -F POSTROUTING 2>/dev/null || return 1
    ip netns exec "$hop" iptables -t nat -A POSTROUTING -o "$out" \
        -j SNAT --to-source "$alt" 2>/dev/null || return 1
    ip netns exec "$hop" conntrack -F >/dev/null 2>&1 || true
    return 0
}

# ── Path specs and scenario pairs ──────────────────────────────────────────
#
# A path is a free combination of the four axes:
#
#     <access>:<transit>[:<nat>[:<mtu>]]
#
# so "eth:bgp_plain:public" is a wired client on a public IP behind ordinary
# transit, and "5g_throttled:carrier_qos:symmetric:1400" is a phone on a
# throttled carrier behind symmetric NAT with a clamped MTU. Any access leg
# composes with any transit, any NAT and any MTU — the axes are independent by
# construction, which is what makes the interesting combinations cheap to write
# instead of needing a hand-authored profile each.
#
# A scenario pairs two such specs with '|'. Pairing is where the value is: a
# scheduler behaves the same across "two similar links" no matter what they
# are, and differently the moment the two legs disagree. The list below is
# therefore organised by the KIND of disagreement, not by enumerating pairs.
declare -gA NETSIM_CLASS=(
  # ── Baselines: what aggregation looks like with nothing wrong ──
  [homo_good]="eth:bgp_opt:public|eth:bgp_opt:public"
  [homo_bad]="5g_half:bgp_junk:port_restricted|5g_half:bgp_junk:port_restricted"

  # ── The headline case, and the per-commit gate ──
  # Wired public-IP client on a premium port, plus a barely-connected phone
  # behind carrier NAT on junk transit. Everything differs at once: capacity,
  # latency, loss burstiness, NAT, MTU. The question is whether the bad leg
  # drags the good one down, and it is where WLB / MinRTT / backup-FEC
  # actually separate.
  [hetero_extreme]="eth:bgp_opt:public|5g_edge:bgp_junk:symmetric:1400"

  # ── Capacity disagreement: tiny premium line + wide commodity port ──
  # The "great network, small pipe" pairing. A 50mbit private line next to a
  # 380mbit plain-BGP port: aggregation has to use both without letting the
  # small one become the head-of-line.
  [asym_capacity]="eth:iplc:public|eth:bgp_plain:public"
  # Same disagreement, but the small pipe is also the good one and the big
  # pipe is behind CGNAT — the realistic office-plus-mobile-backup shape.
  [premium_plus_mobile]="eth:iplc:public|5g_full:bgp_plain:cgnat"

  # ── Latency disagreement ──
  [asym_latency]="eth:bgp_opt:public|geo_sat:bgp_plain:port_restricted"

  # ── Stability disagreement ──
  [one_flapping]="eth:bgp_opt:public|eth:bgp_flappy:public"

  # ── Both legs throttled by the carrier: is the PPS cap symmetric? ──
  [carrier_pair]="5g_throttled:carrier_qos:symmetric:1400|5g_half:carrier_qos:cgnat:1400"

  # ── NAT disagreement with everything else held equal ──
  # Isolates NAT as the variable: identical links, one public, one symmetric.
  # A difference here is a NAT-handling bug, not a scheduling one.
  [nat_split]="eth:bgp_plain:public|eth:bgp_plain:symmetric"

  # ── MTU disagreement with everything else held equal ──
  # Isolates MTU. Pairs with carrier_qos below, where a PPS cap makes
  # bytes-per-packet the dominant term.
  [mtu_split]="eth:bgp_plain:public:1500|eth:bgp_plain:public:1280"
  # The axis, each entry against a fixed 1500 reference leg so a difference is
  # attributable to the one leg that changed. mtu_split above is the 1280 end
  # of the same axis; mtu_1500 is the control where nothing disagrees, which is
  # what makes the other two readable.
  [mtu_1500]="eth:bgp_plain:public:1500|eth:bgp_plain:public:1500"
  [mtu_1400]="eth:bgp_plain:public:1500|eth:bgp_plain:public:1400"
  # MTU under a packet-rate cap, where goodput scales with bytes-per-packet
  # rather than with bandwidth. The pair of these two is what turns MTU choice
  # and GSO batching into a number instead of a guess.
  [mtu_pps_1500]="5g_throttled:carrier_qos:public:1500|5g_throttled:carrier_qos:public:1500"
  [mtu_pps_1280]="5g_throttled:carrier_qos:public:1280|5g_throttled:carrier_qos:public:1280"
  # One clean leg, one PMTUD black hole. The black-holed leg cannot carry
  # full-size packets and never learns why, so this asks whether the tunnel
  # rides the good path or stalls behind the broken one — a hang, not a
  # slowdown, which is why it gets its own class.
  [mtu_blackhole]="eth:bgp_plain:public:1500|eth:bgp_plain:public:bh1280"

  # ── Server-bound reference, for the tier sweep ──
  # Two unconstrained paths, so whatever limits throughput is the server's own
  # CPU budget rather than the emulated network.
  [tier_ref]="eth:lan:public|eth:lan:public"

  # ── Real-world composites ──
  # Home fibre plus a tethered phone, the commonest consumer multipath setup.
  [home_plus_tether]="wifi_good:bgp_plain:port_restricted|tether_otg:carrier_qos:cgnat:1400"
  # Two mobile paths, one prioritised and one not, both behind carrier NAT.
  [dual_mobile]="5g_full:bgp_plain:cgnat|5g_throttled:carrier_qos:cgnat:1400"
  # Satellite primary with a congested-cell backup: both legs bad, differently.
  [sat_plus_cell]="starlink:bgp_plain:port_restricted|5g_edge:bgp_junk:symmetric:1400"
)

# ── Runtime capability detection ───────────────────────────────────────────
# Both features below are version-gated, and the runner is not this dev box:
# ubuntu-24.04 ships iproute2 6.1, which predates `netem seed`. Detect instead
# of assuming, and degrade to something honest.
NETSIM_HAVE_SEED=0
NETSIM_HAVE_PPS=0
NETSIM_HAVE_NAT=0
NETSIM_HAVE_ICMP_DROP=0

netsim_detect_caps() {
    local probe_ns="netsim-capprobe"
    ip netns del "$probe_ns" 2>/dev/null || true
    ip netns add "$probe_ns" || return 1
    ip netns exec "$probe_ns" ip link add p0 type dummy
    ip netns exec "$probe_ns" ip link set p0 up

    if ip netns exec "$probe_ns" tc qdisc replace dev p0 root \
           netem delay 10ms loss 1% seed 1 2>/dev/null; then
        NETSIM_HAVE_SEED=1
    fi

    ip netns exec "$probe_ns" tc qdisc replace dev p0 root handle 1: htb 2>/dev/null
    if ip netns exec "$probe_ns" tc filter add dev p0 parent 1: protocol ip u32 \
           match ip protocol 17 0xff \
           action police pkts_rate 1000 pkts_burst 100 drop 2>/dev/null; then
        NETSIM_HAVE_PPS=1
    fi

    # NAT needs the nat table plus conntrack. Without it every path silently
    # becomes `public`, which would quietly delete a whole axis of the matrix —
    # so it is reported, not assumed.
    if ip netns exec "$probe_ns" iptables -t nat -L >/dev/null 2>&1; then
        NETSIM_HAVE_NAT=1
    fi

    # A PMTUD black hole is built by dropping the ICMP that would report the
    # smaller MTU. Without the filter table there is no black hole, only a
    # small MTU — a slow path instead of a stalled one, which is a different
    # test wearing the same name.
    if ip netns exec "$probe_ns" iptables -A OUTPUT -p icmp \
           --icmp-type fragmentation-needed -j DROP >/dev/null 2>&1; then
        NETSIM_HAVE_ICMP_DROP=1
        ip netns exec "$probe_ns" iptables -D OUTPUT -p icmp \
            --icmp-type fragmentation-needed -j DROP >/dev/null 2>&1 || true
    fi

    ip netns del "$probe_ns" 2>/dev/null || true

    echo "netsim caps: netem-seed=$([ "$NETSIM_HAVE_SEED" = 1 ] && echo yes || echo NO)" \
         "pps-police=$([ "$NETSIM_HAVE_PPS" = 1 ] && echo yes || echo NO)" \
         "nat=$([ "$NETSIM_HAVE_NAT" = 1 ] && echo yes || echo NO)" \
         "icmp-drop=$([ "$NETSIM_HAVE_ICMP_DROP" = 1 ] && echo yes || echo NO)"
    if [ "$NETSIM_HAVE_SEED" != 1 ]; then
        echo "  note: no netem seed on this iproute2 — loss/jitter patterns vary" \
             "run-to-run; compensate with longer runs, not more repeats."
    fi
    if [ "$NETSIM_HAVE_PPS" != 1 ]; then
        echo "  note: no 'police pkts_rate' — carrier_qos/5g_throttled lose their" \
             "packet-rate cap and become ordinary high-bandwidth profiles."
    fi
    if [ "$NETSIM_HAVE_NAT" != 1 ]; then
        echo "  note: no iptables nat table — every path degrades to 'public'," \
             "so NAT-dependent scenarios measure something easier than intended."
    fi
    if [ "$NETSIM_HAVE_ICMP_DROP" != 1 ]; then
        echo "  note: cannot drop ICMP frag-needed — a 'bh' MTU degrades to an" \
             "ordinary small MTU, measuring a slow path rather than a stalled one."
    fi
}

# Append `seed N` when supported. Seeding removes netem's own PRNG as a
# variance source, which dominates loss-sensitive metrics — but it does NOT
# make a run deterministic overall (the runner's scheduling still varies).
netsim_seeded() {
    local spec="$1" seed="${2:-0}"
    if [ "$NETSIM_HAVE_SEED" = 1 ] && [ "$seed" != 0 ]; then
        echo "$spec seed $seed"
    else
        echo "$spec"
    fi
}

# ── Topology ───────────────────────────────────────────────────────────────

netsim_teardown() {
    local i
    ip netns del "$NETSIM_NS_CLIENT" 2>/dev/null || true
    ip netns del "$NETSIM_NS_SERVER" 2>/dev/null || true
    for (( i=0; i<NETSIM_MAX_PATHS; i++ )); do
        ip netns del "$(netsim_hop_ns "$i")" 2>/dev/null || true
        ip link del "$(netsim_veth_cli "$i")" 2>/dev/null || true
        ip link del "$(netsim_veth_srv "$i")" 2>/dev/null || true
    done
}

# netsim_setup <n_paths>
#
# Builds client + server + one hop namespace per path, wires
# client<->hop<->server, and installs routing so the single server address is
# reachable over every path.
netsim_setup() {
    local n="${1:-2}"
    if [ "$n" -lt 1 ] || [ "$n" -gt "$NETSIM_MAX_PATHS" ]; then
        echo "netsim_setup: need 1..${NETSIM_MAX_PATHS} paths, got $n" >&2
        return 1
    fi

    netsim_teardown
    ip netns add "$NETSIM_NS_CLIENT" || return 1
    ip netns add "$NETSIM_NS_SERVER" || return 1
    ip netns exec "$NETSIM_NS_CLIENT" ip link set lo up
    ip netns exec "$NETSIM_NS_SERVER" ip link set lo up

    # One address, many paths — matches how a real server is reached.
    ip netns exec "$NETSIM_NS_SERVER" \
        ip addr add "${NETSIM_SERVER_ADDR}/32" dev lo || return 1

    local i hop vc vhc vhs vs
    for (( i=0; i<n; i++ )); do
        hop="$(netsim_hop_ns "$i")"
        vc="$(netsim_veth_cli "$i")";     vhc="$(netsim_veth_hop_cli "$i")"
        vhs="$(netsim_veth_hop_srv "$i")"; vs="$(netsim_veth_srv "$i")"

        ip netns add "$hop" || return 1
        ip netns exec "$hop" ip link set lo up
        ip netns exec "$hop" sysctl -qw net.ipv4.ip_forward=1

        # client <-> hop
        ip link add "$vc" type veth peer name "$vhc" || return 1
        ip link set "$vc" netns "$NETSIM_NS_CLIENT"
        ip link set "$vhc" netns "$hop"
        ip netns exec "$NETSIM_NS_CLIENT" ip addr add "$(netsim_cli_ip "$i")/24" dev "$vc"
        ip netns exec "$hop" ip addr add "$(netsim_hop_cli_ip "$i")/24" dev "$vhc"
        ip netns exec "$NETSIM_NS_CLIENT" ip link set "$vc" up
        ip netns exec "$hop" ip link set "$vhc" up

        # hop <-> server
        ip link add "$vhs" type veth peer name "$vs" || return 1
        ip link set "$vhs" netns "$hop"
        ip link set "$vs" netns "$NETSIM_NS_SERVER"
        ip netns exec "$hop" ip addr add "$(netsim_hop_srv_ip "$i")/24" dev "$vhs"
        ip netns exec "$NETSIM_NS_SERVER" ip addr add "$(netsim_srv_ip "$i")/24" dev "$vs"
        ip netns exec "$hop" ip link set "$vhs" up
        ip netns exec "$NETSIM_NS_SERVER" ip link set "$vs" up

        # Client -> server, one route per path. mqvpn binds each path socket
        # with SO_BINDTODEVICE, so the oif-scoped lookup selects the route
        # whose dev matches; the metric only orders unbound traffic. Metrics
        # must differ or the kernel rejects the duplicate.
        ip netns exec "$NETSIM_NS_CLIENT" ip route add "${NETSIM_SERVER_ADDR}/32" \
            via "$(netsim_hop_cli_ip "$i")" dev "$vc" metric "$((100 + i))" 2>/dev/null || true

        # Hop -> server address, and server -> client subnet. Return traffic
        # follows the same hop it arrived through, so each path stays
        # independent (a flap on one must not perturb the other).
        ip netns exec "$hop" ip route add "${NETSIM_SERVER_ADDR}/32" \
            via "$(netsim_srv_ip "$i")" dev "$vhs" 2>/dev/null || true
        # `src` is load-bearing, not cosmetic. mqvpn's server answers on ONE
        # wildcard-bound UDP socket with a plain sendto() and never sets
        # IP_PKTINFO, so the kernel chooses the reply's source address by
        # looking up this route. NETSIM_SERVER_ADDR lives only on lo, so
        # without `src` the reply goes out as 10.<octet>.2.2 — an address the
        # client never dialled. The client drops it, the handshake never
        # completes, and every scenario reports 0 Mbps. ci_bench_env.sh is
        # immune only because there the service address *is* the directly
        # connected server veth; netsim is the first topology to put it behind
        # a router hop.
        ip netns exec "$NETSIM_NS_SERVER" ip route add "10.${NETSIM_OCTETS[$i]}.1.0/24" \
            via "$(netsim_hop_srv_ip "$i")" dev "$vs" \
            src "$NETSIM_SERVER_ADDR" 2>/dev/null || true
    done

    # Loose rp_filter: several /32 routes to one address over different devices
    # is exactly the shape strict mode drops.
    local ns
    for ns in "$NETSIM_NS_CLIENT" "$NETSIM_NS_SERVER"; do
        ip netns exec "$ns" sysctl -qw net.ipv4.conf.all.rp_filter=2
    done

    # Verify each path end to end BEFORE any shaping is applied — an
    # unreachable path must fail here, not silently produce a zero later.
    for (( i=0; i<n; i++ )); do
        if ! ip netns exec "$NETSIM_NS_CLIENT" ping -c 1 -W 2 \
                -I "$(netsim_veth_cli "$i")" "$NETSIM_SERVER_ADDR" >/dev/null 2>&1; then
            echo "netsim_setup: path $i cannot reach $NETSIM_SERVER_ADDR" >&2
            return 1
        fi
    done

    # Reachability is not enough. The ping above is sourced from the client's
    # veth address, so it succeeds on a topology whose *service* address is
    # unusable — which is exactly the state that made every scenario read zero
    # while this check reported the path healthy. Assert the other direction
    # too: the server must answer each client from NETSIM_SERVER_ADDR.
    local want_src
    for (( i=0; i<n; i++ )); do
        want_src="$(ip netns exec "$NETSIM_NS_SERVER" ip -o route get "$(netsim_cli_ip "$i")" \
                    2>/dev/null | sed -n 's/.* src \([0-9.][0-9.]*\).*/\1/p')"
        if [ "$want_src" != "$NETSIM_SERVER_ADDR" ]; then
            echo "netsim_setup: server would answer path $i from ${want_src:-<none>}," \
                 "not $NETSIM_SERVER_ADDR — the client would drop the reply" >&2
            return 1
        fi
    done

    echo "OK: netsim topology up — $n path(s), 2 hops each, server ${NETSIM_SERVER_ADDR}"
}

# netsim_apply_path <slot> <spec> [seed]
#
# spec = "<access>:<transit>[:<nat>[:<mtu>]]"
#
# Shapes both legs of one path, installs the NAT and clamps the MTU. Each leg
# is shaped on BOTH ends, so RTT is 2 x (access_delay + transit_delay).
netsim_apply_path() {
    local slot="$1" spec="$2" seed="${3:-0}"

    # Split on ':' — the axes are positional, and only the first two are
    # required, so an older two-field spec keeps working unchanged.
    local IFS=:
    read -r access transit nat mtu <<<"$spec"
    unset IFS
    nat="${nat:-public}"

    local aspec="${NETSIM_ACCESS[$access]:-}"
    local tspec="${NETSIM_TRANSIT[$transit]:-}"
    if [ -z "$aspec" ]; then echo "netsim: unknown access leg '$access'" >&2; return 1; fi
    if [ -z "$tspec" ]; then echo "netsim: unknown transit profile '$transit'" >&2; return 1; fi

    local hop vc vhc vhs vs
    hop="$(netsim_hop_ns "$slot")"
    vc="$(netsim_veth_cli "$slot")";     vhc="$(netsim_veth_hop_cli "$slot")"
    vhs="$(netsim_veth_hop_srv "$slot")"; vs="$(netsim_veth_srv "$slot")"

    # Distinct seeds per direction/leg: one seed everywhere would correlate
    # upstream and downstream loss, which no real path does.
    local a_up a_dn t_up t_dn
    a_up="$(netsim_seeded "$aspec" "$((seed))")"
    a_dn="$(netsim_seeded "$aspec" "$((seed + 1))")"
    t_up="$(netsim_seeded "$tspec" "$((seed + 2))")"
    t_dn="$(netsim_seeded "$tspec" "$((seed + 3))")"

    ip netns exec "$NETSIM_NS_CLIENT" tc qdisc replace dev "$vc"  root netem ${a_up} || return 1
    ip netns exec "$hop"              tc qdisc replace dev "$vhc" root netem ${a_dn} || return 1
    ip netns exec "$hop"              tc qdisc replace dev "$vhs" root netem ${t_up} || return 1
    ip netns exec "$NETSIM_NS_SERVER" tc qdisc replace dev "$vs"  root netem ${t_dn} || return 1

    # An explicit MTU in the spec wins; otherwise the access leg's own clamp
    # applies (it is the CPE or radio that sets it). A `bh` prefix asks for the
    # black-hole variant at that size rather than a clean clamp.
    mtu="${mtu:-${NETSIM_LEG_MTU[$access]:-}}"
    if [ -n "$mtu" ]; then
        if [ "${mtu#bh}" != "$mtu" ]; then
            netsim_set_pmtud_blackhole "$slot" "${mtu#bh}"
        else
            netsim_set_mtu "$slot" "$mtu"
        fi
    fi

    netsim_apply_nat "$slot" "$nat" || return 1

    # Packet-rate cap, if either leg calls for one. Applied on the hop's
    # server-facing side so it throttles the uplink the way a carrier does.
    local pps="${NETSIM_PPS_CAP[$transit]:-${NETSIM_PPS_CAP[$access]:-}}"
    if [ -n "$pps" ]; then netsim_apply_pps_cap "$slot" "$pps"; fi

    echo "  path $slot: ${access} + ${transit} + nat=${nat}${mtu:+ mtu=$mtu}$([ -n "$pps" ] && echo " pps<=${pps}")"
}

# netsim_set_mtu <slot> <mtu> — clamp every device on the path.
netsim_set_mtu() {
    local slot="$1" mtu="$2" hop
    hop="$(netsim_hop_ns "$slot")"
    ip netns exec "$NETSIM_NS_CLIENT" ip link set "$(netsim_veth_cli "$slot")" mtu "$mtu"
    ip netns exec "$hop" ip link set "$(netsim_veth_hop_cli "$slot")" mtu "$mtu"
    ip netns exec "$hop" ip link set "$(netsim_veth_hop_srv "$slot")" mtu "$mtu"
    ip netns exec "$NETSIM_NS_SERVER" ip link set "$(netsim_veth_srv "$slot")" mtu "$mtu"
}

# netsim_set_pmtud_blackhole <slot> <hop_mtu>
#
# The MTU failure that hangs a tunnel instead of slowing it. Three things have
# to be true at once, and leaving out any one of them produces a merely slow
# path:
#
#   1. the client side keeps a full-size MTU, so the sender goes on emitting
#      packets too big for the far side;
#   2. the hop's server-facing link is smaller, so forwarding them is
#      impossible and the kernel drops them (QUIC sets DF, so there is no
#      fragmentation to fall back on);
#   3. the ICMP Frag Needed that would report the smaller MTU is dropped, so
#      path MTU discovery never converges and the sender retransmits the same
#      oversized packet indefinitely.
#
# Only the client->server direction is black-holed. The reverse stays clean,
# which is both the realistic shape and what lets a stalled upload be told
# apart from a dead path.
netsim_set_pmtud_blackhole() {
    local slot="$1" mtu="$2" hop
    hop="$(netsim_hop_ns "$slot")"

    ip netns exec "$NETSIM_NS_CLIENT" \
        ip link set "$(netsim_veth_cli "$slot")" mtu "$NETSIM_FULL_MTU"
    ip netns exec "$hop" \
        ip link set "$(netsim_veth_hop_cli "$slot")" mtu "$NETSIM_FULL_MTU"
    ip netns exec "$hop" \
        ip link set "$(netsim_veth_hop_srv "$slot")" mtu "$mtu"
    ip netns exec "$NETSIM_NS_SERVER" \
        ip link set "$(netsim_veth_srv "$slot")" mtu "$mtu"

    if [ "$NETSIM_HAVE_ICMP_DROP" = 1 ]; then
        ip netns exec "$hop" iptables -A OUTPUT -p icmp \
            --icmp-type fragmentation-needed -j DROP 2>/dev/null || true
    fi
}

# netsim_apply_pps_cap <slot> <pps>
#
# The carrier signature: a fat pipe with a packet-rate ceiling, so goodput
# depends on bytes-per-packet. Silently skipped when the kernel/iproute2 lacks
# `police pkts_rate` — netsim_detect_caps already warned, and a missing cap is
# better than a hard failure that blocks every other scenario.
netsim_apply_pps_cap() {
    local slot="$1" pps="$2" hop
    [ "$NETSIM_HAVE_PPS" = 1 ] || return 0
    hop="$(netsim_hop_ns "$slot")"
    local dev; dev="$(netsim_veth_hop_srv "$slot")"
    ip netns exec "$hop" tc qdisc replace dev "$dev" handle ffff: ingress 2>/dev/null || true
    ip netns exec "$hop" tc filter add dev "$dev" parent ffff: protocol ip u32 \
        match ip protocol 17 0xff \
        action police pkts_rate "$pps" pkts_burst "$((pps / 40 + 50))" drop 2>/dev/null || true
}

# ── Time-varying paths ─────────────────────────────────────────────────────

# The running spike driver, or empty. A global rather than a return value on
# purpose: reporting the PID on stdout meant the caller had to read it through
# `$(...)`, and the backgrounded loop inherits the write end of that command
# substitution's pipe. Because the loop never exits, the pipe never reached
# EOF and the substitution blocked forever — before the server was even
# started, which is how `classes`, `combo` and `sched` each burned their full
# 60-minute job timeout while the modes with no flapping transit finished in
# 3-10 minutes. Assigning here also makes the job a direct child of the calling
# shell, so netsim_spike_stop's `wait` is meaningful instead of an error it has
# to swallow.
NETSIM_SPIKE_PID=""

# netsim_spike_start <slot> <profile> <on_sec> <off_min> <off_max>
#
# Background driver for bgp_flappy / starlink. Uses `tc qdisc change` so the
# path mutates in place: tearing it down and re-adding would reset the queue
# and conntrack, which is a different event than a flap and would let the code
# under test off easy. Sets NETSIM_SPIKE_PID for netsim_spike_stop.
netsim_spike_start() {
    local slot="$1" profile="$2" on_sec="${3:-10}" off_min="${4:-45}" off_max="${5:-90}"
    local base="${NETSIM_TRANSIT[$profile]:-}" spike="${NETSIM_SPIKE[$profile]:-}"
    if [ -z "$spike" ]; then echo "netsim: no spike defined for '$profile'" >&2; return 1; fi

    local hop dev_up dev_dn
    hop="$(netsim_hop_ns "$slot")"
    dev_up="$(netsim_veth_hop_srv "$slot")"
    dev_dn="$(netsim_veth_srv "$slot")"

    (
        while :; do
            sleep "$(( off_min + RANDOM % (off_max - off_min + 1) ))"
            ip netns exec "$hop" tc qdisc change dev "$dev_up" root netem ${spike} 2>/dev/null
            ip netns exec "$NETSIM_NS_SERVER" tc qdisc change dev "$dev_dn" root netem ${spike} 2>/dev/null
            sleep "$on_sec"
            ip netns exec "$hop" tc qdisc change dev "$dev_up" root netem ${base} 2>/dev/null
            ip netns exec "$NETSIM_NS_SERVER" tc qdisc change dev "$dev_dn" root netem ${base} 2>/dev/null
        done
    ) >/dev/null 2>&1 &
    NETSIM_SPIKE_PID=$!
}

# Stops the driver started above. Takes no argument; an ignored one is accepted
# so an older call site cannot silently stop stopping anything.
netsim_spike_stop() {
    [ -n "${NETSIM_SPIKE_PID:-}" ] || return 0
    kill "$NETSIM_SPIKE_PID" 2>/dev/null || true
    wait "$NETSIM_SPIKE_PID" 2>/dev/null || true
    NETSIM_SPIKE_PID=""
}

# netsim_apply_class <class> [seed] — build both paths of a heterogeneity class.
netsim_apply_class() {
    local class="$1" seed="${2:-0}"
    local spec="${NETSIM_CLASS[$class]:-}"
    if [ -z "$spec" ]; then echo "netsim: unknown class '$class'" >&2; return 1; fi

    local a="${spec%%|*}" b="${spec##*|}"
    echo "class ${class}:"
    netsim_apply_path 0 "$a" "$seed"          || return 1
    netsim_apply_path 1 "$b" "$((seed + 10))" || return 1
}

# netsim_path_field <spec> <access|transit|nat|mtu> — one axis out of a spec.
# Positional parsing lives here so no caller has to repeat it.
netsim_path_field() {
    local spec="$1" want="$2"
    local IFS=:
    read -r access transit nat mtu <<<"$spec"
    unset IFS
    case "$want" in
      access)  echo "$access" ;;
      transit) echo "$transit" ;;
      nat)     echo "${nat:-public}" ;;
      mtu)     echo "${mtu:-}" ;;
      *)       return 1 ;;
    esac
}

# Which transit profile a class puts on a given slot — the spike driver needs
# it to know whether that leg has a storm state.
netsim_class_transit() {
    local class="$1" slot="$2"
    local spec="${NETSIM_CLASS[$class]:-}"
    [ -n "$spec" ] || return 1
    local leg
    if [ "$slot" = 0 ]; then leg="${spec%%|*}"; else leg="${spec##*|}"; fi
    netsim_path_field "$leg" transit
}
