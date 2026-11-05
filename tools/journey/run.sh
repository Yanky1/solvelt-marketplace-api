#!/usr/bin/env bash
# Runs the whole buyer journey locally or in CI: stack up, every journey, stack down (logs are kept in .journey/ for a failed run).
# Usage: tools/journey/run.sh [journey ...]    (default: all)   e.g. tools/journey/run.sh product
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PW_CHROMIUM_PATH="${PW_CHROMIUM_PATH:-}"
[ -z "$PW_CHROMIUM_PATH" ] && unset PW_CHROMIUM_PATH
"$HERE/stack.sh" up || exit 1
journeys=("$@"); [ ${#journeys[@]} -eq 0 ] && journeys=(product)
failed=0
for j in "${journeys[@]}"; do
  echo "=========== journey: $j"
  node "$HERE/$j-journey.mjs" || { failed=1; echo "journey $j FAILED"; }
done
[ "${KEEP_STACK:-0}" = "1" ] || "$HERE/stack.sh" down
exit $failed
