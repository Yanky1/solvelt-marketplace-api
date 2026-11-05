#!/usr/bin/env bash
# Brings the buyer-journey stack up (or down): the REAL seller-portal backend, the marketplace API on PostgreSQL, the web server and a mail sandbox.
# Usage:  tools/journey/stack.sh up | down | status | logs <name>
# Needs: .NET 10 SDK, Node 22, a reachable PostgreSQL (JOURNEY_PG_ADMIN, default postgres://postgres:postgres@localhost:5432/postgres; it creates the database "journey_marketplace" fresh on every `up`).
# Checkouts (set when they are not next to this repo): PORTAL_DIR (Yanky1/Yanky1-test-login-backend, read-only: built and run, never changed), WEB_DIR (Yanky1/solvelt-marketplace-web).
# A missing or unbuildable checkout, or a service that does not become healthy, FAILS with a message: there is no mock-portal fallback (brief M8).
set -euo pipefail

# The real Stripe TEST key (opt-in job) is for the API process ONLY: take it out of the environment before anything else starts, so the portal's MSBuild and publish, npm, the web server and the
# mail sink never see it (review LOW-1). The API subshell below exports it as Stripe__SecretKey; env.json never holds it.
REAL_STRIPE_KEY="${STRIPE_TEST_SECRET_KEY:-}"
unset STRIPE_TEST_SECRET_KEY

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_DIR="$(cd "$HERE/../.." && pwd)"
PORTAL_DIR="${PORTAL_DIR:-$(cd "$API_DIR/.." && pwd)/yanky1/yanky1-test-login-backend}"
WEB_DIR="${WEB_DIR:-$(cd "$API_DIR/.." && pwd)/solvelt-marketplace-web}"
RUN_DIR="${JOURNEY_RUN_DIR:-$API_DIR/.journey}"
PG_HOST="${JOURNEY_PG_HOST:-localhost}"; PG_PORT="${JOURNEY_PG_PORT:-5432}"; PG_USER="${JOURNEY_PG_USER:-postgres}"; PG_PASSWORD="${JOURNEY_PG_PASSWORD:-postgres}"
DB_NAME="journey_marketplace"
PORTAL_PORT="${PORTAL_PORT:-5001}"; API_PORT="${API_PORT:-5080}"; WEB_PORT="${WEB_PORT:-3000}"; SMTP_PORT="${SMTP_PORT:-2525}"; MAIL_HTTP_PORT="${MAIL_HTTP_PORT:-8025}"

# Shared dev secrets: obviously test values, different per purpose (the API refuses equal keys; none of them is a real secret).
INBOUND="journey-portal-inbound-secret-0123456789abcdef"      # marketplace -> portal  (Portal__InboundSecret = Orders__InboundSecret)
OUTBOUND="journey-portal-outbound-secret-fedcba9876543210"    # portal -> marketplace  (Portal__WebhookSecret = Orders__OutboundSecret)
FWD_KEY="journey-web-forwarding-key-aaaaaaaaaaaaaaaaaaaa"
SESSION_SECRET="Journey#Session9Secret!kdj39fK2mZq7Xw2Rt8YbN4"
STRIPE_WEBHOOK="whsec_journey_test_only_00000000000000000000"
ADMIN_EMAIL="admin@example.com"; ADMIN_PASSWORD="Qu1et-Marble-Fox-Test!"

die() { echo "JOURNEY STACK FAILED: $*" >&2; exit 1; }
wait_http() { # name url [seconds]
  local name="$1" url="$2" n="${3:-90}"
  for _ in $(seq 1 "$n"); do curl -fsS -o /dev/null -m 3 "$url" 2>/dev/null && { echo "up: $name"; return 0; }; sleep 1; done
  echo "--- last log lines of $name ---" >&2; tail -n 40 "$RUN_DIR/$name.log" >&2 || true
  die "$name did not become healthy at $url within ${n}s"
}
start() { # name command...   (cwd and env are the caller's)
  local name="$1"; shift
  ( "$@" >"$RUN_DIR/$name.log" 2>&1 & echo $! >"$RUN_DIR/$name.pid" )
}

