#!/usr/bin/env bash
#
# docs/plan/23 § Test layers, row Security: `NuGetAudit`, gated on "No criticals".
#
# ── ⚠ WHY THIS IS A SEPARATE RUN AND NOT JUST THE BUILD'S RESTORE ────────────────────────────────
#
# Directory.Build.props § Restore turns NuGetAudit on at `NuGetAuditMode=direct` and then exempts
# NU1901-NU1904 from TreatWarningsAsErrors, with the comment: "a red build caused by someone else's
# release. CI runs Build.Licence/audit separately." This is that separate run, and it changes two
# things on purpose:
#
#   * `NuGetAuditMode=all`. A critical CVE in a transitive package is in the shipped closure whether
#     or not anything references it directly. `direct` is the right setting for a developer's inner
#     loop and the wrong one for the security gate.
#
#   * It fails. That is the whole difference between an advisory and a gate.
#
# ⚠ It stays out of `./build.sh`: the compile path must not go red because a CVE was published
# overnight against a package nobody touched, and the props file has already made that call.

set -euo pipefail

solution="${1:-CyberCloud.slnx}"
log="${RUNNER_TEMP:-/tmp}/vulnerable.log"

echo "Restoring $solution with NuGetAuditMode=all"
dotnet restore "$solution" -p:NuGetAuditMode=all

dotnet list "$solution" package --vulnerable --include-transitive 2>&1 | tee "$log"

# ⚠ `dotnet list package` over a solution it cannot read, or one with no projects, prints a header
# and exits 0. Counting the projects it reported on is what stops "no vulnerabilities" from meaning
# "no packages were looked at" — the same reasoning as `--minimum-expected-tests 1` one layer down.
#
# ⚠ Both phrasings, because the tool uses a different one per outcome: "The given project `X` has no
# vulnerable packages given the current sources" against "Project `X` has the following vulnerable
# packages". The first draft of this line counted only the second, so a completely clean audit
# counted 0 projects and failed itself. Caught by running it, which is the only way that gets caught.
projects=$(grep -cE 'has (no vulnerable packages|the following vulnerable packages)' "$log" || true)
echo "projects audited: $projects"

if [ "$projects" -eq 0 ]; then
    echo "::error title=NuGetAudit::dotnet list package reported on 0 projects, so nothing was audited. A clean result over an empty list is not a clean result."
    exit 1
fi

# The report puts one severity word per vulnerable package line, after the resolved version.
if grep -qE '(Low|Moderate|High|Critical)[[:space:]]+https://github.com/advisories' "$log"; then
    echo "::error title=NuGetAudit::vulnerable package(s) in the restore closure, listed above. docs/plan/23 § Test layers, row Security: 'No criticals'. The fix is a bump in Directory.Packages.props."
    exit 1
fi

echo "No vulnerable packages across $projects project(s)."

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "### NuGetAudit"
        echo
        echo "$projects project(s) audited at \`NuGetAuditMode=all\`, including transitive packages. No advisories."
    } >> "$GITHUB_STEP_SUMMARY"
fi
