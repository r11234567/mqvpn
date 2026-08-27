# Realistic network emulation matrix — status and completion plan

Two tiers only, matching the existing workflows: **per-commit** (`perf.yml`, push
to `main`) is a fast smoke test on one bad network; **weekly**
(`perf-weekly.yml`) carries the matrix. There is no nightly tier.

---

## 0. Honest status

The endpoint half now exists. What remains undone is the topology half — hop
count and geography — plus one item that turns out to be blocked on the client
rather than on the harness.

| Requested | Status | Where |
|---|---|---|
| 6 backbone classes (IPLC, optimized/plain/junk/flapping BGP, carrier QoS) | **done** — 8 profiles incl. an unconstrained `lan` reference | `NETSIM_TRANSIT` |
| Client access: eth / WiFi / 5G / satellite / tethering | **approximated** — 10 legs stand in for ~32 combinations | `NETSIM_ACCESS` |
| Heterogeneity-class aggregation | **done** — 19 classes | `NETSIM_CLASS` |
| **Many hops to the server** | **NOT done** — exactly one intermediate namespace (2 hops) | — |
| **Geographic detours (Asia→EU via US, Asia→US via EU)** | **NOT done** | — |
| Asymmetric routing (forward ≠ return) | **done** — four netem qdiscs per path, independently seeded per direction | `netsim_apply_path` |
| MTU large/small | **done** — 1500/1400/1280 axis, crossed with a PPS cap, plus a PMTUD black hole | `mtu` mode |
| Client NAT: public + 4 types | **done** — all five, at the access hop | `NETSIM_NAT` |
| **Server tier (1c1g / 2c2g, GB5 400–2000)** | **done** — four tiers as cgroup caps, labelled nominal | `ci_bench_host.sh` |
| **Host (母鸡) state: healthy / loaded / softirq storm** | **done** — plus a mid-run CPU throttle | `ci_bench_host.sh` |
| Server 2.5G / client 1G line rates | **NOT done, and partly infeasible** — see §5 | — |
| 5 special conditions | **4 of 5** — NAT aging, corrupt/reorder, ACK starvation, roaming under load | `run_special` |
| Dual-stack routing split | **blocked on the client, not the harness** — see §2.5 | — |
| Scheduler comparison across classes | **done** — WLB / MinRTT / backup-FEC over the three diverging classes | `sched` mode |

The matrix now answers "how does a real deployment behave" for everything
except *where the server is*: every path is still one hop wide, so a detour
through another continent is modelled as latency rather than as a route.

### 0.1 The matrix is built, but it had never produced a measurement

