# MQVPN integration revalidation (2026-10-30)

PR #15 was merged as `f008555` before the new head's hosted verification
finished. The user requested an immediate rollback and a fresh PR. Rollback
PR #16 was merged as `cfd9db5`; its tree is exactly
`ca210e611949c88110a221c6ab38dfc08965a79a`, the tree of pre-integration main
`d30f55c`. Both the accidental integration and rollback retain Git history.
XQUIC #9 remains merged; reapplying MQVPN restores the verified XQUIC main
pin `2176a65` and unchanged BoringSSL pin `427ec40`.

## Failure and correction

CI run `41056098090`, Darwin job `124063891751`, failed only
`check_build_version` in its 14-test Darwin CTest suite. After loading the
tag cache, the test's executable still returned the snapshot version.

Apple's published Make 3.81 configuration has `FILE_TIMESTAMP_HI_RES 0`.
The test rapidly changed compile definitions in the same build directory;
second-resolution dependency timestamps can leave the old object executable
in place. This was reproduced locally with Make 3.81 using that timestamp
configuration: the original test failed with the same old snapshot identity
where the numeric tag version was expected. The Linux-hosted reproduction
requires one non-Apple command-parsing compatibility change in the isolated
Make source; it does not modify MQVPN or claim native macOS execution.

Each test identity now compiles with `--clean-first`. Independent assertions
check the actual CMake cache and generated display/package/core metadata
after same-directory snapshot-to-tag reconfiguration. These checks distinguish
cache reset from fresh binary compilation. No sleep, retry, ignored exit
status or removed assertion is used. Failures print configure/build logs,
generator/cache values and platform details before cleanup.

## Verification

- With the isolated Make 3.81, five independent invocations of the corrected
  full version-contract test passed. Every invocation compiled the actual
  library version function for snapshot, tag, default and legacy-suffix
  identities, checked UTC determinism, cache reset, invalid inputs and DEB
  metadata. These invocations investigate the reproduction locally; CI still
  executes the test once and fails normally on any error.
- The existing clang Debug `-Werror` ASan/UBSan build passed all 59 CTest
  entries against the revalidated source and updated test script. No C
  product behavior changed in the timestamp correction.
- GUI test build completed with zero warnings/errors. Production package
  transaction, rollback, configuration preservation, exact asset selection
  and snapshot core/ABI compatibility checks passed.
- Actions structural/expression validation, modified-script ShellCheck with
  dependency tracking, Windows CLI identity-step PowerShell parsing and diff
  whitespace checks passed.

The existing Linux build, Darwin build and Windows x64/ARM64 builds now load
the generated snapshot CMake cache. They execute their real CLI and compare
its identity to the checked-out commit-derived version, and require the core
and callback ABI metadata. The same-run GUI packaging consumes those Windows
snapshot executables, exercising actual updater compatibility rather than
only synthetic version strings. No extra workflow or manual weekly/stress/
performance/release run is introduced.

Fresh hosted results must be recorded for the new PR head. Old #15 successes
do not certify this PR. The new PR stays unmerged, without auto-merge. Native
macOS, ARM64 and final package acceptance depend on that hosted run; local
Linux reproduction is not a substitute for those results.
