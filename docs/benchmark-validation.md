# Benchmark measurement contracts

The performance and stress workflows exercise Linux namespace topologies.
An executed workflow is not, on its own, evidence that every application
transfer succeeded or that every performance requirement was met.

## Receiver measurements

`scripts/ci_benchmarks/ci_bench_results.py` validates the required measurement
fields for each core benchmark. Stream counts, offered rates and other
configuration metadata cannot satisfy a throughput check. Missing or duplicate
cells, non-finite values, missing receiver data and absent failover recovery
measurements are rejected.

The shared iperf helper retains raw JSON under `ci_bench_results/raw/`, including
the client exit code, requested duration, observed receiver duration and failure
reason. A complete sample must exit successfully and cover at least 90% of its
requested receiver measurement duration. The small tolerance accommodates
measurement boundaries; it does not turn a short transfer into a soak result.
Malformed output is retained as an additional `.invalid.log` file. Callers may
delete their temporary copy without deleting this evidence.

Throughput is the receiver's delivered rate. Sender-only summaries are invalid.
The parser emits `NA` for an invalid sample, never synthetic zero throughput or
zero loss. A numeric decline can be a valid measurement of a performance problem;
measurement validity and performance health must be reported separately.

## Netsim coverage and verdicts

`ci_bench_netsim_results.py` maintains the expected scenario/scheduler manifest
for every mode. Missing and duplicate rows remain visible in the result and
fail the gate. FEC cells are required by the scheduler mode; a build without
FEC is incomplete coverage rather than a smaller successful matrix. Cancelled
runs retain the manifest and the rows that completed.

`execution_complete` means the scenario loop finished. `measurement_complete`
also requires every expected transfer, including QUIC repeats and game traffic,
to have a valid result. The legacy `complete` field now follows this stricter
meaning. `harness_valid` identifies setup, manifest and generator defects.
`performance_health` is `pass`, `degraded` (diagnostic findings), `regression`
(valid zero delivery), or `inconclusive` (incomplete evidence). A valid
performance decline is never reclassified as a setup failure.

Incomplete measurements and zero delivery fail the gate. Diagnostic findings
emit warnings and remain visibly `degraded`; `CI_BENCH_STRICT_PERF=1` additionally
fails on them. Their heuristic thresholds are not statistical proof of a
regression. Both endpoints use the row's scheduler during paired measurements.

## Stress contracts

Every requested reconnect and fault cycle must succeed. Every multi-client
connection must pass its later connectivity check and complete both upload and
download traffic. Clients share a global connection deadline from the launch
batch. Each client uses its own iperf port and a default 2 Mbit/s offered rate
for 30 seconds in each direction; raw per-client receiver measurements survive
on both successful and failed runs. `CI_STRESS_NUM_CLIENTS` (1..64) and
`CI_STRESS_CLIENT_TRAFFIC_SEC` permit smaller integration runs.
Each direction also reports minimum/maximum/sum delivered client rates and
Jain fairness. Incomplete clients remain in the recorded count and make the
fairness index inconclusive, rather than disappearing from it.

The long-running test requires successful iperf exit, a full receiver sample,
positive delivery, live VPN processes and continuous inner receiver progress.
`duration_sec` remains the requested duration for existing consumers; use
`observed_duration_sec` for the actual receiver interval. Raw progress, iperf,
resource and VPN logs are retained below `ci_stress_results/raw/`.

Default faults include admin-down, peer carrier loss, bidirectional blackhole
and each one-way blackhole. Each requested kind must exercise A and B. The test
requires inner probes and bulk receiver progress while faulted, and real bytes
on the restored path as well as its active marker. Silent-blackhole product
defects are reported as failures rather than omitted to keep the job green.

All owned VPN children are reaped even if they already exited. Graceful stop
has a bounded watchdog, and forced SIGKILL fails. Captured sanitizer diagnostics
are checked separately from exit codes. The stress build explicitly instruments
mqvpn, the separate xquic build and BoringSSL with ASan/UBSan and uses new cache
keys so old uninstrumented dependencies are not reused.

Missing, malformed or gapped resource observations fail. At least three samples
covering two configured intervals are required. RSS checks use a 30-second
warmup and both 50% and 32 MiB growth budgets; slopes and raw curves are retained.
Short runs without three steady samples explicitly report insufficient memory
evidence. ASan memory growth is `inconclusive_asan`, never a claim of no leak;
the FD check still applies. A separate ordinary optimized soak is necessary to
interpret steady memory use.

## Harness regression tests

Run without a VPN build:

