# Performance acceptance

Measurement completeness and performance health are separate verdicts. A green
performance job requires both. Preserve failures and all raw samples; do not
retry until success, replace errors with zeroes, or discard slow repeats.

- Netsim findings fail by default, including repeat CV above 25%. A local
  exploratory run may set `CI_BENCH_STRICT_PERF=0`; its degraded verdict is
  still not healthy-performance evidence.
- Multipath goodput must retain at least 80% of the same-run best solo path.
  Apply this to every TCP stream-count cell, scheduler scenario and NTN cell.
  Independent runner or protocol results are not interchangeable baselines.
- Inner-QUIC measurements require all requested objects and same-protocol
  baselines to complete. Unstable or incomplete repetitions cannot pass by
  their median. WLB and `wlb_udp_pin` now use the same UDP affinity policy;
  retain both names in historical reports, but do not interpret their rows as
  a packet-striping experiment after this change.
- UDP sweeps retain at least three repetitions per condition/rate, require
  exact coverage and fail on any invalid repetition. Startup failures are not
  capacity crossings. Captures retain four-byte UDP startup messages, TCP
  control and ICMP to diagnose reply/data races without capturing bulk load.
- iperf receiver duration may fall short by at most 2% or one second, whichever is smaller;
  positive receiver bytes/time and reported bitrate must agree within 2%.
  Server error logs invalidate a result even when the process exits zero.
- Continuous TCP stress requires positive receiver evidence for every stream
  and Jain fairness of at least 0.8. The default aggregate progress deadline
  is three seconds. This does not claim absence of individual-flow stalls.
- Release memory growth and descriptor checks remain independent of these
  performance checks. ASan RSS is not proof of bounded Release memory.
- Reorder e2e requires complete UDP receiver reports with the feature enabled
  and disabled under the same explicit per-path packet disorder. A ping cannot
  replace a failed UDP transfer. Retain both iperf reports, exits and VPN logs;
  keep the 0.1% late-drop ceiling and verify the disabled control actually
  reorders packets. An interrupted fault runner writes a failed, incomplete
  summary with attempted and unattempted cycles rather than losing its verdict.
- Branch validation retains workflow artifacts without publishing experimental
  measurements into the main branch's R2 performance history.

These thresholds are acceptance criteria, not proof that all deployment
conditions are safe. Low-MTU PMTUD, repeated reconnect cleanup, game traffic,
ACK starvation and multi-client result exchange retain their own coverage.
Do not weaken these gates to accommodate an unresolved product regression.