cmd_down() {
  for p in "$RUN_DIR"/*.pid; do [ -e "$p" ] || continue; kill "$(cat "$p")" 2>/dev/null || true; rm -f "$p"; done
  pkill -f "$RUN_DIR/" 2>/dev/null || true
  echo "stack stopped"
}

cmd_up() {
  [ -d "$PORTAL_DIR/src/Solvelt.Api" ] || die "the seller-portal backend is not checked out at $PORTAL_DIR (set PORTAL_DIR). The journey runs against the REAL portal; there is no mock fallback."
  [ -d "$WEB_DIR/app" ] || die "the web repo is not checked out at $WEB_DIR (set WEB_DIR)."
  command -v dotnet >/dev/null || die "dotnet is not installed (.NET 10 SDK)."
  command -v psql >/dev/null || die "psql (the PostgreSQL client) is not installed."
  command -v node >/dev/null || die "node is not installed (Node 22)."
  cmd_down >/dev/null 2>&1 || true
  if [ "${SKIP_BUILD:-0}" = "1" ]; then rm -rf "$RUN_DIR/portal-data"; find "$RUN_DIR" -maxdepth 1 -name "*.log" -delete; mkdir -p "$RUN_DIR/portal-data/uploads"
  else rm -rf "$RUN_DIR"; mkdir -p "$RUN_DIR/portal-data/uploads"; fi

  echo "== database"
  export PGPASSWORD="$PG_PASSWORD"
  psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_USER" -d postgres -v ON_ERROR_STOP=1 -q -c "DROP DATABASE IF EXISTS $DB_NAME WITH (FORCE)" -c "CREATE DATABASE $DB_NAME" \
    || die "cannot reach PostgreSQL at $PG_HOST:$PG_PORT as $PG_USER"

  if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "== build (portal, API, web)"
  dotnet publish "$PORTAL_DIR/src/Solvelt.Api" -c Release -o "$RUN_DIR/portal" --nologo -v q >"$RUN_DIR/build-portal.log" 2>&1 || { tail -30 "$RUN_DIR/build-portal.log" >&2; die "the portal backend did not build"; }
  dotnet publish "$API_DIR/src/Solvelt.Marketplace.Api" -c Release -o "$RUN_DIR/api" --nologo -v q >"$RUN_DIR/build-api.log" 2>&1 || { tail -30 "$RUN_DIR/build-api.log" >&2; die "the marketplace API did not build"; }
  ( cd "$WEB_DIR" && { [ -d node_modules ] || npm ci --silent; } && npm run build --silent ) >"$RUN_DIR/build-web.log" 2>&1 || { tail -30 "$RUN_DIR/build-web.log" >&2; die "the web app did not build"; }
  fi

  echo "== mail sandbox"
  SMTP_PORT="$SMTP_PORT" HTTP_PORT="$MAIL_HTTP_PORT" start mail node "$HERE/mail-sink.mjs"
  wait_http mail "http://127.0.0.1:$MAIL_HTTP_PORT/" 15

  echo "== seller portal (Testing, SQLite)"
  (
    cd "$RUN_DIR/portal"
    export ASPNETCORE_ENVIRONMENT=Testing ASPNETCORE_URLS="http://127.0.0.1:$PORTAL_PORT"
    export Database__Provider=Sqlite Database__ConnectionString="Data Source=$RUN_DIR/portal-data/portal.db"
    export App__HmacSecret="journey-hmac-secret-0123456789abcdef" App__UseHttpsRedirection=false App__CorsOrigins__0="http://localhost:5173" App__FrontendBaseUrl="http://localhost:5173"
    export Email__Provider=Sink Email__SendInBackground=false Turnstile__UseTestStub=true Turnstile__SecretKey="1x0000000000000000000000000000000AA" Turnstile__SiteKey="1x00000000000000000000AA"
    export AdminSeed__Email="$ADMIN_EMAIL" AdminSeed__Password="$ADMIN_PASSWORD" Storage__LocalRoot="$RUN_DIR/portal-data/uploads"
    export RateLimit__AuthPerMinute=100000 RateLimit__SensitivePerMinute=100000 RateLimit__TeamPerMinute=100000 RateLimit__CatalogPerMinute=100000 RateLimit__OrdersPerMinute=100000 RateLimit__InternalPerMinute=100000 RateLimit__InternalCatalogPerMinute=100000 RateLimit__InternalUnsignedPerMinute=100000
    export Passkeys__RelyingPartyId=localhost Passkeys__AllowedOrigins__0="http://localhost:5173"
    export Orders__InboundSecret="$INBOUND" Orders__OutboundSecret="$OUTBOUND" Orders__WebhookUrl="http://127.0.0.1:$API_PORT/api/portal/webhooks" Orders__OutboxBackoffSeconds=2
    export Demo__Enabled=false
    start portal dotnet "$RUN_DIR/portal/Solvelt.Api.dll"
  )
  wait_http portal "http://127.0.0.1:$PORTAL_PORT/health" 90

  echo "== marketplace API (Development, PostgreSQL)"
  (
    cd "$RUN_DIR/api"
    export ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:$API_PORT"
    export Database__Provider=Postgres ConnectionStrings__Default="Host=$PG_HOST;Port=$PG_PORT;Username=$PG_USER;Password=$PG_PASSWORD;Database=$DB_NAME"
    export Portal__BaseUrl="http://127.0.0.1:$PORTAL_PORT" Portal__InboundSecret="$INBOUND" Portal__WebhookSecret="$OUTBOUND"
    export Web__ForwardingKey="$FWD_KEY" Stripe__WebhookSecret="$STRIPE_WEBHOOK"
    # no secret key = the in-process fake payment provider (CI); the web needs a publishable key to mount the (stand-in) Payment Element. A fake test-mode value, not a secret
    export Stripe__PublishableKey="${STRIPE_PUBLISHABLE_KEY:-pk_test_journeyfakekey0000000000}"
    export Marketplace__PublicBaseUrl="http://localhost:$WEB_PORT" Marketplace__RelayDomain="relay.journey.test"
    export Email__Provider=Smtp Email__SmtpHost=127.0.0.1 Email__SmtpPort="$SMTP_PORT" Email__SmtpUseTls=false Email__From="noreply@journey.test"
    export Demo__Enabled=true Catalog__Source=Http
    if [ "${JOURNEY_REAL_STRIPE:-0}" = "1" ]; then
      # the opt-in job: a REAL Stripe test-mode key from the environment (never a file, never printed). The API refuses live keys (sk_live_/rk_live_) at start-up in every environment.
      [ -n "$REAL_STRIPE_KEY" ] || die "JOURNEY_REAL_STRIPE=1 needs STRIPE_TEST_SECRET_KEY (a sk_test_ key)"
      case "$REAL_STRIPE_KEY" in sk_test_*|rk_test_*) ;; *) die "STRIPE_TEST_SECRET_KEY must be a Stripe TEST key (sk_test_ or rk_test_)";; esac
      export Stripe__SecretKey="$REAL_STRIPE_KEY"
    else
      export Stripe__FakeCaptureOnWebhook=true
    fi
    # many sign-ins and requests from one address in a few minutes: the limits are not under test here
    export RateLimits__AuthPerMinute=100000 RateLimits__PublicPerMinute=100000
    export Outbox__BackoffSeconds=2 Catalog__SyncIntervalSeconds=5
    start api dotnet "$RUN_DIR/api/Solvelt.Marketplace.Api.dll"
  )
  wait_http api "http://127.0.0.1:$API_PORT/health" 90

  echo "== web"
  (
    cd "$WEB_DIR"
    # a production BUILD that says it is the test site (like Ronald's): APP_ENV=staging lets DEMO_ENABLED through, the stand-in Stripe.js is accepted only from localhost
    export NODE_ENV=production APP_ENV=staging PORT="$WEB_PORT" API_BASE_URL="http://127.0.0.1:$API_PORT" PUBLIC_ORIGIN="http://localhost:$WEB_PORT" DEMO_ENABLED=true TRUSTED_PROXIES="127.0.0.1,::1"
    export STRIPE_JS_URL="http://localhost:$MAIL_HTTP_PORT/__stripe/v3.js" ALLOW_STRIPE_STUB=1
    export SESSION_COOKIE_SECRET="$SESSION_SECRET" WEB_FORWARDING_KEY="$FWD_KEY"
    start web node server.js
  )
  wait_http web "http://localhost:$WEB_PORT/" 60

  cat >"$RUN_DIR/env.json" <<JSON
{ "portal": "http://127.0.0.1:$PORTAL_PORT", "api": "http://127.0.0.1:$API_PORT", "web": "http://localhost:$WEB_PORT", "mail": "http://127.0.0.1:$MAIL_HTTP_PORT",
  "webDir": "$WEB_DIR", "pg": { "host": "$PG_HOST", "port": $PG_PORT, "user": "$PG_USER", "password": "$PG_PASSWORD", "db": "$DB_NAME" }, "inboundSecret": "$INBOUND", "outboundSecret": "$OUTBOUND", "stripeWebhookSecret": "$STRIPE_WEBHOOK", "adminEmail": "$ADMIN_EMAIL", "adminPassword": "$ADMIN_PASSWORD", "runDir": "$RUN_DIR" }
JSON
  echo "stack is up: $RUN_DIR/env.json"
}

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  status) for n in mail portal api web; do [ -e "$RUN_DIR/$n.pid" ] && kill -0 "$(cat "$RUN_DIR/$n.pid")" 2>/dev/null && echo "$n running" || echo "$n stopped"; done ;;
  child-env) env | grep -c "STRIPE_TEST_SECRET_KEY\|sk_test_" || true ;;   # what every child process of this script sees of the key (the check in tools/journey/test-key-scope.sh)
  logs) tail -n 80 "$RUN_DIR/${2:?name}.log" ;;
  *) echo "usage: $0 up|down|status|logs <name>"; exit 2 ;;
esac
