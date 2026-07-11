#!/usr/bin/env bash
# Self-play measure primitive for the M4 improvement loop.
# Runs SelfPlayTests headlessly, prints the METRIC lines, exits non-zero on Unity failure.
# Usage: Tools/selfplay.sh   (run from project root)
set -u
UNITY="C:/Program Files/Unity/Hub/Editor/6000.5.3f1/Editor/Unity.exe"
# Derived from this script's own location rather than hardcoded, so it works on any machine the
# repo is cloned to (was hardcoded to one machine's path, breaking it everywhere else).
PROJ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG="$PROJ/Tools/selfplay.log"
RESULTS="$PROJ/Tools/selfplay-results.xml"

"$UNITY" -batchmode -nographics -projectPath "$PROJ" \
  -runTests -testPlatform PlayMode -testFilter SelfPlayTests \
  -testResults "$RESULTS" -logFile "$LOG"
CODE=$?

echo "=== Unity exit: $CODE ==="
grep -E "METRIC selfplay_(batch|match)" "$LOG" || echo "(no METRIC lines — check $LOG)"
grep -iE "error CS|Compilation failed" "$LOG" | head && true
exit $CODE
