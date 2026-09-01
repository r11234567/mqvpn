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
| Client NAT: public + 4 types | **built, and measured nothing until now** — no NAT'd path ever completed a handshake (§0.4 F); `cgnat` is one translation, not two | `NETSIM_NAT` |
| **Server tier (1c1g / 2c2g, GB5 400–2000)** | **done** — four tiers as cgroup caps, labelled nominal | `ci_bench_host.sh` |
| **Host (母鸡) state: healthy / loaded / softirq storm** | **done** — plus a mid-run CPU throttle | `ci_bench_host.sh` |
| Server 2.5G / client 1G line rates | **NOT done, and partly infeasible** — see §5 | — |
| 5 special conditions | **4 of 5** — NAT aging, corrupt/reorder, ACK starvation, roaming under load | `run_special` |
| Dual-stack routing split | **blocked on the client, not the harness** — see §2.5 | — |
| Scheduler comparison across classes | **built; two of its three classes were vacuous** — §0.4 F left path B dead in `hetero_extreme` and `asym_latency`, so all three schedulers measured the same single path. `one_flapping` did separate them (§1.3.5) | `sched` mode |

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
document from the `EXIT` trap. **Fixed in §0.3.**

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

**C. The `loss gemodel` profiles were calibrated far heavier than intended.**
Every `carrier_qos` row is 0.2-0.5 Mbps whatever the access leg, against a
ceiling of roughly 90 Mbps from its own 8000 pps cap at 1400 bytes — two orders
of magnitude under the profile's design intent.

The cause is the loss parameters, not the packet-rate cap. `loss gemodel p r
(1-h) (1-k)` takes P(good→bad), P(bad→good), loss in the bad state, and loss in
the good state, so the average loss is `p/(p+r)·(1-h) + r/(p+r)·(1-k)` — a
figure none of the four terms resembles on its own. Worked out for the four
profiles as they were configured:

| Profile | Configured | Average loss | Mean burst |
|---|---|---|---|
| `bgp_junk` | `3% 20% 88% 0.5%` | **11.9%** | 5 pkt |
| `carrier_qos` | `2% 15% 80% 0.3%` | **9.7%** | 6.7 pkt |
| `5g_edge` | `4% 25% 80% 1%` | **11.9%** | 4 pkt |
| `5g_throttled` | `5% 20% 85% 1.5%` | **18.2%** | 5 pkt |

A transit that loses an eighth of its packets is not a junk route, it is an
outage: it explains both the low readings *and* B's 21 handshake failures,
which cluster on exactly this set of profiles. It also defeats the purpose of
`carrier_qos`, whose whole reason to exist is that goodput should track
bytes-per-packet rather than congestion control — at 9.7% loss, congestion
control is the only thing being measured.

A second, smaller point about the policer, since it was mis-stated when this
section was first written: a tc `ingress` qdisc filters what *arrives* on a
device, so attaching the policer to the hop's server-facing veth polices the
server→client direction, i.e. the downlink. The benchmark measures a download,
so the cap did apply to the traffic under test — it was the call-site comment
claiming it throttled "the uplink" that was wrong, not the placement. Both
directions are policed now, which is what a subscriber-level carrier cap does
anyway and removes the ambiguity.

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

### 0.3 All five are fixed, plus the artifact loss from §0.1 C

Written, syntax-checked and committed; **none of it is confirmed in CI yet** —
read the next weekly before trusting any number below §1.

**A — the leaked client.** `measure_pathset` no longer echoes its result, so it
no longer has to be called through `< <( )`. It returns through
`MEASURED_MBPS` / `MEASURED_CV` / `MEASURED_STATUS`, and all six call sites are
plain calls in the caller's own shell; there is a comment on the function
saying why, because the subshell form reads perfectly natural and will be
reintroduced by anyone who does not know what it cost. Belt and braces:
`ci_bench_start_client` also appends its pid to a file whose name derives from
`$$` (unchanged inside a subshell), `ci_bench_start_client` now begins by
calling `ci_bench_stop_client`, and that function reaps every live pid in the
file. It refuses to do so silently — reaping more than one prints