Everything marked **done** above means *the scenario exists and runs*, not that
it has yielded a usable number. In the 2026-08-26 weekly run
([32942370465](https://github.com/r11234567/mqvpn/actions/runs/32942370465))
**every netsim scenario reported `0.0 Mbps`** — 64 result rows across eight
jobs, not one of them non-zero — and three of the eleven jobs never finished at
all. The per-commit `netsim_percommit` row is 0.0 as well, so this has been true
since the harness was first wired in, not a recent regression.

Two independent defects, both in the harness rather than in mqvpn. **Both are
fixed below; neither is confirmed yet.** The deadlock (B) is proven fixed by
local reproduction, but nothing here can be called verified until a weekly run
comes back with non-zero numbers — read the next run before trusting §2-§4.

**A. The tunnel never establishes, so every scenario reads zero.**
`NETSIM_SERVER_ADDR` (`10.99.0.1`) exists only as a `/32` on `lo` in the server
namespace, and the server answers on one wildcard-bound UDP socket with a plain
`sendto()` (`src/mqvpn_server.c:765`; there is no `IP_PKTINFO` anywhere in
`src/`). The kernel therefore picks the reply's source address by route lookup
toward the client, which yields the server's veth address `10.<octet>.2.2` — not
the `10.99.0.1` the client dialled. The client drops the reply, the handshake
never completes, and the server closes 15 s later:

```
xqc_conn_destroy ... hsk_recv:0 | handshake_time:0 | close_msg:idle timeout
                     l-0.0.0.0-4433-...  p-10.10.1.2-47462-...
```

`ci_bench_env.sh` never hit this because there the service address `10.100.0.1`
*is* the directly-connected server veth, so the route-selected source and the
dialled destination coincide. netsim is the first topology to put the service
address behind a router hop.

**Fixed** by giving every server-side route back to a client subnet an explicit
preferred source, in `netsim_setup`:

```bash
ip netns exec "$NETSIM_NS_SERVER" ip route add "10.${NETSIM_OCTETS[$i]}.1.0/24" \
    via "$(netsim_hop_srv_ip "$i")" dev "$vs" src "$NETSIM_SERVER_ADDR"
```

The `ping -I <dev>` reachability check that closes `netsim_setup` sources from
the veth address, so it passed on a topology whose service address was
unusable — it certified the broken path as healthy for as long as this bug
existed. `netsim_setup` now also asserts, per path, that
`ip route get <client>` in the server namespace reports
`src $NETSIM_SERVER_ADDR`, and fails the scenario when it does not. A
regression here now stops the job instead of publishing zeros.

**B. Any class with a flapping transit hangs its job until the 60-minute cap.**
`netsim_spike_start` backgrounds an infinite loop and reports its PID on stdout,
and the caller reads that PID through command substitution:

```bash
SPIKE_PID="$(netsim_spike_start 1 "$t1" 8 20 35)"
```

The backgrounded subshell inherits the write end of the substitution's pipe and
never exits, so the pipe never reaches EOF and `$( )` blocks forever — before
the server is even started. `bgp_flappy` is the only spike profile any class
actually selects, which is exactly why `classes`, `combo` and `sched` (the three
modes containing `one_flapping` or a `bgp_flappy` leg) time out, while `mtu`,
`tiers`, `catalog` and `special` finish in 3-10 minutes. In the `sched` job the
last lines are the two `path` announcements of `one_flapping`, then fifty
minutes of silence.

**Fixed** by not returning the PID through stdout at all: `netsim_spike_start`
now assigns `NETSIM_SPIKE_PID` in the caller's own shell and redirects the
loop's own output to `/dev/null`, and `netsim_spike_stop` reads that global.
The driver is now a direct child of the calling shell, so its `wait` is
meaningful rather than an error the old code had to swallow.

**C. A job that times out loses even the scenarios that succeeded.** The results
JSON is written once, after the mode's loop finishes, so all three hung jobs
uploaded nothing (`No files were found with the provided path:
ci_bench_results/*.json`). Append each row as it is produced, or emit the
document from the `EXIT` trap.

Until a run comes back green *with numbers in it*, the §0 table should still be
read as "scenario implemented", never as "scenario measured". The estimates in
§4 are built on timings from runs that carried no traffic and will need redoing
against a real one.

### 0.2 First run with the fixes: it measures now, but the multipath numbers do not hold up

Run [33024352078](https://github.com/r11234567/mqvpn/actions/runs/33024352078)
(2026-08-26, commit `90b746c`) is the first weekly to finish. All eleven netsim
jobs completed — `classes` 19m10s, `combo` 14m54s, `sched` 12m36s, the three
that used to be cancelled at the 60-minute cap — and **76 of 97 result rows
carry a non-zero throughput, against 0 of 64 the run before**. Both §0.1
defects are confirmed fixed in CI, not just locally.

That is as far as the good news goes. With numbers finally coming out, five
things are visible that zeros were hiding. A blocks every multipath conclusion
in the matrix and should be fixed before anything in §2 is read.

**A. `run_pair` measures one path three times.** `hetero_extreme`'s path B is
`5g_edge:bgp_junk:symmetric:1400` — one-way 95 ms + 120 ms and a 12 Mbit cap,
so RTT ≈ 430 ms and no more than 12 Mbps. It reported `solo_b = 111.8 Mbps`
with `min_rtt_ms = 71`, which is path A's `eth:bgp_opt` (2 x (0.2 + 40) ≈ 80 ms).
`asym_latency`'s path B is `geo_sat:bgp_plain` — RTT ≈ 800 ms, 20 Mbit — and
reported 108.3 Mbps at `min_rtt_ms = 75`. `asym_capacity` put `eth:iplc`
against `eth:bgp_plain` and returned 36.9 / 37.4 / 37.4 at `min_rtt 127`, all
three of them path A's private line. `min_rtt_ms` is `max()` over the server's
path list, so a second, slower path would raise it; it never does.

Every such row lands at `aggregation_efficiency` ≈ 0.50 and `vs_best_single`
≈ 1.00, which is just the arithmetic of `mp == a == b`. The core suite, which
runs on `ci_bench_env.sh`'s topology and is untouched by this, shows what a
working pair looks like in the same run: `equal_paths` single 45.3 → WLB 89.1.

The cause is that `measure_pathset` is invoked as `< <(measure_pathset ...)`.
Process substitution is a subshell, so the `_CB_CLIENT_PID` that
`ci_bench_start_client` assigns never reaches `run_pair`'s shell. Its
"kill previous client" guard therefore always sees an empty PID, and
`ci_bench_stop_vpn` only ever kills the server. Three clients accumulate inside
one `netsim_setup` and stay connected; the first owns the TUN and the route to
the tunnel address, so measurements two and three travel over path 0, and
`collect_stats` reads `clients[0]` — that same first client, which has one
path. `catalog` and `tiers` escape it: catalog tears the namespaces down
between scenarios, which strands a leaked client in a dead netns, and
`run_tier` measures only once.

Fix: get the PID out of the subshell (start the client in `run_pair`'s own
shell, or have `measure_pathset` write the PID to a file the caller reads), and
give `ci_bench_stop_vpn` a way to reap a client it did not start — matching on
the netns and binary — so a leak cannot persist silently. Until that lands,
`solo_b`, `multipath_mbps`, `aggregation_efficiency` and `vs_best_single` are
invalid for every `run_pair` mode: `classes`, `combo`, `mtu`, `sched` and the
per-commit gate.

**B. 21 rows are still zero, and a zero still means two different things.** They
cluster on exactly the profiles whose loss is `loss gemodel` — `bgp_junk`,
`carrier_qos`, `5g_throttled` — plus the legs that pair with them. Each carries
`bytes_tx: 684`, `bytes_rx: 0`, `gso_factor: 1.0`: the handshake never
completed and `ci_bench_wait_tunnel` expired three times over (87 s for such a
scenario against ~77 s for one that measures). It is marginal rather than
impossible — `catalog bgp_junk+eth` reaches 0.5 Mbps and `+5g_half` 0.9 — so
the wait deserves to be longer and configurable. Separately, a `0.0` that means
"never connected" must stop being written as the same value as a `0.0` that
means "measured, and it was zero"; the row needs a reason field.

**C. `carrier_qos` measures the policer, not a carrier.** Every row on that
transit is 0.2-0.5 Mbps whatever the access leg. At the configured 8000 pps and
a 1400-byte MTU its ceiling should be around 90 Mbps, so the profile sits two
orders of magnitude under its own design intent. Note also that
`netsim_apply_pps_cap` attaches the policer to the **ingress** qdisc of the
hop's server-facing device, which is the server→client direction, while the
comment at its call site says it throttles the uplink. Settle which was meant
before recalibrating.

**D. A 6-second single-stream sample cannot fill a high-BDP path.**
`ci_bench_run_iperf` is called as `TCP DL`, 6 s, one stream. One inner TCP
stream needs rate x RTT of window, and `bgp_plain` at 380 Mbit / 160 ms RTT
needs 7.6 MB, which six seconds of slow start will not reach. The catalog says
so directly: the widest transit is the slowest, `bgp_plain+eth` at 20.6 Mbps
against `iplc+eth` at 37.6 on a pipe less than a seventh the size, and
`bgp_opt+eth` at 110.3. Above roughly 100 Mbit x 100 ms the number describes
the sample, not the path. Lengthen the sample, raise the stream count, or say
in the results that the high-capacity profiles are not measurable this way.

**E. `special` silently reported half of itself.** `run_special` writes four
rows by construction; the artifact has two, `corrupt_reorder` and
`ack_starvation`. `nat_aging` and `roam_under_load` sit inside
`if start_server && start_client && wait_tunnel; then` and emit nothing when
that fails — no row, no warning, and the job still reports success. It finished
in 2m52s, less than `nat_aging`'s own 35-second idle. The failure path has to
write a row saying why.

**What does look right.** `tiers` is the one multipath mode not built on
`run_pair`, and it behaves: 813 / 900 / 917 / 925 Mbps across the four
instance sizes on the unconstrained `lan` profile, falling to 715 under a
softirq storm and 445 under a mid-run CPU throttle. The `catalog` modes rank
their access legs sensibly within a transit. Those are the rows worth reading
today.

---

## 1. Three blockers, before more coverage is worth adding

### 1.1 A product bug that invalidates lossy measurements — `XQC_ELIMIT`

Found by the hybrid-e2e failures. The server closes the connection with
`FRAME_ENCODING_ERROR` (0x7) tens of MB into a transfer over a lossy,
high-jitter, multipath route. The chain, from the collected logs:

```
xqc_insert_stream_frame  error  -613  (stream_id:16)   <- XQC_ELIMIT
xqc_process_frames       process frame error  -613
xqc_engine_packet_process  fail to process packets  ret:-613
                         -> conn closed, err:0x7, close_msg:local error  (server)
                                        err:0x7, close_msg:remote error (client)
```

`src/transport/xqc_frame.c:101` rejects a STREAM frame once
`buffered_frame_count >= XQC_MAX_STREAM_FRAME_BUFFERED_COUNT` (8192), a CWE-770
mitigation from upstream commit `e4d89de`. The cap itself is reasonable. Two
things around it are not:

- **It contradicts the advertised flow-control window.**
  `XQC_MAX_RECV_WINDOW` is 16 MiB per stream, so a peer is told it may send
  16 MiB of unread stream data. mqvpn sends 1400-byte packets
  (`MQVPN_MAX_PKT_OUT_SIZE`), so 8192 frames is only **~11.5 MB** — less than
  the window. A peer obeying flow control can be killed for exceeding a limit
  it was never told about.
- **Hitting a resource cap is treated as a protocol violation.** The caller
  (`xqc_frame.c:722`) special-cases only `-XQC_EDUP_FRAME`; every other
  non-zero return falls into `goto error`, which kills the connection. "I have
  no room to buffer this" and "you sent me a malformed frame" get the same
  fatal response.

Why loss + jitter + multipath triggers it: a missing frame creates a hole, and
every frame after the hole stays buffered because nothing can be delivered in
order. Two paths with different one-way delays (35 ms vs 50 ms +/- 25 ms) keep a
hole open continuously, so the backlog grows to thousands of nodes. This is
mqvpn's core use case, not an edge case.

**Until this is fixed, every lossy/multipath scenario in the matrix randomly
loses its connection mid-measurement, and the numbers are noise.** Fixing it is
prerequisite work, not a side quest. Directions, cheapest first:

1. Derive the cap from the window rather than hardcoding it
   (`window / min_expected_frame_payload`), so the two limits cannot disagree.
2. Handle `-XQC_ELIMIT` distinctly from a protocol violation: reset the one
   stream (`RESET_STREAM`/`STOP_SENDING`) instead of the whole connection, or
   at minimum close with a resource error rather than `FRAME_ENCODING_ERROR`.
3. Correct-but-invasive: do not ACK the packet whose frame could not be
   buffered, so the peer retransmits when there is room. This is real
   backpressure; the other two are damage control.

The cap must stay bounded — the point of it is that a hostile peer cannot pin
unbounded memory with sparse frames. The fix is to make the bound consistent
and non-fatal, not to remove it.

### 1.2 `ci_bench_run_iperf` can hang forever — fixed

`ci_bench_run_iperf` (`scripts/ci_benchmarks/ci_bench_env.sh`) lacked both
guards that its sibling in `tests/test_e2e_hybrid_h2.sh` documents as mandatory:
no `timeout` wrapper on the iperf3 client, and `wait "$iperf_srv_pid"` on a
one-shot `iperf3 -s -1` **without killing it first**, which never returns if no
client ever connected. Both are now in place, along with a follow-up fix for the
port collision between back-to-back samples.

This is called out because the symptom outlived the cause: `netsim (classes)`
still burns its full 60 minutes, and it is now tempting to re-diagnose it here.
It is not this. The remaining hang is the spike-driver deadlock in §0.1 B, which
blocks before iperf3 is ever invoked.

### 1.3 Budget structure

`classes` at 60 minutes is the §0.1 B deadlock, not real work: ~95 s per class
x 13 is about 20 minutes. But the additions below multiply the scenario count,
so the weekly must shard by scenario group rather than growing one job.

Note that the current timings are *failure* timings and will grow once §0.1 A is
fixed. A scenario that cannot establish its tunnel spends 25 s per `measure_pathset`
call timing out in `ci_bench_wait_tunnel` and never runs iperf3 at all; a working
`run_pair` runs three real measurements instead. Re-estimate the budget against a
run that actually carries traffic before sharding to the table in §4.

---

## 2. Missing coverage, in priority order

### 2.1 Geographic multi-hop and asymmetric routing — highest value

This was the explicit ask and the biggest gap. What exists is
`client -> hop -> server`, one intermediate namespace, with both directions of
each leg shaped identically. What a real cross-border path looks like is a
chain of five to eight hops, each with its own queue, and a return path that
often does not retrace the forward one.

**Topology.** Generalise the chain to N hops:

```
NS_CLIENT - access - hop1 - national - hop2 - transoceanic - hop3 - peering - NS_SERVER
```

Each hop is a namespace with `ip_forward=1` and its own netem, so delay and
queueing accumulate the way they do on a real AS path, TTL decrements, and
`traceroute` shows the hop count. The existing 2-hop code is this with N=1;
the change is to loop over a hop list instead of creating one.

**Route table** (one-way delays; RTT is the sum of both directions, which is
the point):

| Route | Forward | Return | RTT | Models |
|---|---|---|---|---|
| `cn_jp_direct` | CN-JP 30 | JP-CN 30 | 60 ms | symmetric regional |
| `cn_eu_direct` | CN-RU-EU 45+55 | same reversed | 200 ms | symmetric long-haul |
| `cn_eu_via_us` | CN-JP-USW-USE-EU 30+90+35+80 | EU-CN direct 180 | **415 ms** | **asymmetric detour** |
| `cn_us_via_eu` | CN-RU-EU-US 45+55+80 | US-JP-CN 100+30 | **490 ms** | **asymmetric detour** |
| `cn_us_direct` | CN-JP-US 30+90 | US-JP-CN 90+30 | 240 ms | symmetric transpacific |
| `cn_us_congested_return` | CN-US 120 | US-CN 120, +2% loss, 40 Mbit | 480 ms | one-way congestion |

The last one matters on its own: a path that is clean outbound and congested
inbound is extremely common on China routes, and it is invisible to any
symmetric model.

**Implementation of asymmetry.** netem shapes egress only, which is exactly
what makes this expressible. Forward = egress of each hop's server-facing
device; return = egress of each hop's client-facing device. Two changes:

1. Allow a *different* netem spec per direction on the same hop (today both
   directions get the same string).
2. For a true detour, route the return through a *different* hop chain: the
   server's route to the client subnet points at return-chain hop 1, while the
   client's route to the server points at forward-chain hop 1. Two chains per
   path, joined at the endpoints.

Verification must assert the asymmetry actually exists, not just that the
config was applied: `ping` reports RTT (the sum), so the practical check is that
`traceroute` from each end reports different hop counts, plus measured RTT
matching `forward + return` rather than `2 x forward`.

### 2.2 Server tier and host contention — entire second half of the ask

Nothing exists. cgroup v2 with `cpu`/`cpuset`/`memory` and `systemd-run` are
confirmed present.

**Tiers.** Wrap the server process:
`systemd-run --scope -p AllowedCPUs=... -p CPUQuota=... -p MemoryMax=...`

| Tier | Nominal GB5 (single) | Shape | Emulation |
|---|---|---|---|
| `vps_1c1g` | 400-600 | 1 core, 1 GB | `AllowedCPUs=0 CPUQuota=40% MemoryMax=1G` |
| `vps_1c1g_std` | 1000-1100 | 1 core, 1 GB | `AllowedCPUs=0 CPUQuota=100% MemoryMax=1G` |
| `vps_2c2g` | 1400-1500 | 2 cores, 2 GB | `AllowedCPUs=0-1 CPUQuota=140% MemoryMax=2G` |
| `vps_2c2g_fast` | 1500-2000 | 2 cores, 2 GB | `AllowedCPUs=0-1 CPUQuota=200% MemoryMax=2G` |

**These labels are nominal and must be printed as such.** A GB5 score is
IPC x frequency; `CPUQuota` throttles time slices. This reproduces the
*throughput ceiling* of a smaller box, not its per-operation latency, and it
cannot make a fast core behave like a slow one. Disk size (20 GB / 40 GB) has
no effect on these tests and is not modelled.

**Host (母鸡) state:**

| State | Emulation |
|---|---|
| `healthy` | nothing |
| `noisy_neighbour` | `stress-ng --cpu N` pinned to the CPUs *outside* the server's `AllowedCPUs` |
| `softirq_storm` | small-packet flood on an unrelated veth pair, to contend for `ksoftirqd` |
| `cpu_capped` | tighten `CPUQuota` mid-run, emulating a provider throttle |

Hypervisor steal time cannot be emulated in a container; competing load is the
closest available proxy and should be labelled that way.

### 2.3 NAT matrix

Documented, not implemented. All five at the access hop, with nftables/iptables:

| Type | Rule |
|---|---|
| `public` | none |
| `full_cone` | `masquerade persistent` (mapping stable across destinations) |
| `port_restricted` | plain `masquerade` (conntrack default) |
| `symmetric` | `masquerade random` (fresh port per destination) |
| `cgnat` | masquerade at the access hop **and** at a transit hop (double NAT) |

`nf_conntrack_udp_timeout` already defaults to 30 s, so the "silent killer" is
the default; the aging test only has to idle past it. Worth adding on top:
aging *while multipath*, where one path idles and the other stays busy — the
busy path keeps the connection alive, so the idle path's NAT binding dies
silently and only path recovery reveals it.

### 2.4 MTU

Today: one leg at 1400. Needed: 1500 / 1400 / 1280 as an explicit axis, plus a
**PMTUD black hole** case (an intermediate hop that drops oversized packets
*and* suppresses ICMP Frag Needed) — the failure mode that hangs a tunnel
rather than slowing it. Cross MTU with `carrier_qos`, because under a PPS cap
goodput scales with bytes-per-packet: that pairing is what turns MTU choice and
GSO batching into a measurable number instead of a guess.

### 2.5 The remaining special condition

- **Live roaming under load.** **Done** — `run_special`'s `roam_under_load`
  rotates the hop's SNAT source mid-transfer and flushes conntrack, because
  without the flush the live UDP flow keeps its old translation, the server
  never sees a new peer address, and the test passes having exercised nothing.
  The row carries `roam_applied` so a run on a box without conntrack is not
  mistaken for a clean result.

- **Dual-stack routing split.** **Blocked on the client, not on the harness.**
  This was filed as "needs v6 addressing threaded through the hop chain", which
  understated it. The condition to test is one session with a v4 path and a v6
  path, asserting no stall when the preferred family degrades. But the client
  takes a single `--server HOST:PORT` — the option is not repeatable, while
  `--path IFACE` is — so every path in a session dials the same endpoint and
  therefore the same address family. No amount of netns plumbing expresses a
  per-path family split.

  Adding v6 addresses to the hop chain on its own would be dead code, so
  nothing was added. Unblocking it needs a product change first: either a
  repeatable `--server`, or per-path endpoint selection. Until then the honest
  scope is v6 *instead of* v4 (a v6-only topology, which would exercise the v6
  data path but is a different test), not v4 *and* v6 in one session.

### 2.6 Scheduler sweep

`classes` runs one scheduler. The interesting comparison is WLB vs MinRTT vs
backup-FEC across `hetero_extreme`, `asym_latency` and `one_flapping` — the
three classes where they should diverge. Current data already hints at it: WLB
beats MinRTT at every stream count on the plain asymmetric profile, and MinRTT
goes *negative* at 1 stream (multipath slower than single path), which deserves
its own regression guard.

---

## 3. Dashboard: say what each test and metric is

The picker shows `aggregate (7)`, `netsim_classes (1)`, `raw_throughput (9)`
with no indication of what any of them measures, so a reader cannot tell a
throughput benchmark from a smoke test. Three additions, all in the page:

1. **A bilingual description per test id**, shown under the picker: what it
   measures, what topology, and whether it is a gate or a trend. Keyed off the
   `test` field already present in every document.
2. **A metric glossary** — one line per metric explaining what it is and which
   direction is good. `aggregation_efficiency` and `vs_best_single` are not
   self-evident, and `rtt_inflation` needs "this is bufferbloat" spelled out.
3. **Scenario context on each card.** Documents already carry `netem`,
   `mode`, `caps`, `repeats`; surface the emulated profile next to the number
   so a value is never read without knowing which network produced it.

None of this needs Worker changes — the API returns whole documents and the
naming layer already lives in the page.

---

## 4. Revised tiering and budget

Sharded by scenario group so no single job approaches its timeout.

### Per-commit — `perf.yml`, about +4 min

One bad network, ratio gates only, nothing that can flake:
`hetero_extreme` multipath, gated on `vs_best_single` and `degraded_ratio`.
Absolute Mbps recorded, never gated.

### Weekly — `perf-weekly.yml`, sharded

| Job | Content | Est. |
|---|---|---|
| `classes` | 7 heterogeneity classes | ~15 min |
| `schedulers` | 3 classes x 3 schedulers | ~25 min |
| `geo` | 6 geographic routes, incl. 3 asymmetric | ~25 min |
| `catalog-a/b/c` | 6 transits x 10 access legs, split 3 ways | 3 x ~20 min |
| `tiers` | 4 server tiers x 4 host states | ~30 min |
| `nat-mtu` | 5 NAT types x 3 MTU + PMTUD black hole | ~25 min |
| `special` | all 5 special conditions, under load | ~25 min |
| `endurance` | 30 min diurnal + 60 min soak on `bgp_flappy` | ~95 min |
| `capacity` | 1 Gbit+ probe, labelled runner-bound | ~10 min |

About 11 parallel jobs, longest ~95 min, roughly 5 job-hours/week. Inside the
6 h job cap and the 20-concurrent-job limit; the repo is public so
standard-runner minutes are free.

### Measured, from run 33024352078

The first weekly that finished, for calibration against the estimates above:

| Job | Wall | Job | Wall |
|---|---|---|---|
| `perf-weekly` (core) | 57m00s | `cat_carrier` | 9m24s |
| `classes` | 19m10s | `cat_plain` | 7m37s |
| `combo` | 14m54s | `cat_iplc` | 6m50s |
| `sched` | 12m36s | `cat_opt` | 6m45s |
| `mtu` | 12m21s | `tiers` | 3m52s |
| `cat_junk` | 9m56s | `special` | 2m52s |

Total wall-clock 57m23s, set by the core job, with the whole scenario matrix
running inside it. Every job is comfortably under the 60-minute cap, so the
`netsim` timeout does not need raising yet — but these are still not final
numbers. Roughly a fifth of the rows never established a tunnel (§0.2 B) and
spent 25 s per attempt failing instead of 18 s measuring, and fixing §0.2 A
makes each `run_pair` do three real measurements where today it does one and
two cheap repeats of it. Re-measure before sharding further.

## 5. What this cannot model — state it with the results

- **GB5 scores.** `CPUQuota` caps throughput, not IPC. Tier labels are nominal.
- **2.5 Gbit server / 1 Gbit client.** A shared 4-vCPU runner cannot saturate
  those, so at those rates the runner is the bottleneck and the number measures
  the runner. Regular tiers stay at or below 400 Mbit aggregate so the emulated
  link is the constraint; anything above is a labelled capacity probe.
- **Radio layer.** HARQ, scheduler grants and handovers are approximated by
  jitter tails and burst loss, not simulated.
- **BGP semantics.** A re-route is an abrupt delay/loss change; AS paths, flap
  damping and convergence are out of scope. The geographic routes model the
  *result* of a detour, not the protocol that chose it.
- **Hypervisor steal.** Competing load is a proxy.
- **Cross-tenant noise.** Seeding netem removes *our* randomness, not the
  host's — which is why gates are on ratios and CV is published per metric.

## 6. Build order

0. ~~**Make the harness measure anything at all** — the service-address source
   bug (§0.1 A) and the spike-driver deadlock (§0.1 B).~~ **Done and confirmed**
   by run 33024352078: no job timed out and 76 of 97 rows carry a throughput.

0a. **Make the multipath rows mean something** — the leaked-client bug in
   `run_pair` (§0.2 A). This is now the item that blocks value: `classes`,
   `combo`, `mtu`, `sched` and the per-commit gate all currently compare a path
   against itself, so every aggregation figure in the matrix is an artefact.
   Nothing below is worth tuning until it is fixed.

0b. **Stop a failure from looking like a measurement** — a reason field instead
   of a bare `0.0` (§0.2 B), and a row from `special`'s failure path (§0.2 E).
   Cheap, and it is what turned §0.1 into a seven-hour investigation.

0c. **Recalibrate what the numbers rest on** — the `carrier_qos` policer
   (§0.2 C) and the 6-second single-stream sample that cannot fill a high-BDP
   path (§0.2 D). Both make a profile report something other than what its
   table entry claims.

1. ~~Fix the `XQC_ELIMIT` connection kill.~~ **Done** — the buffered-frame cap
   was raised to match the receive window and `-XQC_ELIMIT` was made a tolerant
   error, so a full buffer drops a packet and retransmits instead of killing the
   connection.
2. ~~Fix the `ci_bench_run_iperf` hang.~~ **Done** — client `timeout` plus
   killing the server before waiting on it. A follow-up fixed the related port
   collision: consecutive samples share one port, and the listener check could
   match the *previous* sample's server still shutting down.
3. **N-hop chain**, then the geographic route table. The largest remaining
   *coverage* item, and the one that needs `netsim_setup`'s veth chain and
   routing rewritten rather than extended. Per-direction netem is already in
   place. Do this after item 0: the rewrite touches exactly the routing that
   §0.1 A gets wrong, and doing both at once makes it impossible to tell which
   change fixed or broke the handshake.
4. ~~Server tiers + host contention.~~ **Done** — `ci_bench_host.sh`, `tiers`
   mode.
5. ~~NAT matrix and MTU axis (including the PMTUD black hole).~~ **Done** —
   `NETSIM_NAT`, `mtu` mode, `netsim_set_pmtud_blackhole`.
6. ~~Dashboard descriptions and glossary.~~ **Done** — per-test descriptions,
   a direction-aware metric glossary, and the emulated profile shown on each
   card via the series API's new `context` map.
7. ~~Scheduler sweep~~ (**done**, `sched` mode) and ~~roaming under load~~
   (**done**). The dual-stack split is blocked on the client; see §2.5.
   Endurance jobs remain unscheduled.

Still open, in short: item 0 (both defects), the N-hop chain and geographic
routes (§2.1), the 2.5 G/1 G line rates (not feasible on a shared runner — §5),
the dual-stack routing split (blocked on the client — §2.5), the fifth special
condition, per-scenario incremental result writing (§0.1 C), and the endurance
jobs. Everything else in §0 is implemented; none of it is yet verified against a
run that carried traffic.
