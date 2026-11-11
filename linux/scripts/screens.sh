#!/bin/bash
# Renders the popup from the sample data in every variant CI checks (no display needed).
# Usage: linux/scripts/screens.sh "<command that runs AIUsageBar>" <output folder>
set -euo pipefail
exe="$1"; out="$(mkdir -p "$2" && cd "$2" && pwd)"; docs="$(cd "$(dirname "$0")/../../docs" && pwd)"
mkdir -p "$out"
fixtures=(--fixture "$docs/sample-usage.json" --grok-fixture "$docs/sample-grok-usage.json" --codex-fixture "$docs/sample-codex-usage.json" --copilot-fixture "$docs/sample-copilot-usage.json" --cursor-fixture "$docs/sample-cursor-usage.json" --gemini-fixture "$docs/sample-gemini-usage.json")
run() { local name="$1" lang="$2"; shift 2; $exe --snapshot "$out/popup-$name.png" --lang "$lang" "${fixtures[@]}" "$@"; }
run light en
run dark en --dark
run light-pt pt
run copilot-tab en --tab copilot
run codex-tab en --tab codex
run cursor-tab en --tab cursor
run cursor-tab-pt pt --tab cursor
run gemini-tab en --tab gemini
run agents-first-run en --first-run
run update-available en --update-available 9.9.9
run tab-limit en --tab-limit claude=weekly_all
run all-together en --layout list
run no-tray en --no-tray-notice
