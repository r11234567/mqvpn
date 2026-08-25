# Realistic network emulation matrix — design

Status: **proposal**. Nothing here is implemented yet.

Today's benchmarks emulate one shape: two clean, symmetric, low-jitter links
with uniform random loss (`bench_env_setup.sh`'s `BENCH_ENV_NETEM`). That is a
reasonable model of a private backbone and a poor model of everything else, so
the aggregation and failover numbers we publish describe the easiest case we
ship into. This document plans the way out.

Two tiers only, matching the existing workflows: **per-commit** (`perf.yml`,
push to `main`) stays a fast smoke test on one bad network; **weekly**
(`perf-weekly.yml`) carries the entire matrix. There is no nightly tier.

---

## 1. Why the cross product is not the plan

Enumerating the factors as stated:

| Factor | Levels |
|---|---|
| Backbone type | 6 (IPLC/IEPL, optimized BGP, plain BGP, junk BGP, flapping BGP, throttled/carrier) |
| Client access | ~32 (wired, 2×WiFi, 3 signal × 3 congestion × 2 priority 5G, 2×2×2 satellite, 3 tethering) |
| MTU | 3 |
| Client NAT | 5 (public + 4 NAT behaviours) |
| Server CPU tier | 4 |
| Server sizing | 2 |
| Host (母鸡) state | 3 |

`6 × 32 × 3 × 5 × 4 × 2 × 3` = **69,120** single-path configurations. At 75 s per
measurement that is ~60 days of runner time for one pass. Multipath squares the
path dimension: `(6 × 32)²` = **36,864** path pairs before any endpoint factor.

So the plan is explicitly **not** to enumerate. Three reductions, in descending
order of leverage.

### 1.1 Legs compose — build them, don't enumerate them

A "5G-half-signal client behind a junk BGP transit" is not a profile to be
authored. It is an access leg *followed by* a transit leg; delay, loss and rate
compose along the wire. Model a path as a **chain of namespaces**, one qdisc per
hop:

```
NS_CLIENT ── access hop ── transit hop 1 ── transit hop 2 ── NS_SERVER
             (CPE/radio)   (peering)        (backbone)
```

`6 × 32 = 192` path types collapse to `6 + 10 = 16` reusable blocks, and the
chain buys three things one veth cannot:

- **Real hop count.** A separate queue per hop, TTL decrements, a traceroute
  that looks like the internet. Bufferbloat lives *in a hop's queue*; with one
  veth there is only one queue to put it in.
- **A place to put the NAT.** CPE NAT at the access hop, CGNAT at a transit hop
  — double NAT becomes topology, not a special case.
- **Independent failure injection.** Flap transit without touching access state.

### 1.2 Multipath cares about heterogeneity class, not about pairs

Schedulers do not differ between "two 300 Mbit links" and "two 310 Mbit links".
They differ across these classes — this is the whole interesting space:

| Class | Composition | Question it answers |
|---|---|---|
| `homo_good` | 2× optimized | Does aggregation add at all? |
| `homo_bad` | 2× junk | Does it degrade gracefully or collapse? |
| `hetero_extreme` | optimized + junk | **Does the bad path drag down the good one?** |
| `asym_capacity` | IPLC 150M + plain 300M | Is the split proportional to capacity? |
| `asym_latency` | 40 ms + 320 ms GEO | Does it avoid head-of-line blocking? |
| `one_flapping` | stable + flapping | Does it evacuate, and re-admit after? |
| `carrier_pair` | 2× carrier_qos | Does PPS capping break both paths the same way? |

Seven classes, not 36,864 pairs. `hetero_extreme` and `one_flapping` are where
WLB / MinRTT / backup-FEC actually separate — they get the deepest runs.

### 1.3 Endpoint factors are orthogonal — sample them pairwise

NAT type, MTU, CPU tier and host state do not interact with *each other* in
interesting ways; each interacts with the path. Use a **2-way covering array**
(every pair of levels appears at least once) over
`{backbone × access × MTU × NAT}`: ~35 runs instead of ~2,900, and any
two-factor interaction bug still gets caught.

Full-factorial only where interaction is known to exist:
- MTU × path (PMTUD, fragmentation, black-hole detection)
- loss burstiness × FEC scheduler (the entire point of backup-FEC)
- PPS cap × packet size (see `carrier_qos` — this is where GSO pays)

---

## 2. Transit profiles (the six backbones)

Verified against `iproute2-6.15` / kernel 6.12. `limit` is in packets, sized
against the bandwidth-delay product, because queue depth *is* the difference
between a good line and a bloated one.

| Name | netem | Notes |
|---|---|---|
| `iplc` | `delay 65ms 1ms distribution normal rate 150mbit limit 1000` | Stable, low jitter, no loss, **small pipe**. Aggregation has the most to offer here. `limit` ≈ 1×BDP. |
| `bgp_opt` | `delay 40ms 2ms distribution normal loss 0.01% rate 400mbit limit 2200` | The upper bound. See §7 on why not 1 Gbit by default. |
| `bgp_plain` | off-peak: `delay 80ms 8ms distribution normal loss 0.1% rate 300mbit limit 2500`<br>peak: `delay 110ms 25ms distribution normal loss 0.8% rate 180mbit limit 4000` | Time-varying; the diurnal driver swaps between the two. |
| `bgp_junk` | `delay 180ms 45ms distribution paretonormal loss gemodel 3% 20% 88% 0.5% rate 80mbit limit 6000` | Cogent-class. **Gilbert-Elliott, not uniform loss** — real bad lines lose in bursts, and burst loss is what breaks congestion control. `limit` ≈ 5×BDP = bloated. |
| `bgp_flappy` | base = `bgp_plain` off-peak; every 45–90 s inject 5–15 s of<br>`delay 600ms 250ms distribution paretonormal loss 6% rate 6mbit limit 8000` | Applied with `tc qdisc change` (verified: mutates in place, does not reset the path). The most demanding scheduler test. |
| `carrier_qos` | `delay 240ms 90ms distribution paretonormal loss gemodel 2% 15% 80% 0.3% rate 900mbit limit 10000`<br>**plus** `tc action police pkts_rate 8000 pkts_burst 200 drop` on UDP | The China-carrier shape: pipe looks huge, **packet rate is capped**, so bitrate collapses at small MTU. Verified available. |

`carrier_qos` is the only profile whose optimization signal is *packet size*
rather than congestion control: under a PPS cap, throughput scales with
bytes-per-packet, so it measures directly whether GSO/batching and MTU choice
pay off. No current profile can show that.

## 3. Access legs (client side)

Ten legs cover the ~32 requested combinations, because signal × congestion ×
priority collapse onto the same three observables (delay, jitter tail, loss
burstiness):

| Name | netem | Models |
|---|---|---|
| `eth` | `delay 0.2ms rate 1000mbit` | Wired |
| `wifi_good` | `delay 3ms 2ms distribution normal loss 0.05% rate 400mbit` | Clean 5 GHz |
| `wifi_busy` | `delay 18ms 30ms distribution paretonormal loss 1.2% rate 60mbit limit 3000` | Contended channel |
| `5g_full` | `delay 18ms 6ms distribution normal loss 0.05% rate 350mbit` | Full bars, uncongested, prioritized |
| `5g_half` | `delay 45ms 25ms distribution paretonormal loss 0.4% rate 90mbit` | Half bars |
| `5g_edge` | `delay 95ms 60ms distribution paretonormal loss gemodel 4% 25% 80% 1% rate 12mbit limit 4000` | Almost no signal |
| `5g_throttled` | `delay 130ms 85ms distribution paretonormal loss gemodel 5% 20% 85% 1.5% rate 8mbit limit 5000` + PPS cap | Congested cell, no priority |
| `starlink` | `delay 45ms 30ms distribution paretonormal loss 1% rate 200mbit` + 300 ms spike every 15 s | LEO reconfiguration |
| `geo_sat` | `delay 320ms 25ms distribution normal loss 0.5% rate 20mbit limit 2000` | GEO |
| `tether_otg` | `5g_half` + `duplicate 0.3% corrupt 0.05%`, MTU 1400 | Phone tethering; duplicate/corrupt is the "driver jank" |

Radio-layer behaviour (HARQ, scheduler grants, handover) is *approximated* by
the jitter tail and burst-loss model, not simulated — see §9.

## 4. Endpoint factors

**MTU** — 1500 / 1400 (tether, PPPoE) / 1280 (v6 minimum), set per access hop.

**NAT** — at the access hop: `public` (none) · `full_cone` (persistent mapping)
· `port_restricted` (conntrack default) · `symmetric` (`--random`) · `cgnat`
(masquerade at access *and* transit hop).

`nf_conntrack_udp_timeout` already defaults to **30 s** — the "silent killer" is
the default, not something to force. It only needs a test that idles past it.

**Server tier** — `systemd-run --scope -p AllowedCPUs= -p CPUQuota= -p MemoryMax=`
(cgroup v2 `cpu`/`cpuset`/`memory`/`io` confirmed present):

| Tier | Target GB5 (single) | Emulation |
|---|---|---|
| `weak` | 400–600 | `AllowedCPUs=0 CPUQuota=40% MemoryMax=1G` |
| `std` | 1000–1100 | `AllowedCPUs=0 CPUQuota=100% MemoryMax=1G` |
| `good` | 1400–1500 | `AllowedCPUs=0-1 CPUQuota=140% MemoryMax=2G` |
| `high` | 1500–2000 | `AllowedCPUs=0-1 CPUQuota=200% MemoryMax=2G` |

**Label these as approximations.** A GB5 score is IPC × frequency; `CPUQuota`
throttles time slices. It reproduces the *throughput ceiling* of a slower box,
not its per-operation latency.

**Host (母鸡) state** — `healthy` · `noisy` (`stress-ng --cpu` on sibling CPUs)
· `softirq_storm` (packet flood on an unrelated veth pair). Hypervisor steal
cannot be emulated in a container; competing load is the closest proxy.

## 5. The five special conditions

All five already have **functional** coverage under `scripts/ci_e2e/`
(`run_ipv6_dataplane`, `run_ipv6_transport`, `run_addr_del_failover`,
`run_reconnect`, `run_nat_test`). None runs under saturating traffic — that is
the real gap, because these are all *timing and buffering* bugs.

| Condition | New variant | Tier |
|---|---|---|
| Dual-stack routing split | v4/v6 paths with divergent delay/loss; assert no stall when the preferred family degrades **mid-transfer** | weekly |
| NAT 30 s aging | Idle 35 s mid-session behind NAT, resume; assert keepalive/rebind recovers with no user-visible break | weekly |
| ACK starvation / bufferbloat | Asymmetric 100/5 Mbit, deep uplink queue, saturate uplink, measure downlink collapse — does pacing protect ACKs? | weekly |
| Corrupt / reorder / duplicate | `corrupt 0.1% reorder 25% 50% duplicate 0.5%` under load; integrity + no crash | weekly |
| Live roaming under load | Existing rebind test with iperf3 saturating across the address change | weekly |

---

## 6. Metrics

The point of the matrix is the metrics, so these are collected for **every**
scenario, not just the throughput ones.

**Throughput / aggregation**
`solo_a_mbps` · `solo_b_mbps` · `multipath_mbps` · `aggregation_efficiency`
(= mp / (a+b)) · `vs_best_single` (= mp / max(a,b), the headline "is multipath
worth it") · `theoretical_max_mbps` · `utilization`

**Latency**
`srtt_p50_ms` · `srtt_p95_ms` · `srtt_p99_ms` · `min_rtt_ms` ·
`rtt_inflation` (= p95/min, a direct bufferbloat readout)

**Loss / integrity**
`dgram_lost` · `dgram_lost_pct` · `retrans_pct` · `fec_send_cnt` ·
`fec_recover_cnt` · `fec_efficiency` · `reorder_events` · `dup_recv`

**Path fairness**
`path_bytes` per path · `minshare` (= min/max) · `path_switch_count`

**Failover**
`ttf_sec` · `ttr_sec` · `degraded_ratio` · `recovery_ratio` · `outage_ms`

**Application-level** — what a user actually feels, and missing today
`ttfb_p50_ms` · `ttfb_p95_ms` · `fct_64k_ms` · `fct_1m_ms` · `stall_count` ·
`max_stall_ms`. Bulk iperf3 hides these: a scheduler can win on bulk goodput
while making short flows worse.

**Resource / batching**
`server_rss_peak_kb` · `client_rss_peak_kb` · `server_cpu_pct` ·
`gso_factor` (= udp_tx_datagrams / udp_tx_sends) ·
`gro_factor` (= udp_rx_datagrams / udp_rx_receives). The two factors are the
readout that makes `carrier_qos` actionable.

**Stability**
`samples` · `median` · `iqr` · `cv_pct` per repeated metric.

## 7. Result JSON layout — and why the dashboard needs no change

The Worker has **no metric-specific logic**: `flattenNumbers` walks any
document and charts every finite number it finds, so new metrics appear with no
code change. Two things in it are fixed rather than dynamic, and the JSON layout
is designed around them so the Worker stays untouched:

1. **`MAX_METRICS = 400` per document.** Today's worst file is `failover` at 251
   (its per-interval time series dominates). So: **one file per scenario class**,
   and put per-interval timelines in a separate `*_timeline_*.json`. Each file
   then becomes its own entry in the dashboard's benchmark picker, which also
   makes navigation better than one giant document would.

2. **`ARRAY_KEY_FIELDS` labels array elements** by the first of
   `name, scenario, condition, label, direction, scheduler, streams,
   target_mbps, packet_size, loss_percent, time_sec` it finds. So key arrays
   with **`scenario`** (e.g. `{"scenario": "iplc+5g_half", …}`) rather than
   inventing `backbone`/`class`/`tier` as the key field — those would fall back
   to a bare index and produce labels like `results[3].vs_best_single`.

Everything else already works unchanged: `update_perf_index.py` globs `*.json`
and registers whatever filenames it finds; `publish_to_r2.sh` uploads them;
`testIdFromFile` strips the `_YYYYMMDD_HHMMSS` suffix, so
`weekly_hetero_extreme_20260823_120000.json` becomes the series
`weekly_hetero_extreme`.

Top-level shape stays as it is today (`test`, `commit`, `timestamp`, `netem`,
`results`), so nothing downstream needs teaching.

## 8. Making the numbers mean something on a shared runner

`perf.yml` now runs on `ubuntu-latest`, so absolute throughput is partly a
measurement of whoever else is on the host. Five rules:

1. **Shape below the runner's ceiling.** The emulated link must be the
   bottleneck, not the runner CPU. The current ~380 Mbit aggregate profile
   completes and produces stable numbers, so cap regular tiers at **≤400 Mbit
   aggregate**. A 1 Gbit `bgp_opt` variant is a *capacity probe*, weekly only,
   labelled runner-bound — a link that never saturates measures nothing.

2. **Ratio metrics, not absolute.** `aggregation_efficiency`, `vs_best_single`
   and `degraded_ratio` divide the host's speed out. Gate on those; publish
   absolute Mbps as context.

3. **Pair the measurements.** solo-A, solo-B and multipath run back-to-back in
   the same job, seconds apart. Never compare across jobs.

4. **Median of ≥3, with IQR and CV.** The dashboard already surfaces CV per
   metric. Gate only when CV is under threshold; otherwise emit *inconclusive*
   rather than failing — a noisy red is worse than an honest gap.

5. **Seed netem.** `iproute2-6.15` accepts `netem … seed <N>` (verified:
   re-applying the same seed reproduces it), removing netem's own PRNG as a
   variance source — the dominant one for loss-sensitive metrics.
   **Caveat:** `ubuntu-24.04` ships iproute2 6.1, which predates the knob.
   Detect at runtime; fall back to longer runs (more samples), not more repeats.

## 9. Tiering and budget

Measured on the current `perf.yml` job (warm cache): fixed overhead ≈ 90 s;
raw-throughput 67 s, failover 219 s, aggregation 301 s. A *scenario unit* is
≈ 60 s single-path, ≈ 100 s multipath (solo A + solo B + MP).

The repo is public, so standard-runner minutes are free and unlimited. The real
constraints are the 6 h job cap, 20 concurrent jobs (Free plan), queue
contention, and how long a push should wait for feedback.

### Per-commit — `perf.yml`, push to `main`

One bad network, nothing that can flake:

- `hetero_extreme` (optimized + junk) multipath unit — the single
  highest-signal scenario, since it is where a scheduler regression shows first
- Gate on `vs_best_single` and `degraded_ratio` only; absolute Mbps recorded,
  never gated
- Keep the three existing benchmarks

**Budget: +3–4 min** on a job that runs ~12 min today.

### Weekly — `perf-weekly.yml`

Everything else, as a matrix:

| Job | Content | Est. |
|---|---|---|
| `catalog-a` … `catalog-c` | 6 backbones × 10 access legs, single path (60 units), split 3 ways | 3 × ~22 min |
| `classes` | 7 heterogeneity classes × 3 schedulers (21 MP units) | ~40 min |
| `pairwise` | 2-way covering array over `{backbone × access × MTU × NAT}` (~35 units) | ~35 min |
| `special` | the 5 special conditions, under load | ~20 min |
| `tiers` | 4 CPU tiers × 3 host states at fixed network (12 units) | ~20 min |
| `endurance` | 30 min diurnal (`bgp_plain` peak/off-peak) + 60 min soak on `bgp_flappy` | ~95 min |
| `capacity` | 1 Gbit probe, labelled runner-bound | ~10 min |

**Total ≈ 4 job-hours/week across 8 parallel jobs**, longest job ~95 min —
inside every limit, and the weekly wall-clock stays under two hours.

## 10. What this does not model

- **GB5 scores.** `CPUQuota` emulates a throughput ceiling, not IPC. Tier labels
  are nominal.
- **1 Gbit line rate.** On a 4-vCPU shared runner this measures the runner.
- **Radio layer.** HARQ, scheduler grants, handovers → approximated by jitter
  tails and burst loss.
- **BGP semantics.** A re-route is an abrupt delay/loss change; AS paths, flap
  damping and convergence are out of scope.
- **Hypervisor steal.** Competing load is a proxy.
- **Cross-tenant noise.** Seeding netem removes *our* randomness, not the host's.

## 11. Build order

1. **Leg composition + chained-namespace topology** in `bench_env_setup.sh`.
   Nothing else is measurable until a path can be built from an access leg and a
   transit leg.
2. **Profile catalog as data** (transit + access tables), seeded, with runtime
   capability detection for `netem seed` and `police pkts_rate`.
3. **Metric collection + ratio/CV harness**, emitting the §7 JSON layout.
4. **Per-commit scenario** — smallest change that improves today's signal.
5. **Weekly matrix** — classes, catalog, pairwise, special-under-load.
6. **Endpoint factors** (NAT / MTU / server tier) and the endurance jobs.
