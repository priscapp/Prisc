#!/usr/bin/env bash
# Runs every kept C# facade and every wrapper once in the real app (a small real window on the
# UI thread, then again from a thread-pool thread) and writes one JSON result per item to
# probe-out/probe.json. The exit code counts `error` results only (a broken contract: a wrong
# status for a documented case, a panic, a layout mismatch); `unavailable` and `denied` (the OS
# said no, as it does for screen recording on a runner) never fail. Never requests a permission.
#
# Needs a desktop session (it opens a window). CI runs it from the repo root with
# PRISC_INTEROP_REQUIRED=1 and PRISC_DISABLE_TELEMETRY=1.
set -euo pipefail

# Built here too (a no-op after an earlier build), so a failure in an earlier step still
# leaves the probe a library to load instead of a cascade of "not available".
cargo build -p prisc_interop
dotnet build ui/Prisc.Ui/Prisc.Ui.csproj -c Release --nologo
mkdir -p probe-out
# The app takes a native path; the file checks below use the shell's.
if [ "${RUNNER_OS:-}" = Windows ]; then
  probe="$(pwd -W)/probe-out/probe.json"
  exe=ui/Prisc.Ui/bin/Release/net10.0/Prisc.Ui.exe
else
  probe="$PWD/probe-out/probe.json"
  exe=ui/Prisc.Ui/bin/Release/net10.0/Prisc.Ui
fi
status=0
"$exe" --interop-probe "$probe" || status=$?
if [ ! -s probe-out/probe.json ]; then
  echo "::error::the interop probe wrote no results (exit code $status)"
  exit 1
fi
cat probe-out/probe.json
if grep -qE '"status"[[:space:]]*:[[:space:]]*"error"' probe-out/probe.json || [ "$status" -ne 0 ]; then
  echo "::error::the interop probe reported broken contracts (exit code $status); see the error results above"
  exit 1
fi
