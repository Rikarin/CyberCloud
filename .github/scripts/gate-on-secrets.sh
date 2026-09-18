#!/usr/bin/env bash
#
# Decides whether a job that needs secrets can run, and says so in a way the run page shows.
#
# Usage: gate-on-secrets.sh <label> <because> <NAME=what it unblocks> [NAME=...]
#        Values are read from the environment variables of those names.
#        Writes `configured=true` or `configured=false` to $GITHUB_OUTPUT.
#
# ── ⚠ HOW THIS DIFFERS FROM require-secrets.sh, WHICH SAYS "FAIL, DON'T SKIP" ────────────────────
#
# require-secrets.sh is right that `if: secrets.X != ''` on the scanning step produces a green job
# that scanned nothing and said so nowhere. Issue #25 is what the alternative cost: main, nightly and
# weekly were red on every run for a year of Sundays because the secrets they name do not exist, and
# a red job cannot get redder — a suite went red on Linux with #75 and nobody saw it for ten days,
# because the only place it ran was inside a workflow that was red anyway.
#
# So this script skips, and it is allowed to because of three things the bare `if:` does not have:
#
#   1. The skip is a NAMED STEP in the job. The caller puts a step called
#      `skipped: CONTAINER_REGISTRY is not configured — docs/plan/23 § CI secrets` after this one,
#      conditioned on `configured != 'true'`, so the job's step list reads as what happened. A reader
#      of a green `images` job sees, in the list, the word "skipped" and the secret's name.
#   2. The secret is DOCUMENTED. docs/plan/23 § CI secrets lists every name, what it unlocks and who
#      sets it, and CiSecretsReconciliationTests fails the build when a workflow references a secret
#      that section does not list.
#   3. A PARTIAL configuration is still a failure. Two of three registry secrets set is somebody
#      halfway through configuring the job, and the honest answer is require-secrets.sh's: fail,
#      naming the one that is missing. Only "none of them" is a skip.
#
# The `::notice` and the step summary are the same sentence in two more places the run page shows,
# because the failure this guards against is a green tick nobody reads past.

set -euo pipefail

label="${1:?usage: gate-on-secrets.sh <label> <because> <NAME=unblock>...}"
because="${2:?usage: gate-on-secrets.sh <label> <because> <NAME=unblock>...}"
shift 2

if [ "$#" -eq 0 ]; then
    echo "::error title=$label::gate-on-secrets.sh was given nothing to gate on. A gate over an empty list is always open, which is the failure this script exists to make visible."
    exit 1
fi

set_names=()
unset_names=()

for requirement in "$@"; do
    name="${requirement%%=*}"

    if [ -n "${!name:-}" ]; then
        set_names+=("$name")
    else
        unset_names+=("$name")
    fi
done

# Every check evaluated before anything is written, so the output is the whole answer.
output="${GITHUB_OUTPUT:-/dev/null}"

if [ "${#unset_names[@]}" -eq 0 ]; then
    for name in "${set_names[@]}"; do
        echo "  ✔ \$$name is set"
    done
    echo "$label: ${#set_names[@]} of ${#set_names[@]} secret(s) configured — running."
    echo "configured=true" >> "$output"
    exit 0
fi

if [ "${#set_names[@]}" -gt 0 ]; then
    # Some set, some not: the caller is halfway through configuring this job, and a skip here would
    # hide the half that is missing. require-secrets.sh fails naming it.
    echo "::warning title=$label::${#set_names[@]} of $(( ${#set_names[@]} + ${#unset_names[@]} )) secret(s) are configured, so this is a partial configuration and not an unconfigured job. Skipping would hide the missing half; failing names it."
    echo "configured=false" >> "$output"
    exec "$(dirname -- "${BASH_SOURCE[0]}")/require-secrets.sh" "$label" "$because" "$@"
fi

names="$(printf '%s, ' "${unset_names[@]}")"
line="skipped: ${names%, } not configured — docs/plan/23 § CI secrets"

for name in "${unset_names[@]}"; do
    echo "  ○ \$$name is not set"
done

echo "$line"
echo "::notice title=$label::$line. $because"
echo "configured=false" >> "$output"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
        echo "### ○ $label — $line"
        echo
        echo "$because"
        echo
        echo "Nothing in this job ran. The secrets above are listed in docs/plan/23 § CI secrets with what each unlocks and who sets it."
    } >> "$GITHUB_STEP_SUMMARY"
fi

exit 0
