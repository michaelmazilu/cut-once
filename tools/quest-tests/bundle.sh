#!/usr/bin/env bash
# Refreshes the copies of repo files that the headset app carries in a Resources folder (a build cannot read
# outside Assets). BundledFilesTests fails when a copy is stale.
set -euo pipefail
cd "$(dirname "$0")/../.."
DEST=apps/quest/Assets/CutOnce/AR/Resources/CutOnce
cp data/fixtures/hologram-palette.json "$DEST/hologram-palette.json"
echo "bundled: hologram-palette.json (the app ships no plans: it starts blank)"
