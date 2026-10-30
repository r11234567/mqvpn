# Log conventions

What the levels mean, how a path is identified across lines, and why printed
strings are ASCII. Three rules, each of which was learned from a log that was
hard to read.

## Levels, and the xquic mapping

mqvpn has four levels — `debug`, `info`, `warn`, `error` — set with
`--log-level` or `[General] LogLevel`. xquic has seven, and the two are bridged
twice, in opposite directions, by maps that are deliberately not each other's
inverse:

- **Forward** (`map_log_level_to_xquic`) sets the *engine threshold*. It shifts
  mqvpn `info` to `XQC_LOG_WARN`, because xquic at `INFO` emits several lines
  per packet and would bury everything else.
- **Reverse** (`cb_xqc_log_write`) picks the *display severity* for a line
  xquic has already decided to emit. It keeps the incoming severity honest: a
  real xquic warning shows up as a warning.

The trap in the reverse map is `XQC_LOG_REPORT`. Its enum value is 0, which
reads like "most severe", and it was mapped to `error` on that basis. It is not
a severity at all — xquic groups it with `XQC_LOG_STATS` as a statistics
channel and routes both to a separate `xqc_log_write_stat` callback:

```c
/* XQC_LOG_STATS & XQC_LOG_REPORT are levels for statistic */
if ((level == XQC_LOG_STATS || level == XQC_LOG_REPORT)
    && log->log_callbacks->xqc_log_write_stat)
```

`REPORT` is what carries the end-of-request and end-of-connection summaries:
the `|stream_id:…|close_msg:…|err:…|rcvd_bdy_sz:…|path_info:…|` line that
*every* completed request emits, successful or not. Logged as `error`, ordinary
teardown became indistinguishable from failure. In hybrid mode this is not a
minor cosmetic issue: the TCP lane opens an H3 stream per client connection, so
a browser doing nothing unusual produced a continuous run of `error` lines
carrying `err:268` (`H3_REQUEST_CANCELLED`) and `-626` (`XQC_ESTREAM_RESET`)
while both paths were healthy and validating normally. `REPORT` now sits with
`STATS` at `info`.

The corollary for anyone reading a log: **`err:` in an xquic request-summary
line is a close reason, not a fault.** `268`/`H3_REQUEST_CANCELLED` on a
proxied flow usually means the application closed its connection.

## Identifying a path

Every path log line identifies its path by **handle** — the `int64_t` that
`mqvpn_client_add_path_fd` returned and that the public `path_event` callback
carries. The platform layers also keep a private slot index into their own
arrays; that index is never logged, because printing both made one event read
as two:

```
netmon: interface WLAN carrier lost, closing path 1     <- slot index
platform path dropped: handle=2 iface=WLAN reason=2     <- handle
```

Both lines describe the same path. They now both say `handle=2`.

`path <handle> -> <status>` reports the **public** five-state status
(`pending`, `active`, `degraded`, `standby`, `closed`). That projection is
lossy: three internal lifecycle states collapse onto `closed` and three onto
`pending`. The public `path_event` callback still fires on every internal
transition — observers use the repeat as a cue to re-read
`mqvpn_client_get_paths` — but the `info` line is emitted only when the public
status actually changes, so a single close no longer prints as two identical
lines seconds apart.

When the internal step is what you need, raise the level to `debug` and read
`path_log_state_change`'s line instead, which names both lifecycle states and
the reason:

```
path[handle=2 name=WLAN] CLOSED_DROPPED -> CLOSED_FREE reason=… retries=0 fd=-1 xqc_path_id=1
```

## Printed strings are ASCII

Non-ASCII characters are not allowed inside C string literals, and
`scripts/lint/check_log_string_ascii.py` fails CI if one appears. Write `->`
and `--`, not `→` and `—`. Comments are exempt.

A UTF-8 literal is written to the console as its raw bytes, which a non-UTF-8
console decodes with its own codepage; on a Simplified Chinese Windows install
that is CP936, and a single log line could show a correctly rendered adapter
name beside a corrupted arrow, because the adapter name arrived from Win32
already in the console's codepage. `docs/windows_build.md` has the byte-level
walkthrough and explains why `SetConsoleOutputCP(CP_UTF8)` is not a sufficient
answer.

## Reading the log on Android

A phone has no console, and several failures are named nowhere else — a
certificate rejection says *why* only in this log. So the SDK keeps its own copy:
`MqvpnLogBuffer` (`android/sdk-core/.../MqvpnLogBuffer.kt`) is a bounded ring
that `MqvpnVpnService.onNativeLog` fills, and the sample app renders it under
the Logs action on the dashboard, with a level filter, a text filter, and a copy
button.

Two details are deliberate:

- **It is a process-wide singleton, not state on `MqvpnManager`.** The lines
  that matter most are emitted while `startTunnel` is still running, which can
  be before the Activity's `bindService` has completed. A sink that existed only
  once something was listening would miss exactly the failure being diagnosed.
- **The viewer polls it; the ring does not push.** At `debug` the engine emits
  far faster than a screen can render, so a fixed-interval read costs the same
  whatever the log rate. Lines evicted by the ring are counted and shown, so a
  truncated log is never mistaken for a whole one.

The level is per-connection (`[General] LogLevel` / `MqvpnConfig.logLevel`, in
the app under Settings > Logging) and applies from the next connect. Remember the
forward map above: mqvpn `info` already asks xquic for `WARN`, so raising this to
`debug` is what makes xquic's own lines appear.
