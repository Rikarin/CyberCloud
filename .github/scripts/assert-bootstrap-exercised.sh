#!/usr/bin/env bash
#
# Proves the half of `./build.sh E2E` that does not need a suite actually happened.
#
# Usage: assert-bootstrap-exercised.sh <build-log> [preflight|cluster]
#
# ── ⚠ WHY A JOB THAT ALWAYS FAILS STILL NEEDS A GUARD ────────────────────────────────────────────
#
# `E2E` is BLOCKED today — no test/CyberCloud.E2E, no `cyc` under cli/ — so every nightly run of it
# ends red on preconditions. That is correct and Build.cs § target graph argues for it. It also
# means the job's own red tick says nothing about the work that DID happen before the block:
#
#   * Build.E2E.cs § ExerciseBootstrap runs seven cases against deploy/bootstrap/bootstrap.sh, each
#     pinned to its own refusal message, on every invocation. docs/plan/09 § The platform's own
#     cluster is the sentence that makes true: bootstrap.sh "is exercised by every e2e run, so it
#     cannot rot".
#   * Build.E2E.cs § BootstrapDryRun renders the manifests against a real API server — but SKIPS,
#     with a warning, when --kube-context is absent.
#
# So the failure mode this guards is not "the build lied". It is subtler and worse: the day
# somebody drops --kube-context from a job, the dry-run silently stops happening, the job goes on
# failing for its usual reason, and nobody notices that the one thing it was still testing is gone.

set -euo pipefail

log="${1:?usage: assert-bootstrap-exercised.sh <build-log> [preflight|cluster]}"
mode="${2:-preflight}"

fail() {
    echo "::error title=bootstrap::$1"
    exit 1
}

[ -f "$log" ] || fail "$log does not exist, so ./build.sh E2E produced no output. The target did not run at all."

# Build.E2E.cs § ExerciseBootstrap's own log line. Matched on its stable half.
grep -q 'bootstrap.sh preflight exercised' "$log" \
    || fail "./build.sh E2E did not exercise deploy/bootstrap/bootstrap.sh. ExerciseBootstrap runs before every precondition check, so the run stopped even earlier than that — check the log for a missing file or an unresolvable \`bash\`. docs/plan/09 § The platform's own cluster: the script 'is exercised by every e2e run, so it cannot rot'."

cases=$(grep -o 'preflight exercised — [0-9]* case' "$log" | grep -o '[0-9]*' | head -1)
echo "bootstrap.sh preflight: ${cases:-?} case(s) exercised."

if [ "$mode" != "cluster" ]; then
    exit 0
fi

# Build.E2E.cs § BootstrapDryRun warns and returns when there is no --kube-context.
if grep -q 'bootstrap dry-run against a cluster was SKIPPED' "$log"; then
    fail "the bootstrap dry-run was SKIPPED for want of a --kube-context, in a job whose whole purpose is to supply one. The manifests in deploy/bootstrap/ were never rendered or validated by an API server, so this cluster was created and never talked to. ○, not ✔."
fi

grep -q 'dry-run clean against context' "$log" \
    || fail "the bootstrap dry-run neither succeeded nor reported being skipped, so what it did is unknown. Read the ./build.sh E2E output above — a dry-run that the API server rejected is a real finding and the one this job exists to surface."

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