```sh
python3 -m unittest discover -s tests -p 'test_benchmark*.py' -v
sudo bash tests/test_benchmark_harness.sh
```

The Python suite includes failed/partial transfer, timeout, sender-only and
metadata false-pass fixtures. The root integration test exercises real iperf3
TCP/UDP in both directions and verifies that raw evidence survives temporary
file cleanup. The `Benchmark Harness` workflow runs these checks on Linux.

## Extended weekly measurements (version 2)

UDP sweeps now use the common timeout, exit and receiver validator. Each
direction and payload size has a separate result document and complete rate
manifest: UL/DL, 50-byte packets at 0.2/0.8/2/5/10 Mbit/s and 1100-byte packets
at 10/50/100/200/250/300/350/380 Mbit/s. The default duration is 10 seconds.
All points retain receiver bytes, duration, loss and jitter; failed parsing
produces null metrics and fails. Saturation is an ascending first-crossing
bound: `below_sweep`, `bracketed`, `above_sweep` or `incomplete`. It is not an
exact path-capacity estimate, and a single ascending sweep cannot establish
hysteresis or statistical significance. Version-1 sweep values are not directly
comparable because direction, payload, rates and receiver extraction changed.

Failover uses client-side **reverse-mode receiver** intervals and actual
monotonic fault/recovery timestamps, with raw actions, receiver output and UDP
echo observations retained. A threshold must hold for 1.5 seconds. The nominal
resolution is 0.5 seconds; interval arrival includes stdio and scheduling delay.
TTF/TTR describe receiver throughput restoration and do not measure xquic's
internal detection time. Echo gaps are measured separately, including phase
boundaries; missing fault application, receiver phase coverage or recovery fails.
These version-2 measurements replace sender-socket throughput and planned sleeps.

Each inner QUIC cell includes three attempts each for direct path A, tunnel
solo A, tunnel solo B and tunnel multipath, with the same 128 MiB object and
pinned BBR implementation. Successful exit, exact received object bytes and a
completed receiver summary are required. A timeout stays incomplete even if
partial data arrived. All client/server logs and per-attempt measurements are
retained. Sampling covers the first MP attempt only, sums both path interfaces,
and also retains individual interface counters. A short attempt cannot prove
steady-state oscillation, and wire counters alone cannot identify nested
congestion control as its cause.

The scheduled matrix adds a `paths` mode with 1/2/4/8 configured and verified
active paths and both TCP directions. This is Linux namespace performance
coverage; existing platform CI still owns Windows/mobile lifecycle coverage.
The MTU mode runs four serial same-VM arms: PMTUD off/on x DL/UL, in a shuffled
order with pair identity. PMTUD is configured using `[Interface] PMTUD`; the
product default is unchanged. Config, control state and both TUN MTUs are saved
per phase. The MTU correlation warning does not by itself diagnose PMTUD.

Game mode similarly pairs production defaults and the tuned UDP-pin/offload-off
configuration on one VM. Offered gamegen PPS is recorded as its actual default
4267, independent of the iperf PPS axis. Ordered-channel stall figures remain a
synthetic timeout model, not measured freezes in a real game. Optional parallel
workflow arms can use different physical VMs and remain observational.

NAT, MTU, ICMP blackhole and packet-rate policing are required when requested.
Setup-command failures propagate even when Bash callers use a conditional.
Raw kernel link/qdisc/filter/NAT snapshots accompany each path. Both UDP
conntrack timeouts are pinned; the idle-expiry case records state before and
after idle and explicitly expires UDP mappings if keepalives preserved them.
That forced-expiry mechanism is reported rather than presented as natural
aging. NAT profiles emulate selected translation behavior; they are not full
models of every commercial carrier's filtering and multi-stage NAT.

Background spike errors invalidate the mode. Teardown stops namespace-owned
processes before deleting namespace handles. The sampler retains raw aggregate
CSV, per-leg counters and diagnostics. At least the existing minimum shape
coverage is required before the oscillation analysis can form a finding.

Stress now builds separate full ASan/UBSan and optimized Release dependencies,
with profile-specific caches. ASan long tests cover each direction; an ordinary
Release job runs a two-hour upload soak with receiver progress and interpretable
RSS curves. Configuring this job does not constitute evidence it has passed.

Independent core benchmarks continue after a measurement fails, while the job
remains failed. Actions artifacts include the entire result tree, including raw
logs and JSON. Public R2 uploads include only top-level result documents; raw
configs and logs stay in the Actions artifact. Existing functional PMTUD/IPv6
tests are still required; broad synthetic coverage is not proof that all real
radio, NAT, application mixtures or device behaviors are covered.
