#!/usr/bin/env bash
#
# Enforces docs/plan/23 § CI shape: "25 minutes for a PR is a budget, not an observation. It is
# enforced: a PR that pushes the pipeline past it fails, and the fix is parallelism or moving a test
# to nightly — with a written reason."
#
# ── ⚠ WHY THIS IS NOT `timeout-minutes: 25` ──────────────────────────────────────────────────────
#
# A job timeout does enforce a budget. What it cannot do is say what spent it. The runner cancels
# the job mid-step, the log ends in the middle of a sentence, and the developer gets a red tick
# whose only content is "this took too long" — with no ranking of what took long, and no way to tell
# a pipeline that crept from 22 to 26 minutes from one job that hung. The fix the doc prescribes,
# "parallelism or moving a test to nightly", needs to know WHICH test.
#
# So this runs after everything, reads the wall clock off the Actions API, and fails naming the job
# that finished last, how long each one took, and what the pipeline would have cost without it.
# `timeout-minutes` is still set on every job as a backstop against a hang — this is the diagnosis.
#
# ── ⚠ AND WHY IT HAS A ROSTER ────────────────────────────────────────────────────────────────────
#
# A budget over "whatever jobs happened to run" gets cheaper every time a job is deleted, skipped,
# or filtered out by a path expression that matched nothing. That is the same shape as the provider
# discovery that matched at a fixed depth, found zero providers, and reported success. So the roster
# is explicit, every name on it has to have run, and a rostered job that came back `skipped` fails
# this check rather than shortening the measurement.
#
# Environment:
#   GH_TOKEN         a token with actions:read
#   GITHUB_REPOSITORY, GITHUB_RUN_ID, GITHUB_RUN_ATTEMPT   supplied by the runner
#   BUDGET_MINUTES   the budget, in minutes
#   EXPECTED_JOBS    comma-separated job names that must have run
#   SELF_JOB         this job's name, excluded from the measurement (it is still running)
#   JOBS_JSON        optional: read jobs from this file instead of the API. Only for testing this
#                    script; a run that sets it is measuring a fixture, not a pipeline
#   RUN_STARTED_AT   optional: pairs with JOBS_JSON

set -euo pipefail

budget_minutes="${BUDGET_MINUTES:?BUDGET_MINUTES is required}"
expected="${EXPECTED_JOBS:?EXPECTED_JOBS is required}"
self="${SELF_JOB:-budget}"

work="${RUNNER_TEMP:-/tmp}/budget"
mkdir -p "$work"
jobs="$work/jobs.json"

if [ -n "${JOBS_JSON:-}" ]; then
    echo "⚠ Reading jobs from $JOBS_JSON — this is the script's own test harness, not a pipeline."
    cp "$JOBS_JSON" "$jobs"
    run_started_at="${RUN_STARTED_AT:?RUN_STARTED_AT is required alongside JOBS_JSON}"
else
    repo="${GITHUB_REPOSITORY:?}"
    run_id="${GITHUB_RUN_ID:?}"
    attempt="${GITHUB_RUN_ATTEMPT:-1}"

    # The per-attempt endpoint, not repos/{}/actions/runs/{}/jobs. On a re-run the latter returns
    # the jobs of the latest attempt mixed with the ones that were not re-run, and a budget computed
    # over two attempts is a number about nothing.
    gh api --paginate "repos/$repo/actions/runs/$run_id/attempts/$attempt/jobs" \
        --jq '.jobs[]' | jq -s '.' > "$jobs"

    run_started_at=$(gh api "repos/$repo/actions/runs/$run_id/attempts/$attempt" --jq '.run_started_at')
fi

verdict="$work/verdict.json"

