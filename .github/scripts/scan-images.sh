#!/usr/bin/env bash
#
# docs/plan/23 § Test layers, row Security: "Trivy on images", gated on "No criticals".
#
# ── ⚠ IT SCANS DIGESTS, AND THE DIGESTS COME FROM THE RUN THAT PUSHED THEM ───────────────────────
#
# The easy version of this scans `$REGISTRY/cybercloud-silo-host:latest`. Two things are wrong with
# it, and the second is the one that matters:
#
#   1. docs/plan/18 § Platform security: "A pinned digest, never a tag." A scan of a tag is a scan of
#      whatever that tag happened to point at this minute, which is not necessarily what is running.
#   2. It hard-codes the image list. main.yml discovers its hosts from src/Hosts (Build.Images.cs
#      § ImageHostProjects), so a fifth host added there would be built, pushed, deployed — and not
#      scanned, by a job that reported success over four of five images.
#
# So the list comes from artifacts/images.json, which Build.Images.cs calls "the only durable output
# of the target", downloaded from the most recent successful `main` run.

set -euo pipefail

registry="${CONTAINER_REGISTRY:?CONTAINER_REGISTRY is required}"
work="${RUNNER_TEMP:-/tmp}/trivy"
mkdir -p "$work"

fail() {
    echo "::error title=Trivy::$1"
    exit 1
}

repo="${GITHUB_REPOSITORY:?}"

run=$(gh api "repos/$repo/actions/workflows/main.yml/runs?status=success&per_page=1" \
    --jq '.workflow_runs[0].id' 2>/dev/null || true)

if [ -z "$run" ] || [ "$run" = "null" ]; then
    fail "no successful run of main.yml to take image digests from, so there is nothing to scan. That is a block, not a clean scan: until main.yml has built and pushed images once, this row of docs/plan/23 § Test layers is not covered by anything."
fi

echo "Taking digests from main.yml run $run"
gh run download "$run" --repo "$repo" --name images --dir "$work" \
    || fail "run $run has no \`images\` artifact. main.yml uploads it with if-no-files-found: error, so a successful run without one means the artifact expired — retention is 90 days."

manifest="$work/artifacts/images.json"
[ -f "$manifest" ] || manifest="$work/images.json"
[ -f "$manifest" ] || fail "the images artifact contains no images.json. Nothing to scan."

mapfile -t refs < <(jq -r '.images[].reference' "$manifest")

# ⚠ The anti-vacuity guard. A manifest with an empty `images` array, or a schema change that renames
# the field, would leave `refs` empty — and a loop over nothing exits 0 with "no criticals".
if [ "${#refs[@]}" -eq 0 ]; then
    echo "--- images.json"
    cat "$manifest"
    fail "images.json lists 0 image references, so this scan would have covered nothing and passed. Either the manifest's shape changed or main.yml pushed no images."
fi

echo "$registry: scanning ${#refs[@]} image(s) by digest"

echo "${REGISTRY_PASSWORD:?}" | docker login "${registry%%/*}" \
    --username "${REGISTRY_USERNAME:?}" --password-stdin

findings=0

for ref in "${refs[@]}"; do
    echo "── $ref"
    # --exit-code 1 on HIGH,CRITICAL only: docs/plan/23 § Test layers gates this row on "No
    # criticals". Lower severities are printed, so they are visible without being a nightly failure
    # somebody starts ignoring.
    if ! trivy image --quiet --scanners vuln,secret \
        --severity HIGH,CRITICAL --exit-code 1 --ignore-unfixed "$ref"; then
        findings=$((findings + 1))
    fi
done

if [ "$findings" -gt 0 ]; then
    fail "$findings of ${#refs[@]} image(s) carry a fixable HIGH or CRITICAL vulnerability, listed above. docs/plan/23 § Test layers, row Security: 'No criticals'."
fi

echo "${#refs[@]} image(s) scanned, no fixable HIGH or CRITICAL findings."

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "### Trivy"
        echo
        echo "${#refs[@]} image(s) scanned by digest, from main.yml run \`$run\`. No fixable HIGH or CRITICAL findings."
    } >> "$GITHUB_STEP_SUMMARY"
fi
