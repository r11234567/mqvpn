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
