#!/usr/bin/env bash
# Starts a built copy of the app and checks the basics over HTTP: the page and API respond, the setup dialog has
# something to show, the folder browser works, and a second launch notices the first one.
#
#   tests/smoke.sh <path to the built executable> [port]
#
# Used by the CI workflow on Linux and Windows; it never touches a real osu! folder (auto-detection is switched off).
set -euo pipefail

EXE="${1:?usage: smoke.sh <path to executable> [port]}"
PORT="${2:-5187}"
URL="http://localhost:$PORT"
WORK="$(mktemp -d)"
PID=""

cleanup() {
  [ -n "$PID" ] && kill "$PID" 2>/dev/null || true
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { echo "SMOKE TEST FAILED: $1"; echo "--- app output ---"; cat "$WORK/app.log" 2>/dev/null || true; exit 1; }

ARGS=(--urls "$URL" --no-browser --AutoDetect=false --SettingsFile="$WORK/settings.json")

"$EXE" "${ARGS[@]}" >"$WORK/app.log" 2>&1 &
PID=$!

for _ in $(seq 1 40); do
  curl -fs "$URL/api/setup" >/dev/null 2>&1 && break
  kill -0 "$PID" 2>/dev/null || fail "the app exited during start-up"
  sleep 0.5
done
curl -fs "$URL/api/setup" >/dev/null || fail "the app never started answering"

curl -fs "$URL/" | grep -q "osu! Collection Manager"     || fail "home page missing (is wwwroot next to the executable?)"
curl -fs "$URL/js/main.js" >/dev/null                     || fail "static JavaScript not served"
curl -fs "$URL/api/setup" | grep -q '"configured":false'  || fail "expected no osu! folder to be configured"
curl -fs "$URL/api/status" | grep -q '"configured":false' || fail "status should report not configured"

# Nothing that needs osu! may work before a folder is chosen: it must ask for one (409 + setupRequired), not crash.
code=$(curl -s -o "$WORK/library.json" -w '%{http_code}' "$URL/api/library")
[ "$code" = "409" ] && grep -q setupRequired "$WORK/library.json" || fail "library should answer 409 setupRequired, got $code"

# The in-page folder browser: start page (drives or places), then a real folder.
curl -fs "$URL/api/setup/folders" | grep -q '"folders":\[{' || fail "folder browser start page is empty"
# (cygpath turns Git Bash's /tmp/... into a real Windows path when this runs on Windows; on Linux it doesn't exist.)
LISTABLE="$(cygpath -m "$WORK" 2>/dev/null || echo "$WORK")"
curl -fsG --data-urlencode "path=$LISTABLE" "$URL/api/setup/folders" | grep -q '"folders"' || fail "folder browser cannot list a folder"

# Writes from another website must be refused.
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -H 'Origin: http://evil.example' -d '{}' "$URL/api/setup")
[ "$code" = "403" ] || fail "cross-site write should be 403, got $code"

# A second copy must notice the first one instead of fighting over the port.
second="$("$EXE" "${ARGS[@]}" 2>&1 </dev/null || true)"
echo "$second" | grep -qi "already running" || fail "second launch did not report the running copy: $second"

echo "smoke test passed: $EXE"
