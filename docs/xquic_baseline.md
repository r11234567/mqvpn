# xquic Baseline for mqvpn

The v0.19.1-beta1 upstream synchronization pins `third_party/xquic` to
`9b8f27e63cf827077012513780237dcfcb1d148e` from
[r11234567/xquic#11](https://github.com/r11234567/xquic/pull/11).
It merges upstream `1e117b540cd78914ea8216ebc7653bf04fc3f01f` and retains
this fork's proxy, PMTU, and sanitizer fixes. BoringSSL is pinned by xquic
to 0.20261020.0.

The configured fork is `r11234567/xquic`, with `main` as its long-term
tracking branch. Until the dependency PR merges, the pin is published on
`perf/sync-upstream-0.19.1-beta1`. Merge the dependency PR first, using a
merge commit to preserve its history.

After checkout, run `git submodule update --init --recursive --checkout`.
A pin update must reference its published dependency PR or tag, rebuild
both projects, and run the Debug sanitizer and applicable netns tests.
