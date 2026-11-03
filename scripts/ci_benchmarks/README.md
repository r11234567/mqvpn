# Benchmark measurement contracts

The harness validates measurements separately from product performance. Product
crashes, sanitizer errors, missing required phases and incomplete transfers remain
failures. Changing the harness must not make them disappear.

- NAT profiles apply to the outer UDP tunnel. `full_cone` uses persistent SNAT;
  `cgnat` uses UDP masquerade with the port block 20000–20255. Names are legacy
  mapping approximations, not proof of RFC 3489 filtering behaviour. Unsupported
  requested rules fail without substituting plain masquerade.
- Inner QUIC uses the same object size for direct, solo-A, solo-B and MP repeats
  in a scenario. The default is 128 MiB with a 90-second deadline. The lossy
  12-Mbps `hetero_extreme` profile uses 2 MiB so the baseline is not asked to
  deliver almost its entire nominal capacity as application data. Short fast
  transfers are completion measurements, not steady-state oscillation evidence.
  `CI_BENCH_QUIC_BYTES` explicitly overrides all scenarios;
  `CI_BENCH_QUIC_SLOW_BYTES` overrides the slow profile only. Bytes and deadlines
  are recorded, and A/B comparisons require matching budgets.
- A complete QUIC MP rate below 80% of the best same-protocol solo adds a visible
  degradation finding. Below 50% is a hard regression when MP and both solos each
  have at least three samples with CV at most 25%. These are explicit harness
  policy thresholds, not protocol guarantees. Missing baselines stay inconclusive.
- Every VPN process instance has a separate `raw/.../vpn/<role>...` directory
  with context, log and observed exit status, including startup failures. No auth
  key, private key or full command line is copied into process context.
- One-shot iperf servers get a 30-second drain period after a successful client
  (`CI_BENCH_IPERF_SERVER_GRACE` overrides it), then a separate two-second stop
  deadline. A failed client uses a short drain. Natural server errors and
  SIGKILL still invalidate the sample. An observed harness SIGTERM after a
  successful client and complete receiver result is recorded as
  `server_teardown=harness_cleanup`, rather than fabricating a transfer failure.
- iperf JSON stdout and stderr warnings are separate; pristine stdout survives
  JSON metadata updates. Every UDP call also records before/after namespace
  addresses, routes, TCP/UDP socket details, qdisc/counter/firewall state, and
  full-snaplen PCAPs of TCP control-port/ICMP packets at both endpoints, using
  libpcap immediate mode so fast failures do not stay buffered at shutdown. No
  packet rotation or head/tail clipping is used. Capture startup, drops and
  shutdown are retained in capture logs. The manifest validates saved packet
  records against tcpdump statistics; header-only captures, truncation and
  kernel drops are explicitly marked incomplete. Unavailable captures and timed-out
  diagnostic commands are explicit. Bulk UDP payload is outside the filter.
- On failure, the full current client/server VPN logs are copied beside the
  raw traffic logs. `*.evidence.json` inventories artifact names, sizes, SHA-256,
  exit codes, timing/context links and copy errors. These files live under the
  existing always-uploaded raw artifact tree. Extra evidence helps diagnose
  control resets but does not by itself prove a particular product root cause.
- Fault stress stops after a required VPN/traffic process exits. Requested,
  attempted, failed and unexecuted cycles are distinct; aborting still fails the
  run. The first failing cycle and abort cause remain in the summary.

Run the harness regression checks on Linux:

```sh
python3 -m unittest discover -s tests -p 'test_benchmark*.py' -v
sudo bash tests/test_benchmark_harness.sh
bash tests/test_benchmark_processes.sh
sudo bash tests/test_benchmark_netsim_setup.sh
```

These checks validate the harness and actual local TCP/UDP/NAT operations. They
do not replace a full weekly/stress run of the product.

For a focused hosted check, dispatch `perf-weekly.yml` with `quick_udp=true`.
It uses a separate concurrency group and a 15-minute job limit, reuses the
Release build/cache, runs only the standalone rapid control-close regression
and six five-second UDP DL/50-byte samples (5/10 Mbps, single/WLB/MinRTT).
Capture integrity is checked even if a transfer fails. Core/netsim suites,
comparisons and R2 publishing are skipped; results are explicitly partial and
uploaded as `perf-udp-quick-<commit>`. Build/queue time still counts toward wall
time; this does not promise a fixed completion time.
