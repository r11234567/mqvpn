# Realistic network emulation matrix — status and completion plan

Two tiers only, matching the existing workflows: **per-commit** (`perf.yml`, push
to `main`) is a fast smoke test on one bad network; **weekly**
(`perf-weekly.yml`) carries the matrix. There is no nightly tier.

---

## 0. Honest status

Roughly **40%** of the requested scope is implemented. The path-shape half
exists; the endpoint half does not, and the geographic routing that was called
out explicitly is absent.

| Requested | Status | Where |
|---|---|---|
| 6 backbone classes (IPLC, optimized/plain/junk/flapping BGP, carrier QoS) | **done** — 7 profiles | `NETSIM_TRANSIT` |
| Client access: eth / WiFi / 5G / satellite / tethering | **approximated** — 10 legs stand in for ~32 combinations | `NETSIM_ACCESS` |
| Heterogeneity-class aggregation | **done** — 7 classes | `NETSIM_CLASS` |
| **Many hops to the server** | **NOT done** — exactly one intermediate namespace (2 hops) | — |
| **Geographic detours (Asia→EU via US, Asia→US via EU)** | **NOT done** | — |
| **Asymmetric routing (forward ≠ return)** | **NOT done** — every leg is shaped identically both ways | — |
| MTU large/small | **barely** — one leg at 1400; no 1280, no sweep | `NETSIM_LEG_MTU` |
| Client NAT: public + 4 types | **NOT done** — only one MASQUERADE inside the NAT-aging test | — |
| **Server tier (1c1g / 2c2g, GB5 400–2000)** | **NOT done** — designed, zero code | — |
| **Host (母鸡) state: healthy / loaded / softirq storm** | **NOT done** | — |
| Server 2.5G / client 1G line rates | **NOT done, and partly infeasible** — see §5 | — |
| 5 special conditions | **3 of 5** — NAT aging, corrupt/reorder, ACK starvation | `run_special` |
| Scheduler comparison across classes | **NOT done** — one scheduler per run | — |

Nothing here is wasted: the leg-composition engine is what makes the rest cheap
to add. But the matrix as it stands answers "how does a bad *link* behave",
not "how does a real deployment behave".

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

### 1.2 `ci_bench_run_iperf` can hang forever

The weekly `netsim (classes)` job was cancelled at its 60-minute timeout while
its six siblings finished in 3-6 minutes. `ci_bench_run_iperf`
(`scripts/ci_benchmarks/ci_bench_env.sh`) lacks both guards its sibling in
`tests/test_e2e_hybrid_h2.sh` documents as mandatory:

- no `timeout` wrapper on the iperf3 client;
- `wait "$iperf_srv_pid"` on a one-shot `iperf3 -s -1` **without killing it
  first** — if no client ever connects, that server never exits and the wait
  blocks forever.

The existing benchmarks never hit this because they only ever shape gentle
paths (300 Mbit/10 ms). netsim is the first thing to drive paths bad enough
that a tunnel fails to carry iperf3 — so this is a latent bug the new coverage
exposed, not a new one.

Fix: mirror the hybrid helper (client under `timeout $((duration + 15))`, kill
the server before waiting). Every long matrix depends on this.

### 1.3 Budget structure

`classes` at 60 minutes was the hang, not real work: ~100 s per class x 7 is
about 14 minutes. But the additions below multiply the scenario count, so the
weekly must shard by scenario group rather than growing one job.

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

### 2.5 The two remaining special conditions

- **Dual-stack routing split.** v4 and v6 paths with divergent delay/loss;
  assert no stall when the preferred family degrades mid-transfer. Needs v6
  addressing threaded through the hop chain — the reason it was deferred.
- **Live roaming under load.** The existing rebind e2e, but with traffic
  saturating the link across the address change.

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

1. **Fix the `XQC_ELIMIT` connection kill.** Everything lossy is unmeasurable
   until this is done.
2. **Fix the `ci_bench_run_iperf` hang.** Every long matrix depends on it.
3. **N-hop chain + per-direction netem**, then the geographic route table.
4. **Server tiers + host contention** — the whole missing half, and cheap once
   the wrapper exists.
5. NAT matrix and MTU axis (including the PMTUD black hole).
6. Dashboard descriptions and glossary.
7. Scheduler sweep, remaining two special conditions, endurance jobs.
