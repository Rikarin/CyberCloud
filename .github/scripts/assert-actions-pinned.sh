#!/usr/bin/env bash
#
# Every third-party action these workflows use is pinned to a commit SHA, with the release it stands
# for written beside it.
#
# Usage: assert-actions-pinned.sh [workflow-directory]     (default: .github, walked recursively)
#
# ── ⚠ WHY A SHA AND NOT A TAG, AND WHY A COMMENT BESIDE IT ───────────────────────────────────────
#
# A tag is a pointer somebody else moves. `actions/checkout@v4` runs whatever the actions org has
# pointed v4 at this minute, and the 2025 tj-actions/changed-files compromise was exactly that shape:
# every version tag repointed at a commit that exfiltrated the runner's secrets, and every workflow
# that trusted a tag ran it. A 40-hex SHA is content-addressed — it names one tree and cannot be
# moved to another. docs/plan/18 § Platform security says "a pinned digest, never a tag" of images;
# this is the same rule for the code that builds them, one layer up.
#
# The `# vX.Y.Z` comment is what makes the SHA reviewable: a bare SHA is a number nobody can bump
# with confidence, and Dependabot / Renovate keep the two in step when they update the pin. A SHA
# without the comment is refused here for that reason — it is a pin that will never be moved.
#
# ── ⚠ WHAT THIS DOES NOT CHECK ───────────────────────────────────────────────────────────────────
#
# That the SHA IS the release the comment names. That is a network question — the tag's commit on
# the action's own repository — and a check that runs on a runner with no reason to trust its own
# network answer would be asserting the thing it was asked to verify. Review a pin bump the way the
# comment invites: open the action's release page and compare. A local check has no way to ask, so
# it does not pretend to.
#
# Local actions (`uses: ./.github/actions/…`) are exempt: they are this repository's own tree at
# this commit, which is already a content address.

set -euo pipefail

root="${1:-.github}"

if [ ! -d "$root" ]; then
    echo "::error title=Action pins::$root does not exist, so there are no workflows to check. A pin check over nothing is a pass worth nobody's trust."
    exit 1
fi

# Every `uses:` line, with its file and line number. Local actions and reusable workflows in this
# repository begin `./` and are skipped below.
mapfile -t uses < <(grep -rnE '^\s*-?\s*uses:\s*' --include='*.yml' --include='*.yaml' "$root" | grep -vE 'uses:\s*\./')

if [ "${#uses[@]}" -eq 0 ]; then
    echo "::error title=Action pins::no third-party \`uses:\` found under $root. These workflows use a dozen; zero means the grep stopped matching, not that the pins are all local."
    exit 1
fi

# owner/repo[/path]@<40 hex>, then optional spaces, then `# v<something>`.
pinned='uses:[[:space:]]*[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}[[:space:]]+#[[:space:]]*v[0-9]'

violations=()

for entry in "${uses[@]}"; do
    if ! [[ "$entry" =~ $pinned ]]; then
        violations+=("$entry")
    fi
done

if [ "${#violations[@]}" -gt 0 ]; then
    for violation in "${violations[@]}"; do
        file="${violation%%:*}"
        rest="${violation#*:}"
        line="${rest%%:*}"
        text="${rest#*:}"
        echo "::error file=$file,line=$line,title=Action pins::not pinned to a commit SHA with its release beside it: ${text## }"
    done

    echo "::error title=Action pins::${#violations[@]} of ${#uses[@]} action reference(s) are not \`owner/repo@<40-hex sha> # vX.Y.Z\`. A tag is a pointer somebody else moves; pin the commit and write the release it stands for beside it. docs/plan/23 § Action pins."
    exit 1
fi

echo "Action pins: ${#uses[@]} third-party reference(s), every one a commit SHA with its release beside it."
