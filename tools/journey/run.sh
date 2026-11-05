#!/usr/bin/env bash
# Runs the whole buyer journey locally or in CI: stack up, every journey, stack down (logs are kept in .journey/ for a failed run).
# Usage: tools/journey/run.sh [journey ...]    (default: product booking refund staff)   e.g. tools/journey/run.sh product ; tools/journey/run.sh screenshots (writes into the web checkout)
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PW_CHROMIUM_PATH="${PW_CHROMIUM_PATH:-}"
[ -z "$PW_CHROMIUM_PATH" ] && unset PW_CHROMIUM_PATH
"$HERE/stack.sh" up || exit 1
journeys=("$@"); [ ${#journeys[@]} -eq 0 ] && journeys=(product booking refund staff)
failed=0
for j in "${journeys[@]}"; do
  echo "=========== journey: $j"
  if [ "$j" = "screenshots" ]; then script="$HERE/screenshots.mjs"; else script="$HERE/$j-journey.mjs"; fi
  node "$script" || { failed=1; echo "journey $j FAILED"; }
done
[ "${KEEP_STACK:-0}" = "1" ] || "$HERE/stack.sh" down
exit $failed
