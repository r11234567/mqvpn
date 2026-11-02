#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) 2026 mp0rta and mqvpn contributors
# Same VM, same binary, randomized arm order. Do not infer VM identity from dispatch.
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
mode="${1:?mode required}"; shift
FAILED=0
if [ "$mode" = mtu ]; then
    mapfile -t PLAN < <(python3 - <<'PY'
import os, random
rows = [(p,d) for p in ('off','on') for d in ('DL','UL')]
random.Random(os.environ.get('GITHUB_RUN_ID', 'local')).shuffle(rows)
for p,d in rows: print(p,d)
PY
    )
    for entry in "${PLAN[@]}"; do
        read -r pmtud direction <<<"$entry"
        CI_BENCH_PMTUD="$pmtud" CI_BENCH_DIRECTION="$direction" \
            CI_BENCH_PAIR_ID="${GITHUB_RUN_ID:-local}-$$" \
            CI_BENCH_ARM="${CI_BENCH_ARM:-default}-pmtud_${pmtud}" \
            bash "$SCRIPT_DIR/ci_bench_scenarios.sh" mtu "$@" || FAILED=1
    done
elif [ "$mode" = game ]; then
    for profile in $(python3 -c 'import random; a=["default","tuned"]; random.shuffle(a); print(" ".join(a))'); do
        CI_BENCH_GAME_PROFILE="$profile" CI_BENCH_PAIR_ID="${GITHUB_RUN_ID:-local}-$$" \
            CI_BENCH_ARM="${CI_BENCH_ARM:-default}-game_${profile}" \
            bash "$SCRIPT_DIR/ci_bench_scenarios.sh" game "$@" || FAILED=1
    done
else
    bash "$SCRIPT_DIR/ci_bench_scenarios.sh" "$mode" "$@" || FAILED=1
fi
exit "$FAILED"
