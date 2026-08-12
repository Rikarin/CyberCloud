#!/usr/bin/env bash
#
# A stub target is allowed to be green only while the thing it says it is waiting for is genuinely
# absent. The moment that thing exists, the stub is a job that scans, lints, or tests nothing and
# reports success.
#
# Usage: assert-stub-still-blocked.sh <target> <build-log> <evidence-path> [evidence-path...]
#
# ── ⚠ THE ALTERNATIVE, AND WHY IT IS WORSE ───────────────────────────────────────────────────────
#
# The obvious guard is "fail if the log says 'Not implemented yet'". That makes the job red from the
# first run, and Build.cs § the target-graph note argues the case against exactly that: a gate
# everybody has learned to ignore is not a gate. It is right. `Portal` is on the every-PR path
# (docs/plan/23 § Test layers), and a PR check that has been red since before anyone joined is a
# check people learn to click past.
#
# So this is a tripwire rather than a blanket. It stays quiet while the stub's own stated reason
# holds, and it goes off the moment that reason stops being true — which is the only moment at which
# the green tick becomes a lie, and the moment nobody would otherwise notice.
#
# ⚠ The evidence paths are therefore not "some file that suggests progress". Each one is the thing
# the stub itself names as its blocker. If the stub's wording changes, the evidence path has to
# change with it, and that is a feature: the two are one claim.
#
# `Portal` is the worked example and the one that has been round the loop. Build.Portal.cs used to
# say it "depends on the pnpm workspace existing", so the evidence path was that workspace's
# lockfile; the workspace landed, this fired, and the target was written. The gate job still calls
# this, and it now exits 0 on the first branch below — the target is implemented, so there is no
# stub to guard. That is the end state this is for, not a reason to remove the call.

set -euo pipefail

target="${1:?usage: assert-stub-still-blocked.sh <target> <build-log> <evidence-path>...}"
log="${2:?usage: assert-stub-still-blocked.sh <target> <build-log> <evidence-path>...}"
shift 2

if [ "$#" -eq 0 ]; then
    echo "::error title=$target::assert-stub-still-blocked.sh was given no evidence path. A tripwire with nothing to trip on always passes, which is the failure it exists to prevent."
    exit 1
fi

if [ ! -f "$log" ]; then
    echo "::error title=$target::$log does not exist, so there is no build output to inspect. The target did not run."
    exit 1
fi

# Build.cs § NotImplementedYet is the one place this string is produced, and it is produced for
# every stub.
if ! grep -q 'Not implemented yet' "$log"; then
    echo "$target is implemented — Build.cs § NotImplementedYet did not fire. Nothing for this tripwire to guard."
    exit 0
fi

present=()
for evidence in "$@"; do
    # A glob is expanded here rather than by the caller so a pattern matching nothing is an absent
    # blocker rather than a literal filename that never exists.
    for match in $evidence; do
        [ -e "$match" ] && present+=("$match")
    done
done

if [ "${#present[@]}" -eq 0 ]; then
    cat <<MESSAGE
○ $target is a stub, and its stated blocker still holds — none of these exists yet:

$(printf '    %s\n' "$@")

That is a pass, and it is worth nobody's trust: nothing was linted, tested, or scanned. The moment
one of the paths above appears, this step turns red and stays red until the target is written.
MESSAGE
    if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        {
            echo "### ○ $target — stub, still blocked"
            echo
            echo "Nothing ran. The tripwire is armed on: \`$*\`"
        } >> "$GITHUB_STEP_SUMMARY"
    fi
    exit 0
fi

cat <<MESSAGE
::error title=$target::$target is still a stub, but the thing it was waiting for now exists.
MESSAGE

cat <<MESSAGE

Build.cs § NotImplementedYet fired for \`$target\`, so this job ran nothing and would have reported
success. But the blocker the stub names is gone — these exist:

$(printf '    %s\n' "${present[@]}")

docs/plan/23 § Build has a row for this target and docs/plan/23 § Test layers may have one too.
Implement it in build/Build.$target.cs. ⚠ Do not silence this by deleting the job: a green tick over
work that did not happen is the one outcome worse than a red one.
MESSAGE

exit 1
