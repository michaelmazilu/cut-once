#!/usr/bin/env bash
# Run as the macOS user who has activated Unity in Hub. Never use sudo.
set -euo pipefail
set +x

fail() { echo "$*" >&2; exit 1; }
[[ "$(uname -s)" == Darwin ]] || fail "Run this on your licensed Mac."
[[ "$EUID" != 0 ]] || fail "Run as your normal macOS user, without sudo."
command -v node >/dev/null || fail "Install Node.js 22 or newer first (brew install node@22)."
node -e 'process.exit(Number(process.versions.node.split(".")[0]) >= 22 ? 0 : 1)' || fail "Node.js 22 or newer is required."
git lfs version >/dev/null 2>&1 || fail "Install Git LFS first: brew install git-lfs"

repo_root=$(cd "$(dirname "$0")/../.." && pwd)
version=$(sed -n 's/^m_EditorVersion: //p' "$repo_root/apps/quest/ProjectSettings/ProjectVersion.txt" | tr -d '\r')
editor="/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/MacOS/Unity"
[[ -x "$editor" ]] || fail "Install Unity $version with Android Build Support in Unity Hub, then activate Unity as this user."

case "$(uname -m)" in
  arm64) runner_arch=arm64 ;;
  x86_64) runner_arch=x64 ;;
  *) fail "Unsupported Mac architecture." ;;
esac

# A fresh, dedicated directory: CI never checks out over your working project.
runner_dir="$HOME/actions-runner-cutonce"
if [[ -f "$runner_dir/.runner" ]]; then
  echo "Runner already registered at $runner_dir. Starting it in this terminal."
  cd "$runner_dir"
  exec caffeinate -i ./run.sh
fi
[[ ! -e "$runner_dir" ]] || fail "$runner_dir already exists without a registered runner. Inspect it before retrying; it has been preserved."

echo "This registers a runner for michaelmazilu/kitbash and starts it in this terminal."
echo "Keep the Mac awake, logged in, and connected while builds run. Ctrl-C stops the runner."
echo "Use only trusted workflow branches: jobs execute as your Mac user."
echo "Get a registration token from a repository admin at:"
echo "https://github.com/michaelmazilu/kitbash/settings/actions/runners/new"
echo "Use the token from the config command (valid for one hour). Do not paste it into chat."
read -r -s -p "Runner registration token: " registration_token
echo
[[ -n "$registration_token" ]] || fail "No registration token supplied."

# Download the official release and verify its published SHA-256 before execution.
release=$(curl --fail --silent --show-error --location https://api.github.com/repos/actions/runner/releases/latest)
asset=$(printf '%s' "$release" | node -e '
let s = "";
process.stdin.on("data", d => s += d);
process.stdin.on("end", () => {
  const r = JSON.parse(s);
  const a = r.assets.find(a => a.name === `actions-runner-osx-${process.argv[1]}-${r.tag_name.slice(1)}.tar.gz`);
  if (!a || !/^sha256:[a-f0-9]{64}$/.test(a.digest || "") || !a.browser_download_url.startsWith("https://github.com/actions/runner/releases/download/")) process.exit(1);
  console.log(`${a.browser_download_url} ${a.digest.slice(7)}`);
});' "$runner_arch")
read -r download_url expected_sha <<< "$asset"
mkdir "$runner_dir"
cd "$runner_dir"
curl --fail --show-error --location "$download_url" --output runner.tar.gz
printf '%s  runner.tar.gz\n' "$expected_sha" | shasum -a 256 -c -
tar xzf runner.tar.gz
./config.sh --unattended \
  --url https://github.com/michaelmazilu/kitbash \
  --token "$registration_token" \
  --name "cutonce-mac-$(hostname -s)" \
  --labels cutonce-unity \
  --work _work
unset registration_token
echo "Runner configured. Waiting for Quest Mac build jobs; leave this terminal open."
exec caffeinate -i ./run.sh
