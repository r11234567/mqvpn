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
