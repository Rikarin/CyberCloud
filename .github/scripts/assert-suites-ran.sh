#!/usr/bin/env bash
#
# Proves `./build.sh Test` ran something.
#
# Usage: assert-suites-ran.sh <results-directory> <build-log>
#
# ── ⚠ THE EXACT GREEN-BECAUSE-IT-RAN-NOTHING THIS GUARDS ─────────────────────────────────────────
#
# Build.Test.cs § RunTests returns success when discovery finds no per-PR project, and the comment
# there argues the case at length: a gate that is red from commit one is a gate everyone learns to
# ignore. That reasoning is right for the build and wrong for CI. docs/plan/23 § CI shape gates
# every PR on `Test`, this repository has 73 per-PR suites today, and the day discovery returns
# zero of them is the day a green tick means nothing.
#
# ⚠ That number read 26 until issue #81, and it is prose rather than a threshold — nothing below
# compares against it; the three guards test for zero. It is still worth being right, because 26 was
# the number #77 needed when it was reasoning about what this gate costs. Recounted 2026-09-05 by
# applying Build.Test.cs § SuiteOwning's own rules to the project files under its SourceRoots
# (src/, test/, cli/): 42 `*.Tests` + 30 `*.Conformance` + `CyberCloud.Isolation` = 73, out of 141
# .csproj files there. Build.Test.cs § StartsCluster and build/README.md § "How many
# container-backed suites run at once" both record the same 73 from the same day.
# ⚠ What makes it stale: any project added under src/, test/ or cli/ whose name ends `.Tests` or
# `.Conformance`. It is a comment, so nothing goes red — re-run the count above.
#
# So the build stays lenient and the workflow is strict. Three independent guards, because each
# catches a different way of running nothing:
#
#   1. the discovery sentinel  — Build.Test.cs found no project to run
#   2. zero .trx files         — discovery found projects and none of them produced a report
#   3. a .trx with 0 executed  — a suite ran and executed nothing
#
# Guard 3 overlaps `--minimum-expected-tests 1`, which Build.Test.cs already passes to every host.
# Overlapping on purpose: that flag lives in the file another agent owns, and a guard whose only
# evidence is a flag somebody else can remove is not a guard.

set -euo pipefail

results="${1:?usage: assert-suites-ran.sh <results-directory> <build-log>}"
log="${2:?usage: assert-suites-ran.sh <results-directory> <build-log>}"

fail() {
    echo "::error title=Test suites::$1"
    exit 1
}

# ── 1. The discovery sentinel ─────────────────────────────────────────────────────────────────────
#
# Matched on the stable half of Build.Test.cs's message. Deliberately not the whole sentence: a
# reworded message should not silently disable this check, and this fragment is the part that
# cannot be reworded without changing what it means.
if [ -f "$log" ] && grep -q 'no per-PR test projects found' "$log"; then
    fail "./build.sh Test reported 'no per-PR test projects found' and exited 0. On CI that is a failure: docs/plan/23 § CI shape gates every PR on this target, and a PR gate that discovered nothing is not a gate. Check Build.Test.cs § SuiteOwning and Directory.Build.props § Project role detection — the two have to agree."
fi

# ── 2. At least one report ────────────────────────────────────────────────────────────────────────
if [ ! -d "$results" ]; then
    fail "$results does not exist, so no suite wrote a .trx. ./build.sh Test creates it before running anything; a missing directory means the run stopped before that."
fi

shopt -s nullglob
reports=("$results"/*.trx)
shopt -u nullglob

if [ "${#reports[@]}" -eq 0 ]; then
    fail "$results holds no .trx file. Every suite Build.Test.cs runs is invoked with --report-xunit-trx, so zero reports means zero suites ran."
fi

# ── 3. Every report has executed tests ────────────────────────────────────────────────────────────
#
# xunit's TRX carries <Counters total="N" executed="N" passed="N" .../>. Reading `executed` rather
# than counting <UnitTestResult> elements: they are the same number today, and `executed` is the
# one the format defines.
total=0
empty=()

for report in "${reports[@]}"; do
    executed=$(grep -o 'executed="[0-9]*"' "$report" | head -1 | grep -o '[0-9]*' || true)
    executed="${executed:-0}"
    total=$((total + executed))
    printf '  %-52s %6s executed\n' "$(basename "$report")" "$executed"
    [ "$executed" -eq 0 ] && empty+=("$(basename "$report")")
done

if [ "${#empty[@]}" -gt 0 ]; then
    fail "${#empty[@]} suite(s) reported zero executed tests: ${empty[*]}. A suite that discovers nothing and reports success is the failure this job exists to catch."
fi

echo "Test: ${#reports[@]} suite(s), $total test(s) executed."

# The count lands in the job summary so the number is visible on the run page, not only in a log
# somebody has to expand. A drop from 73 suites to 3 is the kind of thing that is obvious in a
# table and invisible in a scrollback.
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "### Test suites"
        echo
        echo "| Suites | Tests executed |"
        echo "|---:|---:|"
        echo "| ${#reports[@]} | $total |"
    } >> "$GITHUB_STEP_SUMMARY"
fi