```
WARNING: reaped N concurrent VPN clients -- an earlier measurement leaked
one, and its paths carried the traffic
```

so the next occurrence of this class of bug announces itself in the job log
instead of being absorbed into the numbers. A pid is only killed after
`/proc/<pid>/exe` (or `comm`) confirms it is still the mqvpn that was recorded,
so a recycled pid cannot make the harness kill something unrelated.

**B — zero versus never-connected.** Every row now carries a `status` field:
`ok`, `client_start_failed`, `tunnel_never_up`, or `measured_zero`. The
`run_pair` rows carry one per measurement (`a=... b=... mp=...`) since the three
can fail independently. The tunnel wait moved from a hard-coded 25 s to
`CI_BENCH_TUNNEL_WAIT_SEC`, default 40; the failing profiles were marginal
rather than impossible, so the old wait was converting "slow to connect" into a
published zero.

**C — profile recalibration.** The four `loss gemodel` profiles are re-set to
2.0% / 1.5% / 3.3% / 4.1% average loss, keeping bursts of 3-4 packets. The
arithmetic is written out beside each one in `ci_bench_netsim.sh` so the next
person to tune them does not have to rediscover that the four terms are not the
average.

> **This breaks comparability.** `bgp_junk`, `carrier_qos`, `5g_edge` and
> `5g_throttled` rows from run 33024352078 and earlier are not comparable with
> anything measured after commit `15f5f3b`. Any trend line crossing that
> boundary on those four profiles is an artifact of the recalibration, not a
> change in mqvpn.

**D — sample size.** `CI_BENCH_IPERF_STREAMS`, default 4, and the default
sample length raised from 6 s to 10 s. The per-commit gate pins itself back to
6 s so it stays out of the way of the push path. This narrows D rather than
closing it: 4 streams x 10 s still does not fill `bgp_plain` at 380 Mbit over
160 ms, and the profiles above roughly 100 Mbit x 100 ms should still be read as
lower bounds until §1.4's budget allows a longer sample.

**E — `special`'s silent half.** A `skip_row` helper writes
`{"scenario": ..., "status": "setup_failed" | "tunnel_never_up"}`, and all four
scenarios call it on every path that used to fall through to nothing. Four rows
now appear whatever happens.

**§0.1 C — a cancelled job losing the scenarios that had succeeded.** The
results document used to be written once, after the mode's loop returned, which
is why the three timed-out jobs uploaded nothing at all. `emit_results` is now
called after every row and from the `EXIT` trap, `SIGTERM`/`SIGINT` are
converted to a normal exit so that trap actually runs under a
`timeout-minutes` cancellation, and the document carries `complete: 0` until
the mode finishes normally. A consumer can therefore tell a partial artifact
from a finished one, and a cancelled job still ships everything it managed to
measure.

### 0.4 The run that was supposed to confirm §0.3 — three fixes hold, and a larger defect was underneath them

