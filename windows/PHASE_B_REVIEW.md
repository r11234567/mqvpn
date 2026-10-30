# GUI configuration alignment and verification

Baseline: MQVPN `ef1bd0e9487cc02456f0dfb03fb65658746383ed`, PR #15,
`sync/upstream-clean`. XQUIC remains
`b6de7f6ad7be5262d8b06403bfc5f078037ac55e`; BoringSSL remains
`427ec40cc8edc545253289232e885708c409e5ea`. No core C source, dependency
gitlink, main branch or upstream history is changed by this stage.

Phase A's baseline Windows x64/ARM64, final native GUI packages and Hybrid e2e
were green in [CI 41011816963](https://github.com/r11234567/mqvpn/actions/runs/41011816963).
The user accepted that gate before this configuration stage began.

## Configuration map

`MqvpnProfileConfiguration.Fields` drives Windows defaults, integer bounds,
enum choices, validation, editing bindings and the contract expectations.
The C parser remains authoritative; tests observe its output rather than
treating a tolerant parser exit code as proof of correct settings.

| UI / JSON | C parser and client bridge | Windows consumer / result |
| --- | --- | --- |
| server_addr / SNI / auth / insecure | `src/config.c`, endpoint parser and `mqvpn_client_cfg_t`, shared setters | Windows host resolution and config-scoped verifier; bounded UTF-8 strings, strict port validation; Windows stores unless existing CA environment overrides apply |
| DNS / paths / tun_name | file config arrays (4 DNS, 8 paths), 32-byte name buffers | Windows DNS/TUN and FriendlyName interface lookup; default adapter resolves at connection time, never during editor save |
| scheduler / cc / request_server_scheduler / redundant / reinjection | shared name maps, deadline limits and UDP port parser, `client_config_bridge.c` | Same-build Windows core enables BBR2/unlimited/FEC/XOR/Hybrid; STREAM stays MinRTT. Redundancy never rewrites CC |
| init_max_path_id / reconnect | unsigned Path ID ≤128, positive signed reconnect interval, shared setters | 0 retains Path ID default 8; old excessive input is shown and explicitly rejected, never clamped |
| log_level | CLI string → enum → bridge | Main GUI log selection intentionally overrides node default at launch; documented in editor |
| manage_routes / kill_switch | platform config and shared killswitch hint | Core Windows routes and dynamic WFP lifecycle. No automatic kill-switch enable. GUI split policy + core route management is rejected explicitly, without altering the saved switches |
| mtu / path_mtu / pmtud | 0 or 1280–9000, shared setters | Core negotiation/path sizing; PMTUD stays opt-in. Legacy tun_mtu migrates to mtu on save |
| hybrid.enabled / tcp / tcp_max_flows / tcp_idle_timeout_sec | embedded Hybrid config → shared bridge | Client lane enabled/mode/flow budget/idle; Windows effective flow cap 4096. Server connect/global/ACL values remain unedited |
| reorder and reorder_rules | file parser, override-presence flags, cross-field validator, embedded bridge | Exact integer widths and rule precedence preserved. Zero rules is supported all-inner-flow mode, including TCP. GUI ports remain unique 1–65535; reserved low_latency is documented as inert |
| advanced.recv_rate_limit | uint64 ≤10000000000, bridge setter | Receive-window/memory bound, not an exact shaper |
| Linux queue/batch/offload, server-only/unknown keys | preserved JSON, not injected into new Windows profiles | No misleading editable Windows controls; existing values are retained byte-value/type/presence-wise |

New nodes explicitly set `manage_routes=false` because GUI routing owns the
policy. An imported missing key still means the actual CLI default true and
the editor shows that. Empty `paths` is a GUI default-adapter mode, not a
valid direct Windows CLI invocation. These are documented compatibility
distinctions, not changes to core defaults.

Integer input no longer passes through NumberBox/double. A text control with
the existing WPF-UI TextBox theme validates exact decimal digits and the
field's range, including full uint64. Saving edits only changed supported
values; absent defaults, unknown root/nested/rule fields and unsupported
platform fields remain present or absent as imported. Recognized old boolean
strings normalize to JSON booleans. Cancel and failed validation leave the
caller's config unchanged. JSON import uses existing decoding, opens a new
editor, and leaves the selected source file unchanged.

## Actual verification

- GUI, updater and test project compile with zero warnings/errors locally.
- Final configuration suite: **282 assertions**, including exact uint64
  (`2^53+1`, `UINT64_MAX`), all numeric control bounds and invalid input,
  malformed imported types, Unicode/UTF-8 limits, route conflicts, default
  paths, cancellation, unchanged fields, rule validation and README import.
- Complete production behavior suite passed after final UI changes:
  persistence/migration, database reopen, factory reset/backup, production
  routing semantics, serialized operations, actual hidden child process
  CTRL_BREAK/exit handling, update rollback, restore/export and asset
  architecture selection. Its remaining legacy scenario group reports 34
  assertions in addition to the separate production/configuration suites.
- **27 actual editor → save/reload → backup ZIP export → runtime fixtures**
  passed **2482 assertions** against the real `src/config.c` parser and
  shared `client_config_bridge.c` with `mqvpn_config.c`, comparing every
  supported scalar, enums, paths/DNS, endpoint mapping, multipath, redundant
  ports, rule values and explicit override flags. Fixtures include defaults,
  sparse imported config, custom settings, every scheduler, upper bounds,
  two/eight paths, IPv6 DNS and the README JSON. The observer starts no VPN.
- The standalone observer builds from repository sources with clang
  `-Wall -Wextra -Werror`, **Debug + ASan/UBSan**. A local PIE sanitizer
  startup exception produced no useful diagnostic; the final local observer
  uses `-fno-pie/-no-pie` and passes the complete corpus without sanitizer
  findings. This does not weaken checks or change production code.
- The actual packaging script generated the final **x64 ZIP** from this
  working tree and the downloaded, current-baseline native core artifact
  from CI 41011816963. The production final-ZIP verifier passed PE imports,
  architecture/hashes/licenses, self-contained GUI/SQLite, core/Xray startup,
  update-helper application/verification, and the packaged guide/client JSON.
  Its manifest names baseline `ef1bd0e` because this local validation preceded
  the new commit; it is local working-tree evidence, not a claim that the
  final new SHA has already passed native CI.
- actionlint, changed PowerShell script parsing, clang-format 18.1.3 and
  diff whitespace review passed. Screenshots were captured and the new
  integer input's theme was inspected; screenshots are not a visual-regression
  gate. README and GUI examples are included in the final ZIP.

Local evidence: `build/harness/gui-phase-b/` (behavior/configuration logs,
`core-evidence/`, final x64 ZIP, `package-reports/`), `build/config-fixtures/`
and test output `screenshots/`. These generated files are not committed.

## CI and remaining scope

The existing reusable native GUI jobs now build the small parser observer in
Debug and execute the GUI-generated corpus on native x64 and ARM64 before
packaging. A missing observer in GitHub Actions is a test failure. No workflow
trigger, heavy benchmark dispatch or duplicate GUI job is added. Artifacts
include the input/output corpus for failure diagnosis. The final SHA's new
native jobs, package results and other checks remain pending after push;
only one automatic startup snapshot is recorded, with no completion polling.

The earlier native baseline gates close phase A's package runtime gaps for
that baseline (R2/R3); phase B's new SHA still needs those same gates. R4/R9
production persistence/reset regressions pass locally again; R13 now also
checks real GUI/C configuration contracts. R1 remains a static defensive
header-scope review from phase A; no security reproduction is introduced.
Live VPN connectivity, GUI global/split route transitions, WFP effects and
network loss behavior were not exercised on the user's Windows host. Local
ARM64 execution is unavailable; native ARM64 is an explicit existing CI gate.
The PR stays unmerged, and neither main nor XQUIC is pushed in this stage.
