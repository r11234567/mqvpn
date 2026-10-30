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