jq -n \
    --slurpfile jobs "$jobs" \
    --arg start "$run_started_at" \
    --arg roster "$expected" \
    --arg self "$self" \
    --argjson budget "$budget_minutes" '
    ($jobs[0] // []) as $all
    | ($roster | split(",") | map(gsub("^\\s+|\\s+$"; "")) | map(select(length > 0))) as $names
    | ($start | fromdateiso8601) as $t0
    # Two renamings stand between a roster entry and the name the API reports, and they compose:
    #   * a matrix leg is reported as "name (leg)";
    #   * a job from a reusable workflow is reported as "caller-job / name".
    # Strip the caller prefix, then allow a matrix suffix. String operations rather than a regex,
    # because a roster entry is a job name and job names are full of characters a regex cares about.
    | def bare: if (index(" / ") != null) then (split(" / ") | last) else . end;
      def claims($n): select((.name | bare) == $n or (.name | bare | startswith($n + " (")));
      ($all | map(select(.name != $self))) as $measured
    | ($measured
        | map(select(.completed_at != null)
              | { name, conclusion,
                  seconds: ((.completed_at | fromdateiso8601) - (.started_at | fromdateiso8601)),
                  finished: ((.completed_at | fromdateiso8601) - $t0) })
        | sort_by(-.finished)) as $rows
    | ($names | map(. as $n | { name: $n, matched: ($measured | map(claims($n)) | length) })) as $roll
    | {
        budget: $budget,
        run_started_at: $start,
        jobs_seen: ($all | length),
        rows: $rows,
        elapsed: ($rows | map(.finished) | max // 0),
        slowest: ($rows | first),
        missing: ($roll | map(select(.matched == 0) | .name)),
        skipped: ($measured
                  | map(select(.conclusion == "skipped" or .conclusion == "cancelled"))
                  | map(.name + " (" + (.conclusion // "null") + ")")),
        unfinished: ($measured | map(select(.completed_at == null) | .name)),
      }' > "$verdict"

fail() {
    echo "::error title=PR budget::$1"
    exit 1
}

emit() {
    echo "$1"
    [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"
    return 0
}

jobs_seen=$(jq -r '.jobs_seen' "$verdict")
elapsed=$(jq -r '.elapsed | floor' "$verdict")

# ── The anti-vacuity guards, before the verdict ───────────────────────────────────────────────────
#
# Each of these is a way this check could report "well within budget" having measured nothing.

if [ "$jobs_seen" -eq 0 ]; then
    fail "the Actions API returned 0 jobs for this run. Nothing was measured, so this is a failure and not a pass. Check that the job grants \`actions: read\`."
fi

missing=$(jq -r '.missing | join(", ")' "$verdict")
if [ -n "$missing" ]; then
    fail "rostered job(s) never ran: $missing. A budget measured over a pipeline that is missing a job is a budget that got cheaper by doing less. Either the job was removed and EXPECTED_JOBS was not, or a condition on it evaluated false."
fi

skipped=$(jq -r '.skipped | join(", ")' "$verdict")
if [ -n "$skipped" ]; then
    fail "job(s) were skipped or cancelled rather than run: $skipped. docs/plan/23 § Test layers puts these rows on every PR, so a skip is a gate that did not happen — and it would have shortened the measurement below."
fi

unfinished=$(jq -r '.unfinished | join(", ")' "$verdict")
if [ -n "$unfinished" ]; then
    fail "job(s) had not finished when the budget was measured: $unfinished. This job must \`needs:\` every job it measures."
fi

if [ "$elapsed" -le 0 ]; then
    fail "measured elapsed time of ${elapsed}s, which cannot be right. The clock, not the pipeline, is what failed here."
fi

# ── The report ────────────────────────────────────────────────────────────────────────────────────

emit "### PR pipeline budget"
emit ""
emit "docs/plan/23 § CI shape: **≤ ${budget_minutes} minutes**, enforced."
emit ""
emit "| Job | Duration | Finished at (from run start) |"
emit "|---|---:|---:|"

while IFS=$'\t' read -r name seconds finished; do
    emit "| \`$name\` | $((seconds / 60))m $((seconds % 60))s | $((finished / 60))m $((finished % 60))s |"
done < <(jq -r '.rows[] | [.name, (.seconds | floor), (.finished | floor)] | @tsv' "$verdict")

emit ""
emit "**Pipeline: $((elapsed / 60))m $((elapsed % 60))s** of a ${budget_minutes}m budget."
emit ""

budget_seconds=$((budget_minutes * 60))

if [ "$elapsed" -le "$budget_seconds" ]; then
    emit "✔ Within budget, with $(((budget_seconds - elapsed) / 60))m $(((budget_seconds - elapsed) % 60))s to spare."
    exit 0
fi

# The diagnosis the doc asks for: which job, by how much, and what the pipeline would have cost
# without it — because "move a test to nightly" is only actionable when you know which test.
slowest_name=$(jq -r '.slowest.name' "$verdict")
slowest_seconds=$(jq -r '.slowest.seconds | floor' "$verdict")
runner_up=$(jq -r '(.rows[1].finished // 0) | floor' "$verdict")
over=$((elapsed - budget_seconds))

emit "✘ **Over budget by $((over / 60))m $((over % 60))s.**"
emit ""
emit "\`$slowest_name\` finished last, after $((slowest_seconds / 60))m $((slowest_seconds % 60))s of work. Without it the pipeline would have taken $((runner_up / 60))m $((runner_up % 60))s."

fail "the PR pipeline took $((elapsed / 60))m $((elapsed % 60))s against a ${budget_minutes}m budget — over by $((over / 60))m $((over % 60))s. \`$slowest_name\` finished last. docs/plan/23 § CI shape: the fix is parallelism, or moving a test to nightly WITH A WRITTEN REASON — not raising this number. 'A 40-minute PR pipeline is how a team stops running tests locally and starts merging on hope.'"
