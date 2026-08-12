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
# every PR on `Test`, this repository has 26 per-PR suites today, and the day discovery returns
# zero of them is the day a green tick means nothing.
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
# somebody has to expand. A drop from 26 suites to 3 is the kind of thing that is obvious in a
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
