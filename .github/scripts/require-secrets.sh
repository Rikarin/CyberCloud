#!/usr/bin/env bash
#
# build/TargetPreconditions.cs, in bash, for the inputs a workflow needs that the build does not
# take as a parameter — a staging URL for ZAP, a kubeconfig for a deploy.
#
# Usage: require-secrets.sh <label> <because> <NAME=what it unblocks> [NAME=...]
#        Values are read from the environment variables of those names.
#
# ── ⚠ WHY EVERY CHECK IS EVALUATED, AND WHY IT NEVER SKIPS ───────────────────────────────────────
#
# The two properties are copied straight from TargetPreconditions.cs, and both are load-bearing:
#
#   * Collect, don't throw on the first. A job with four unmet inputs that reports one per run costs
#     four runs to configure, and the fourth is the first time anybody sees the whole list.
#
#   * FAIL, don't skip. The tempting shape is `if: secrets.X != ''` on the scanning step — and it
#     produces a green job that scanned nothing, which is the exact failure this whole set of
#     workflows is written against. An unconfigured scan is a scan that did not happen, and the tick
#     has to say so.

set -euo pipefail

label="${1:?usage: require-secrets.sh <label> <because> <NAME=unblock>...}"
because="${2:?usage: require-secrets.sh <label> <because> <NAME=unblock>...}"
shift 2

if [ "$#" -eq 0 ]; then
    echo "::error title=$label::require-secrets.sh was given nothing to require. A precondition list that checked nothing is not satisfied, it is empty."
    exit 1
fi

met=()
unmet=()

for requirement in "$@"; do
    name="${requirement%%=*}"
    unblock="${requirement#*=}"

    if [ -n "${!name:-}" ]; then
        met+=("$name")
        echo "  ✔ \$$name is set"
    else
        unmet+=("$name|$unblock")
        echo "  ✘ \$$name is not set"
    fi
done

if [ "${#unmet[@]}" -eq 0 ]; then
    echo "$label: ${#met[@]} of ${#met[@]} precondition(s) satisfied."
    exit 0
fi

for entry in "${unmet[@]}"; do
    echo "::error title=$label::\$${entry%%|*} is not set. To unblock: ${entry#*|}"
done

echo "::error title=$label::$label is BLOCKED on ${#unmet[@]} of $(( ${#met[@]} + ${#unmet[@]} )) precondition(s), listed above. $because ⚠ This step is not unwritten — it is unrunnable here, and every line above is a secret to create, not code to write."
exit 1
