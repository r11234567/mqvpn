# GUI phase A verification record

Baseline: MQVPN `13a92f2ed9e1281b8b1cecb9db6a4bbef78a2dec`, XQUIC
`b6de7f6ad7be5262d8b06403bfc5f078037ac55e`, BoringSSL
`427ec40cc8edc545253289232e885708c409e5ea`. Neither dependency pin changes in
this phase. The source-identical XQUIC empty commit is not a reason to bump it.

Implementation followed the saved plan: encoding/persistence, reset,
connection/process/routing semantics and Path ID compatibility, packaging and
updater, then CI wiring. Full configuration/UI alignment and README examples
remain behind the first CI gate.

| Review item | Change | Evidence and remaining gate |
| --- | --- | --- |
| R1 | Token restricted to non-redirecting GitHub API requests; downloads have no authentication headers | Static request/header review; no credential-leak reproduction |
| R2 | Same-run, exact-SHA core artifact and submodule provenance; strict architecture | Script/workflow review and actionlint passed; final native packages pending CI |
| R3 | GUI/updater published self-contained, native SQLite extraction checked | Local x64 actual publish/start and SQLite reopen passed; complete ZIP and ARM64 pending CI |
| R4 | Atomic no-BOM UTF-8 writes, BOM/legacy reads, backup before conversion, file/DB save recovery | Production persistence checks passed: Unicode, idempotent migration, unchanged load, failed-commit rollback, individual invalid-node isolation |
| R9 | Transactionally replace nodes/settings, valid active ID, replace default file, require safety backup | Production reset, database close/reopen and actual safety-backup restore passed |
| R13 | Tests share production policy semantics; real process host and update transaction; native final-ZIP checks | Local behavior suite passed; screenshot capture is not visual regression; final package gate pending CI |

The native PE checker found a real additional defect in the previously green
baseline artifact: `mqvpn.exe` imports MSVC runtime DLLs absent from that
artifact. The main Windows build now consistently requests `/MT` and invalidates
the affected caches. The final package's import checks must confirm this on
both native runners; it is not counted as verified merely because the flags
were changed. Exact locked Xray x64 and pinned Wintun x64 binaries passed the
local import/architecture checker.

Additional local evidence includes ordered asynchronous operations,
display-name-independent routing classification, runtime default-adapter
resolution without modifying saved files, real hidden process-host UTF-8
output, graceful CTRL_BREAK exit, ordinary process exit reasons, file-lock
rollback, preserved user configuration, explicit correction of old excessive
Path IDs, supported core update interface selection, and actual dependency
license/Wintun-hash processing functions. No host VPN, routes or WFP rules
were activated by these checks.

Final review also catches asynchronous exit-cleanup/restore failures at the
UI boundary, keeps stable-release fallback on the resolved stable tag, and
requires complete SHA256 inputs in the dependency lock. The real lock was
resolved and its four URLs/digests checked after the final script change.
The final GUI publish and package transaction checks passed again. An
initial local default-host launch failed to load SQLite; another launch
terminated before writing a report. The unchanged published executable
then passed the PowerShell 7 diagnostic entry with a fresh extraction
directory, SQLite reopen and .NET 9.0.17. The cause of those earlier launch
failures is not established; native final-ZIP CI remains the acceptance gate.

The full GUI/core configuration mapping, integer precision, platform-specific
settings, paired README examples and real C parser round trips belong to
phase B and are not claimed complete here. Live network/route/WFP acceptance
also remains unperformed. Keep PR #15 unmerged and wait for current-head
Windows/GUI checks before phase B. No Performance, Weekly, Stress or Release
workflow is manually started by this work.

## Hybrid e2e follow-up at the phase A gate

Runs 41003746880 and 41007117110 passed native Windows x64/ARM64 core and
final GUI ZIP verification. The latter failed in H2 functional Test 8:
IPv6 throughput was 40.1 Mbps and both paths carried MBs, but the lighter
path's share was 0.17 against 0.2. IPv6 connectivity and flow recovery passed;
the Release performance step was skipped, so the earlier BASE-B throughput
collapse was not retested by that run.

A real local netns comparison exposed a separate backpressure defect:
`xqc_h3_stream_send_data` clears write notifications on a full accept, while
mqvpn can still owe earlier withheld TCP receive credit. Retain withholding
and re-arm the drain notification after subsequent full accepts until the
low-water callback restores all credit. The corrected H3 test substitute and
two queue-drain cases fail ten assertions on the old code and pass on the fix
(4786 assertions). XQUIC scheduling, API, configuration and dependency pins
remain unchanged. This source defect is confirmed; its connection to the
earlier CI BASE-B failure still needs direct evidence.

Test 8 now uses equal lossless 10 Mbps legs, retains the 50 KB / 0.2 path-load
gates, and additionally requires receiver throughput above 15 Mbps. The real
Debug tunnel achieved 18.4 Mbps with share 0.99 and passed flow recovery. An
actual single-path negative control achieved 9.2 Mbps and was rejected by both
the capacity and path-count assertions. Remove idle post-burst polling,
preserve the immediate path snapshot, and capture full state on either gate
failure. Test 3 retains its asymmetric loss/jitter workload and existing
gates/retry budget. The workflow also runs the independent Release performance
step after a failed functional step, preserving both failures as job failures.

Current-source clang Debug `-Werror` ASan/UBSan CTest: 58/58 passed. Bash,
ShellCheck (existing SC2207 exclusions), clang-format 18.1.3 and actionlint
checks passed. The complete real-netns H2 Debug functional and Release
performance suites both passed. The full functional run repeated IPv6 at
18.3 Mbps / share 1.00; BASE-B samples were 36.2, 20.7 and 15.4 Mbps without
retries, and asymmetric multipath averaged 49.1 Mbps / share 0.78 (1.81x the
best single-leg average). These local measurements are not hosted-CI results.
The new-head CI gate remains pending; phase B stays paused and PR #15 unmerged.
