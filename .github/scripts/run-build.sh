#!/usr/bin/env bash
#
# Runs a build target and leaves behind a log the guard steps can actually read.
#
# Usage: run-build.sh <log-path> <Target> [build arguments...]
#
# ── ⚠ WHY THIS EXISTS RATHER THAN `./build.sh X 2>&1 | tee "$RUNNER_TEMP/x.log"` ─────────────────
#
# Nuke logs through Serilog's ANSI console theme, and on GitHub Actions it emits colour even though
# stdout is a pipe — the Actions log viewer renders ANSI, so this is a feature there and it is not
# going away. The theme colours every structured-log PROPERTY separately from the message around
# it, so
#
#     Log.Information("Charts: {Count} chart(s), {Total} annotated value(s)", ...)
#
# arrives in the tee'd file as
#
#     Charts: ESC[0m ESC[38;5;45;1m 15 ESC[0m ESC[39m chart(s), …
#
# and `grep -E 'Charts: [0-9]+ chart'` cannot match it. Any guard pattern that spans a property
# boundary is matching bytes that are not in the file. Patterns made only of literal message text —
# `inspected 0 chart`, `no provider is registered` — keep working, which is exactly why this went
# unnoticed: the checks that FAIL a job all matched, and the checks that merely PRINT the evidence
# all did not.
#
# `defaults.run.shell: bash` is what turned that into a red tick. It adds `-e -o pipefail`, and the
# evidence-printing grep is the last command in its step, so "this grep found nothing" became "this
# step exited 1". gate/architecture, gate/charts and gate/generate failed on every run of main.yml
# for that reason and no other — each of them after the target it guards had done its work,
# reported it, and passed.
#
# ⚠ The fix belongs here rather than in the patterns. Loosening ten greps to tolerate escape
# sequences is ten chances to loosen one into matching nothing, on gates whose whole purpose is to
# notice that a target inspected nothing. The log is written stripped; the console keeps its colour.
# Both readers then get the form they are good at.

set -uo pipefail

log="${1:?usage: run-build.sh <log-path> <Target> [build arguments...]}"
shift

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." > /dev/null 2>&1 && pwd)"

# ⚠ The coloured original is kept beside the stripped one. When a guard reports that a line is
# missing, the first question is whether the build printed it at all, and that is a question about
# the bytes the build actually emitted.
raw="${log%.log}.ansi.log"

"$ROOT/build.sh" "$@" 2>&1 | tee "$raw"
status=${PIPESTATUS[0]}

# All of Serilog's theme output is SGR (`ESC[…m`), but the pattern covers the whole CSI family: a
# cursor move landing in the log would break a grep just as thoroughly and far less obviously.
esc=$(printf '\033')
sed -E "s/${esc}\[[0-9;?]*[A-Za-z]//g" "$raw" > "$log"

exit "$status"