Run [33302660068](https://github.com/r11234567/mqvpn/actions/runs/33302660068)
(2026-08-30, commit `1ead625`) is the weekly §0.3 said would decide. All eleven
netsim jobs went green and every artifact carries `complete: 1`, which is the
first thing in this section that should not be trusted.

**Confirmed fixed.** §0.3 A holds: `hetero_extreme` now reports
`solo_b = 0.0` with `b=tunnel_never_up` instead of echoing path A's number, so
the leaked client is gone and a second measurement is genuinely a second path.
§0.3 B, §0.3 E and §0.1 C hold too — every row carries a `status`, `special`
ships four rows, and the documents are written incrementally.

**F. The entire non-`public` NAT axis never established a tunnel.** This is the
finding that subsumes most of the rest. Sorting the run's rows by NAT type is
unambiguous:

| NAT | Rows | Result |
|---|---|---|
| `public` | every one | measured |
| `port_restricted`, `symmetric`, `cgnat` | every one | `tunnel_never_up` |

6 of 13 `classes` rows, 4 of 10 `combo` rows, 6 of 9 `sched` rows and 2 of 4
`special` rows, plus every `:1400`-and-NAT leg in between. `catalog` and `tiers`
escaped it only because every path they build is `public`.

The cause is §0.1 A again, one translation later. That fix gave the server a
route back to the *client's* subnet with `src $NETSIM_SERVER_ADDR`. A NAT'd path
never presents that address: the hop masquerades the client to its own
server-facing address, `10.<octet>.2.1`, which lives in the **directly
connected** `10.<octet>.2.0/24` — a route the kernel installs itself, with no
preferred source. The server therefore answered from `10.<octet>.2.2`, the
client dropped a reply from an address it never dialled, and the handshake never
completed. Exactly the §0.1 A failure, reached through the one address family of
paths that §0.1 A's fix did not cover.

The assertion added to stop §0.1 A recurring could not see it: it probes
`ip route get $(netsim_cli_ip i)` only, which is the *pre-NAT* address, and it
runs inside `netsim_setup` — before `netsim_apply_path` installs any NAT at all.

**Fixed** by giving the post-NAT source addresses a preferred source too, as
host routes so they beat the connected prefix without disturbing it, and by
probing all three addresses in the assertion:

```bash
for nat_src in "$(netsim_hop_srv_ip "$i")" "$(netsim_hop_srv_ip_alt "$i")"; do
    ip netns exec "$NETSIM_NS_SERVER" ip route replace "${nat_src}/32" \
        dev "$vs" scope link src "$NETSIM_SERVER_ADDR"
done
```

**G. `carrier_qos` still does not measure the thing it exists to measure.** The
§0.3 C recalibration to 1.5% average loss did not work: every row is 0.5–2.3
Mbps across all ten access legs, against the 96 Mbps its own 8000 pps cap allows
at 1500 bytes — 2.4% of its ceiling. Loss was only half the reason. The other
half is the delay term: `150ms` shaped on **both** ends of the transit leg is a
300 ms RTT floor, and four TCP streams at 1400 B over 300 ms cannot exceed about
`(MSS/RTT)·sqrt(1.5/p)` each, i.e. roughly 1.3 Mbps in total at p = 1.5%. That
is what was measured, twice, and congestion control is precisely the term this
profile is defined not to measure.

**Fixed** by moving the latency and loss character to the access leg, where it
belongs — the `5g_*` legs already carry it — and leaving the transit as what
makes it a carrier: a wide pipe with a hard packet-rate ceiling. At 25 ms/end and
0.005% loss the loss-limited ceiling is ~140 Mbps, comfortably above the 96 Mbps
pps cap, so the cap binds and bytes-per-packet becomes the signal.

**H. Two access legs erase the axis they are crossed with.** `5g_throttled`
returned 0.5–0.9 Mbps under *every* transit — `carrier_qos` 0.5, `iplc` 0.9,
`bgp_junk` 0.6, `bgp_opt` 0.8, `bgp_plain` 0.8 — and `5g_edge` 0.5–1.9. A leg
that reports the same number against a 50 Mbit private line and a 380 Mbit
transit port has deleted the transit dimension: for those two rows, six transits
× ten legs bought nothing. Same arithmetic as G, at 190–260 ms of access RTT.
Recalibrated to 0.53% / 0.44% average, bursts of 3.3 packets.

**I. `wifi_busy` is a loss profile wearing a congestion label.** It took
`bgp_opt` from 128.3 to 4.0 Mbps — a 32× collapse — while `min_rtt` moved 77 →
81 ms. Contention that costs 97% of throughput and 4 ms of latency is not
contention. Loss dropped 1.2% → 0.15%; the jitter tail and queue carry the
congestion, and the 60 Mbit rate cap becomes the leg's headline number.

**J. Four metric defects, all of which publish a plausible-looking number for
something that did not happen.**

- `min_rtt_ms` was `max()` over the paths. xquic initialises `ctl_minrtt` to
  `XQC_MAX_UINT32_VALUE`, so an unsampled path publishes `4294967` ms and `max()`
  selects it deterministically. Every row with a dead leg carried
  `min_rtt_ms: 4294967`. The connection's RTT floor is the **minimum**; it is
  now computed over sampled paths only, and the sentinel is filtered.
- `rtt_inflation` was `max(srtt)/max(min_rtt)`, so those same rows published
  `0.0` for a ratio that cannot be below 1. It is now computed per path and the
  worst reported, since srtt and min_rtt from *different* paths do not form a
  ratio that means anything.
- In `tiers`, six of seven rows had `min_rtt_ms: 0` and therefore
  `rtt_inflation: 0` — the control API reports `min_rtt_us / 1000`, and the
  unshaped `lan` profile is sub-millisecond. Now `null` with a note, not `0`.
- `path_minshare` was `min/max`, which ranges to 1.0 and is not a share:
  `sched/one_flapping/minrtt` published `0.833` for a "minimum share". It is now
  `min/total`, comparable against the `path_share_fair` it is published beside;
  the old ratio is kept as `path_load_ratio`.

**K. `aggregation_efficiency` and `vs_best_single` were computed on scenarios
where a leg never came up.** `hetero_extreme` published `1.009` for both with
path B dead — `mp/(a+0)` on what was really a single-path run, reading as
near-perfect aggregation. These two are the gate-able numbers in §4, so a
plausible value from a half-dead scenario is worse than no value. Both are now
`null` with a `ratio_note` unless all three measurements completed.

**L. None of this affected the exit status.** Every scenario loop swallows its
own failure with `|| echo "(continuing)"`, and every mode ends by setting
`RESULTS_COMPLETE=1`, so `complete: 1` only ever meant "the loop reached the
end". A mode in which 9 of 13 rows produced no usable measurement reported
success. There is now a gate, and it distinguishes the two kinds of bad row:

- a scenario that could not **run** is a harness defect and fails the job
  (`CI_BENCH_MAX_FAIL_PCT`, default 25%, counting partial rows against it);
- a scenario that ran and produced a **bad number** is a finding about the code
  under test — annotated, counted, never fatal, because gating on those would
  leave the weekly red until xquic is fixed and it would stop reporting anything.

Replayed against this run's `classes` rows the gate reports
`complete=4 partial=4 dead=5` and exits 1.

> **Comparability breaks.** `carrier_qos`, `5g_edge`, `5g_throttled` and
> `wifi_busy` rows from run 33302660068 and earlier are not comparable with
> anything measured after this commit. `path_minshare` changed meaning in the
> same commit — earlier values are `min/max`, later ones are `min/total`; the
> old quantity is still published as `path_load_ratio`. `min_rtt_ms` and
> `rtt_inflation` changed for the rows that carried the sentinel, where the old
> values were not measurements at all.

**Still not confirmed.** Everything in §0.4 is static analysis plus a replay of
this run's own numbers through the new code — the arithmetic, the metric
handling and the gate were exercised against the artifacts, the emulation was
not. The NAT fix in particular is reasoned from Linux source-address selection
and matches the failure exactly, but no run has demonstrated it. Read the next
weekly before trusting any of it.

---

## 1. Blockers and product defects, before more coverage is worth adding

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

### 1.3 What the matrix now names in the code under test

The point of the harness is to find these, and until this run it had been
finding its own bugs instead. Each item below is stated from rows that are
**not** confounded by §0.4 F or §0.4 G — both legs `public`, both `ok`, default
MTU unless the finding is about MTU — and each is now emitted as a named
`findings[]` entry on the row that produced it, plus a `::warning` in the job
log. They are deliberately not gated (§0.4 L): a gate on these would leave the
weekly permanently red and stop it reporting anything.

**1.3.1 A path's packet size can only ever go up, so a smaller-MTU path
blackholes. — FIXED in xquic [`55de779`](https://github.com/r11234567/xquic/commit/55de779),
not yet confirmed by a run.** The strongest of these, and the only one that is
legible from source alone. It was also reproduced outside the matrix, on a
laptop pairing WiFi with a USB-tethered handset: adding the second path cost
throughput, and pulling WiFi stalled the tunnel for three minutes while the
tethered path sat `ACTIVE`, because the connection was still sized for the link
that had gone away.

The fix makes the PMTU search per path, seeds a new path at the QUIC-guaranteed
size instead of the connection's current one, recomputes `conn->pkt_out_size`
in both directions over the paths whose limit is actually known, and resets a
path to the base size on persistent congestion (RFC 8899 §5.2). Unit coverage
is in `tests/unittest/xqc_pmtud_mp_test.c` in the xquic tree.

**What this means for the numbers below.** The `mtu_*` rows should move, and so
may any row on a `:1400` leg — which is six of ten `combo` legs and five named
classes. It does **not** explain §1.3.2 or §1.3.3: `homo_good` and
`asym_capacity` both ran at default MTU, so their aggregation failures are
still open and still unexplained. Read the next weekly before crossing anything
off.

The original analysis follows.

- `xqc_conn.c:2105` — `xqc_conn_try_to_update_mss` computes the minimum
  `curr_pkt_out_size` across live paths and then applies it only
  `if (min_pkt_out_size > conn->pkt_out_size)`. A path with a *smaller* usable
  MTU can therefore never lower the connection.
- `xqc_send_ctl.c:1723` — a path's own `curr_pkt_out_size` is likewise only ever
  raised, on a successful probe.
- `xqc_multipath.c:309` — a **new** path is seeded from the connection-wide
  `pkt_out_size`, not from the QUIC-safe base, so it inherits a size the new
  path may not support and starts probing from there.

There is no downward adjustment anywhere in that chain. Combined with mqvpn's
`MQVPN_MAX_PKT_OUT_SIZE` of 1400 — a 1428-byte datagram once UDP and IPv4
headers are on it — any leg whose MTU is below 1428 receives packets it cannot
forward, and every drop is fed to congestion control as congestion. Note what
that makes of the matrix's own MTU axis: `1400` is not "a slightly smaller MTU",
it is *below the floor*, and it is the level used by `hetero_extreme`,
`carrier_pair`, `home_plus_tether`, `dual_mobile`, `sat_plus_cell` and six of
ten `combo` legs. `netsim_mtu_class` now classifies every MTU as
`fits`/`boundary`/`below` and the row says which.

The measurements are consistent with it: `mtu_1400` 83.8 solo → 26.6 multipath
(`vs_best_single` 0.317), `mtu_split` 74.3 → 19.8 (0.266), `mtu_blackhole`
98.4 → 19.2 (0.195). Adding a second, otherwise healthy path costs up to 80% of
throughput. Also worth fixing while in there: the `max_pkt_out_size` captured at
`xqc_conn.c:2101` sits *inside* the branch that tracks the minimum, so the PMTUD
probe ceiling is taken from whichever path currently holds the smallest packet
size rather than the maximum its name claims.

**1.3.2 Two identical healthy paths do not aggregate.** `homo_good` is two
`eth:bgp_opt:public` legs — same profile, same NAT, same MTU — measuring 129.5
and 129.6 Mbps alone and **135.1 Mbps together**: `aggregation_efficiency`
0.521, `vs_best_single` 1.042. A 4% gain from doubling the paths. `mtu_1500`,
the other all-identical pair, gives 0.665 / 1.012. For comparison the core suite
on `ci_bench_env.sh`'s topology shows what a working pair looks like in the same
run: `equal_paths` single 45.3 → WLB 89.1.

**1.3.3 Multipath is *worse* than the best single path when the legs disagree.**
`asym_capacity` pairs `eth:iplc:public` (50 Mbit) with `eth:bgp_plain:public`
(380 Mbit); both came up, both `public`, default MTU. Solo 42.6 and 85.9,
together **45.6** — `vs_best_single` 0.531. Adding the private line to the
commodity port halved it. This is the head-of-line case the matrix was built to
find, and it is the one `NETSIM_CLASS` documents as "aggregation has to use both
without letting the small one become the head-of-line".

**1.3.4 The scheduler does not split evenly between identical paths.** `tiers`
runs `tier_ref` — two unshaped `lan` legs with nothing to tell them apart — and
the byte split came back between 0.126 and 0.338 (`min/max`) on all seven rows.
Nothing in the emulated network accounts for it, which is what makes `tiers` the
right place to read fairness. `run_tier` now raises `share_imbalance` when the
minority path carries under half its fair share.

**1.3.5 WLB falls below single-path where MinRTT does not.** From `sched`, on
`one_flapping` (both legs `eth`, `public`), the three schedulers separate
properly for once:

| Scheduler | `vs_best_single` | Byte split (min/max) |
|---|---|---|
| `wlb` | 0.978 | 0.096 |
| `minrtt` | **1.164** | 0.833 |
| `backup_fec` | 1.101 | 0.131 |

WLB leaves 90% of the second path unused and ends up slower than not using it at
all. §2.6 predicted the opposite ("WLB beats MinRTT at every stream count"), so
whichever is right, one of them changed and neither had a regression guard. The
other six `sched` rows are worthless for this — §0.4 F left path B dead, so all
three schedulers measured the same single path to within 0.3%.

**1.3.6 The control API publishes an unset sentinel as a measurement.**
`ctl_minrtt` is initialised to `XQC_MAX_UINT32_VALUE` and reset to it on a route
change (`xqc_send_ctl.c:129`, `:215`, `:320`, `:1588`); `xqc_multipath.c:955`
copies it to `path_min_rtt`, mqvpn forwards it as `min_rtt_us`, and
`control_socket.c:283` divides by 1000 — so a path that has never taken an RTT
sample reports `min_rtt_ms: 4294967`. The path stats carry no "no sample yet"
flag to read instead, so every consumer has to know the sentinel by value. The
harness now filters it (§0.4 J); the API should expose the state instead. The
same rows show `srtt_ms: 250`, which is xquic's initial RTT default — an
unvalidated path is indistinguishable from a 250 ms one.

### 1.4 Budget structure

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
| `full_cone` | `masquerade --persistent` (mapping stable across destinations) |
| `port_restricted` | plain `masquerade` (conntrack default) |
| `symmetric` | `masquerade --random` (fresh port per destination) |
| `cgnat` | `masquerade --random --to-ports 20000-20255` — one translation with a carrier port block |

`cgnat` was specified here as a double NAT and implemented as two `MASQUERADE`
rules in the same `POSTROUTING` chain on the same device. `MASQUERADE`
terminates, so the second rule was unreachable and `cgnat` was byte-for-byte
`port_restricted`. A real second translation needs a second routing hop, which
is the N-hop chain in §2.1 and is not built. What *is* expressible at one hop is
the other defining property of carrier-grade NAT — the subscriber port block —
so that is what the level now models, and it is labelled as a single
translation rather than claiming two.

`full_cone` likewise carried `--random-fully --persistent`, which asks for two
opposite things: a fresh random port per flow, and a mapping that does not move.
With both set it differed from `symmetric` only in degree.

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

**The axis has a floor, and two of its three levels are under it.** mqvpn emits a
1400-byte QUIC packet, which is a 1428-byte datagram once UDP and IPv4 headers
are on it, so `1400` and `1280` are not smaller MTUs — they are MTUs the
client's default packet does not fit through at all. Every reading on those
levels is dominated by §1.3.1 rather than by MTU sensitivity, and since `1400`
is also the level `hetero_extreme`, `carrier_pair`, `home_plus_tether`,
`dual_mobile`, `sat_plus_cell` and six of ten `combo` legs use, that reaches
well beyond `mtu` mode. `netsim_mtu_class` now labels each level
`fits` / `boundary` / `below` against the 1428-byte floor, every row carries
`mtu_class_a` / `mtu_class_b`, and a `below` leg is called out in the job log
when the path is built. Reading MTU sensitivity as such needs either §1.3.1
fixed or levels chosen above the floor — 1500 / 1450 / 1428 would be an axis
this client can actually traverse.

The `carrier_qos` crossing is separately blocked on §0.4 G: `mtu_pps_1500` and
`mtu_pps_1280` came back at 0.6 and 0.3 Mbps, because both legs are
`5g_throttled:carrier_qos` and so carried both defects at once. With the profile
recalibrated the pps ceiling is 96 Mbps at 1500 and 81 at 1280 — a 16% gap,
which is the signal that pairing exists to produce, and which needs the
measurement CV (17–45% in this run) brought down before it is readable.

### 2.5 The remaining special condition

- **Live roaming under load.** **Done** — `run_special`'s `roam_under_load`
  rotates the hop's SNAT source mid-transfer and flushes conntrack, because
  without the flush the live UDP flow keeps its old translation, the server
  never sees a new peer address, and the test passes having exercised nothing.
  The row carries `roam_applied` so a run on a box without conntrack is not
  mistaken for a clean result.

  It has still never executed. Its path is `5g_full:bgp_plain:port_restricted`,
  so §0.4 F killed it before the roam: run 33302660068 reported
  `roam_under_load` and `nat_aging` as `tunnel_never_up`, which is half of
  `special`. Both should run once the NAT reply-source fix lands, and both now
  derive their `status` from the samples rather than writing the literal `ok`
  the way all three of `special`'s measuring scenarios used to.

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

Run 33302660068 reverses that on the one class where the comparison was valid:
on `one_flapping` MinRTT reached `vs_best_single` 1.164 while WLB sat at 0.978,
below single-path (§1.3.5). The other two classes were vacuous — §0.4 F left
their B leg dead — so this mode has produced exactly one usable comparison so
far, and it disagrees with the paragraph above. The regression guard is still
the right idea; what it should guard is no longer obvious.

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

   §0.4 changed what several of these carry, and the page has to keep up.
   `aggregation_efficiency`, `vs_best_single`, `rtt_inflation`, `srtt_ms`,
   `min_rtt_ms`, `path_minshare`, `roam_retention`, `survived_ratio` and
   `dl_retention` are now **nullable**, and a null means "not measurable here",
   which is a different thing from zero — plotting it as zero would reintroduce
   exactly the error §0.4 J and §0.4 K removed. New fields worth surfacing:
   `findings[]` and `finding_count` (defects the harness named on that row),
   `status_a` / `status_b` / `status_mp`, `mtu_class_a` / `mtu_class_b`,
   `paths_seen` / `paths_rtt_sampled` / `stats_source` (why a metric is
   missing), `path_share_fair` (the value `path_minshare` should be compared
   against), `path_load_ratio` (the old `path_minshare` quantity), and
   `binding_constraint` / `ceiling_utilisation` / `rate_ceiling_mbps` /
   `pps_ceiling_mbps` on catalog rows. A card showing `findings[]` is the
   cheapest version of the whole dashboard ask.
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
running inside it. These are not final numbers, and the §0.3 fixes make them
obsolete upward on purpose: `run_pair` now performs three real measurements
where it used to do one and two repeats of it, the sample went from 1 x 6 s to
4 x 10 s, and the tunnel wait from 25 s to 40 s. For `classes`, the longest
mode at 13 pairs, that is about 28 minutes typically and about 49 if every
tunnel wait runs to its cap — against the 19m10s above. **The `netsim`
`timeout-minutes` was raised 60 → 90 for that reason**; the incremental result
writing in §0.3 keeps a cancellation from destroying the whole artifact, but it
cannot recover the scenarios that never ran. Re-measure from the next run
before sharding further.

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

0a. ~~**Make the multipath rows mean something** — the leaked-client bug in
   `run_pair` (§0.2 A).~~ **Done and confirmed** by run 33302660068: `solo_b`
   now reports its own path's state instead of echoing path A's number. Treat
   `aggregation_efficiency` and `vs_best_single` from 33024352078 and earlier as
   invalid rather than as a baseline.

0d. **Make the NAT axis connect at all** — the post-NAT reply source (§0.4 F).
   Nothing behind a NAT has ever completed a handshake, so `cgnat`,
   `port_restricted` and `symmetric` have never produced a measurement, and
   `sched` and `special` are half-vacuous because of it. **Written, not
   confirmed.** This is now the item that blocks the most: it is the difference
   between 4 and 13 usable rows in `classes`.

0e. **Stop a dead matrix from reporting success** — the completeness gate
   (§0.4 L). Written. Until it runs, `complete: 1` in any artifact from
   33302660068 or earlier means only that the loop reached the end.

0b. ~~**Stop a failure from looking like a measurement** — a reason field
   instead of a bare `0.0` (§0.2 B), and a row from `special`'s failure path
   (§0.2 E).~~ **Fixed** (§0.3 B, §0.3 E). The same commit also closes §0.1 C,
   so a cancelled job no longer discards the scenarios that succeeded.

0c. **Recalibrate what the numbers rest on** — the `loss gemodel` profiles
   (§0.2 C) and the short single-stream sample that cannot fill a high-BDP path
   (§0.2 D). The profiles were recalibrated once (§0.3 C) and **it was not
   enough**: `carrier_qos` still measured congestion control rather than its
   packet-rate cap, and two access legs still erased the transit axis
   (§0.4 G, §0.4 H). Recalibrated again, this time by moving the latency out of
   `carrier_qos` rather than only lowering loss. Every catalog row now carries
   `binding_constraint`, so the next time a profile fails to measure what it
   exists to measure the artifact says which term bound it instead of leaving it
   to be rediscovered. The sample is 4 streams x 10 s (§0.3 D); **D is narrowed,
   not closed** — `bgp_plain+eth` sits at 13.8% of its configured ceiling and
   reports `loss_or_rtt`, so the widest transits are still lower bounds.

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
5. NAT matrix and MTU axis (including the PMTUD black hole). Built —
   `NETSIM_NAT`, `mtu` mode, `netsim_set_pmtud_blackhole` — but **neither axis
   has produced a valid reading yet.** The NAT axis never connected (§0.4 F),
   and on the MTU axis every level below 1428 is under mqvpn's own outer
   datagram, so those rows measure §1.3.1 rather than MTU sensitivity. Both are
   addressed above; both need a run.

5a. **Fix the one-way PMTU state in xquic** (§1.3.1). Until a path's packet
   size can go *down*, the MTU axis cannot measure MTU, the `:1400` legs
   scattered through `NETSIM_CLASS` are measuring a blackhole, and multipath is
   actively harmful on any pair whose legs differ in MTU. This is a product fix,
   not a harness one, and it gates the reading of items 3 and 5.
6. ~~Dashboard descriptions and glossary.~~ **Done** — per-test descriptions,
   a direction-aware metric glossary, and the emulated profile shown on each
   card via the series API's new `context` map.
7. Scheduler sweep (`sched` mode) and roaming under load are both **built and
   both still largely unmeasured** — §0.4 F left two of `sched`'s three classes
   vacuous and `roam_under_load` never past its handshake. The dual-stack split
   is blocked on the client; see §2.5. Endurance jobs remain unscheduled.

8. **Investigate what §1.3 names.** The aggregation deficit (§1.3.2), the
   multipath regression on disagreeing legs (§1.3.3), the byte-split imbalance
   on identical paths (§1.3.4) and the WLB/MinRTT inversion (§1.3.5) are all
   findings about mqvpn and xquic rather than about the harness, and all four
   are now emitted as named `findings[]` on the rows that produce them. They are
   not gated on purpose. §1.3.1 is the one to start with, since it plausibly
   explains part of §1.3.3.

Still open, in short: the NAT axis actually connecting (§0.4 F) and the
completeness gate (§0.4 L), both written and unconfirmed; the one-way PMTU state
in xquic (§1.3.1) and the four scheduler findings behind it (§1.3.2–§1.3.5); the
N-hop chain and geographic routes (§2.1); the sample length on the widest
transits (§0.2 D, narrowed but not closed); the 2.5 G/1 G line rates (not
feasible on a shared runner — §5); the dual-stack routing split (blocked on the
client — §2.5); the fifth special condition; and the endurance jobs.

The pattern across §0.1, §0.2 and §0.4 is worth stating once: every round of
this has been the harness certifying itself healthy while measuring nothing,
and each round's guard was narrower than the bug class it was written for. The
`ping -I` check passed on an unusable service address; the `ip route get` check
that replaced it passed on an unusable NAT'd address; `complete: 1` and a green
job passed on a matrix that was two-thirds dead. §0.4 L's gate is the first one
that fails the job on the *outcome* — how many scenarios produced a measurement
— rather than on a proxy for it, which is the only form of this check that does
not need to anticipate the next variant.
