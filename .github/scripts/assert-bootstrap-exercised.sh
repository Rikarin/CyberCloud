#!/usr/bin/env bash
#
# Proves the bootstrap dry-run actually happened — after `./build.sh Bootstrap`, or after the
# `./build.sh E2E` that runs the same phase first.
#
# Usage: assert-bootstrap-exercised.sh <build-log> [preflight|cluster]
#
# ── ⚠ WHY A JOB THAT CAN GO GREEN ON ITS OWN STILL NEEDS THIS ────────────────────────────────────
#
# This script was written for a job that always failed. `E2E` is BLOCKED — no test/CyberCloud.E2E,
# no `cyc` under cli/ — so every nightly run of it ended red on preconditions, and the job's own red
# tick said nothing about the work that DID happen before the block: seven preflight cases against
# deploy/bootstrap/bootstrap.sh and a dry-run against a real API server. Issue #25 gave that phase a
# target of its own, `Bootstrap`, so the kind and hostile-BYO jobs are green when the dry-run is and
# red when it is not. The guard stays, because the failure it was really about is still there:
#
#   * Build.Bootstrap.cs § BootstrapDryRun SKIPS, with a warning and exit 0, when --kube-context is
#     absent. The day somebody drops that flag from a job, the dry-run silently stops happening, the
#     job stays green, and nobody notices that the one thing it was testing is gone.
#
# On the `e2e` job — the one that still runs `E2E` against staging and still blocks — it also does
# what it always did: reads the pass out of the log of a red job, for the half that passed.

set -euo pipefail

log="${1:?usage: assert-bootstrap-exercised.sh <build-log> [preflight|cluster]}"
mode="${2:-preflight}"

fail() {
    echo "::error title=bootstrap::$1"
    exit 1
}

[ -f "$log" ] || fail "$log does not exist, so the build produced no output. The target did not run at all."

# Build.Bootstrap.cs § ExerciseBootstrap's own log line. Matched on its stable half.
grep -q 'bootstrap.sh preflight exercised' "$log" \
    || fail "the build did not exercise deploy/bootstrap/bootstrap.sh. ExerciseBootstrap is the first thing both Bootstrap and E2E do, so the run stopped even earlier than that — check the log for a missing file or an unresolvable \`bash\`. docs/plan/09 § The platform's own cluster: the script 'is exercised by every e2e run, so it cannot rot'."

cases=$(grep -o 'preflight exercised — [0-9]* case' "$log" | grep -o '[0-9]*' | head -1)
echo "bootstrap.sh preflight: ${cases:-?} case(s) exercised."

if [ "$mode" != "cluster" ]; then
    exit 0
fi

# Build.Bootstrap.cs § BootstrapDryRun warns and returns when there is no --kube-context.
if grep -q 'bootstrap dry-run against a cluster was SKIPPED' "$log"; then
    fail "the bootstrap dry-run was SKIPPED for want of a --kube-context, in a job whose whole purpose is to supply one. The manifests in deploy/bootstrap/ were never rendered or validated by an API server, so this cluster was created and never talked to. ○, not ✔."
fi

grep -q 'dry-run clean against context' "$log" \
    || fail "the bootstrap dry-run neither succeeded nor reported being skipped, so what it did is unknown. Read the build output above — a dry-run that the API server rejected is a real finding and the one this job exists to surface."

context=$(grep -o 'dry-run clean against context .*' "$log" | tail -1)
echo "✔ $context"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "### bootstrap.sh"
        echo
        echo "- preflight: ${cases:-?} case(s), each pinned to its own refusal message"
        echo "- \`--dry-run\`: clean — manifests rendered and validated by a real API server"
    } >> "$GITHUB_STEP_SUMMARY"
fi
