#!/usr/bin/env bash
# A check that the real Stripe test key never reaches the child processes of stack.sh (portal build, npm, web, mail sink): only the API subshell exports it. Fails when it leaks.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
leaked="$(STRIPE_TEST_SECRET_KEY=sk_test_scope_check_000000000000 "$HERE/stack.sh" child-env)"
if [ "${leaked:-0}" != "0" ]; then echo "KEY SCOPE FAILED: stack.sh children can see the Stripe test key ($leaked variables)"; exit 1; fi
echo "key scope ok: stack.sh children do not see STRIPE_TEST_SECRET_KEY"
