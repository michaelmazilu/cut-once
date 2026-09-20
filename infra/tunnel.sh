#!/usr/bin/env bash
# Bash scripts must use LF endings; .gitattributes enforces this on every platform.
# Puts the server that is already running on this laptop (pnpm serve:local) on a public HTTPS address.
#   pnpm tunnel                               random https://<words>.trycloudflare.com, new on every run, no account
#   pnpm tunnel <tunnel-name> <https://host>  a named tunnel on your own domain (one-time setup in infra/README.md)
# Restarting the server does not change the address; only restarting this script does.
set -euo pipefail
PORT="${PORT:-8080}"
LOCAL="http://127.0.0.1:$PORT"

command -v cloudflared >/dev/null || { echo "cloudflared is missing: brew install cloudflared" >&2; exit 1; }
curl -fsS "$LOCAL/health" >/dev/null 2>&1 || { echo "Nothing answers on $LOCAL/health. Start the server first: pnpm serve:local" >&2; exit 1; }

LOG="$(mktemp -t cutonce-tunnel.XXXXXX)"
CF_PID=""
cleanup() { [ -n "$CF_PID" ] && kill "$CF_PID" 2>/dev/null; rm -f "$LOG"; }
trap cleanup EXIT
trap 'exit 130' INT TERM

if [ $# -ge 2 ]; then
  cloudflared tunnel --no-autoupdate run --url "$LOCAL" "$1" >"$LOG" 2>&1 &
  CF_PID=$!
  URL="${2%/}"
else
  cloudflared tunnel --no-autoupdate --url "$LOCAL" >"$LOG" 2>&1 &
  CF_PID=$!
  URL=""
fi

# Wait until the tunnel has a connection (and, for a quick tunnel, its address). Asking DNS for the address
# before that can cache a "not found" on this laptop.
for _ in $(seq 1 60); do
  kill -0 "$CF_PID" 2>/dev/null || { echo "cloudflared stopped:" >&2; tail -20 "$LOG" >&2; exit 1; }
  [ -z "$URL" ] && URL="$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' "$LOG" | grep -v '//api\.' | head -1 || true)"
  [ -n "$URL" ] && grep -q "Registered tunnel connection" "$LOG" && break
  sleep 1
done
[ -n "$URL" ] || { echo "No tunnel address after 60 s:" >&2; tail -20 "$LOG" >&2; exit 1; }

ok=""
for _ in $(seq 1 30); do
  curl -fsS "$URL/health" >/dev/null 2>&1 && { ok=1; break; }
  sleep 2
done

echo
echo "  Cut Once is online at  $URL"
echo "  Headset / Quest browser  $URL/health"
echo "  Director page            $URL/director"
echo "  Token                    API_TOKEN in .env.local"
[ -n "$ok" ] || echo "  (warning: $URL/health did not answer from this laptop yet; try it on the Quest)"
echo
echo "  Keep this window open. This laptop stays awake while it runs; keep it plugged in with the lid open."
echo "  Ctrl-C stops the tunnel. The address above only changes if you restart this script."
echo

# Keep the Mac awake while the tunnel runs (no-op elsewhere).
command -v caffeinate >/dev/null && caffeinate -ims -w $$ &

while kill -0 "$CF_PID" 2>/dev/null; do sleep 2; done
echo "cloudflared stopped:" >&2
tail -20 "$LOG" >&2
exit 1
