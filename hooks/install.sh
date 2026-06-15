#!/usr/bin/env bash
# Activate the committed hooks for this repo (version-controlled, unlike .git/hooks).
set -e
root=$(git -C "$(dirname "$0")/.." rev-parse --show-toplevel)
chmod +x "$root/hooks/"* 2>/dev/null || true
git -C "$root" config core.hooksPath "$root/hooks"
echo "installed: core.hooksPath=$root/hooks"
