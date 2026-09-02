#!/usr/bin/env bash
#
# charts/bundle/install.sh — install the operator layer a platform cluster needs before any provider
# can converge. charts/bundle/README.md § Installing.
#
# ⚠ THIS SCRIPT HARD-CODES NO VERSION. Every pin is read out of a component.yaml, so a bump is a diff
# in one file that the Bundle gate reads and this script obeys. A version written here as well would
# be a second place to change and a second thing to disagree with the first.
#
# ⚠ THIS SCRIPT IS NOT deploy/bootstrap/bootstrap.sh AND MUST NOT GROW INTO IT. `bootstrap/` installs
# Cyber Cloud onto a cluster with kubectl and checked-in YAML only, because it is what an operator
# runs when the platform is the broken thing — deploy/README.md § The platform's own cluster is not
# Kamaji-hosted. This installs other people's operators into a cluster the platform will manage, uses
# helm, and is on no repair path.
#
# ⚠ WHAT HAS AND HAS NOT BEEN EXERCISED. Every URL and version below was resolved against its
# registry on the date each component records. The APPLY path is run against a real API server by
# test/CyberCloud.Bundle.Cluster.Conformance for TWO of the nineteen components — `--phase 15` and
# `--phase 25`, each against its own fresh k3s. Seventeen have never been applied by anything, no
# `manifest:` component has, so the `kubectl` branch below has never executed under test, and
# `--phase` is by its own usage text the flag that skips the barrier — so the phase ordering is
# unexercised too. charts/bundle/README.md § Verification, and its honest limit. `--verify` is the
# half that is reproducible with no cluster at all, and it is the half to run first.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dry_run=false
verify_only=false
only_phase=""
namespace_suffix="-system"
kubectl_args=()
helm_args=()

usage() {
    cat <<'USAGE'
Usage: install.sh [options]

  --dry-run          Print every command and run none.
  --verify           Resolve every pin against its registry and apply nothing.
  --phase <n>        Install one phase only. Phases are listed in bundle.yaml.
  --context <name>   kubectl/helm context.
  -h, --help         This.

Phases are barriers: every component in a phase is installed, and helm waits for it, before the
next phase begins. --phase skips that guarantee and is for repairing one row, not for installing.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run) dry_run=true; shift ;;
        --verify) verify_only=true; shift ;;
        --phase) only_phase="$2"; shift 2 ;;
        --context) kubectl_args+=(--context "$2"); helm_args+=(--kube-context "$2"); shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "install.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

# ── Reading a component.yaml ──────────────────────────────────────────────────────────────────
#
# ⚠ A deliberately narrow reader, for the same reason build/Build.Charts.cs hand-writes one: there is
# no yq on a fresh machine and adding a dependency to the script that installs the cluster is the
# wrong direction. The format is flat top-level `key: value` plus two block mappings, so `awk` reads
# it exactly. Anything outside that subset is a component.yaml the Bundle gate would already have
# rejected.

# key <file> <name> — the value of a top-level scalar, or empty.
key() {
    awk -v k="$2" '
        /^[A-Za-z]/ {
            split($0, parts, ":")
            if (parts[1] == k) {
                sub(/^[A-Za-z0-9]+:[ \t]*/, "")
                sub(/[ \t]+$/, "")
                gsub(/^"|"$/, "")
                print
                exit
            }
        }' "$1"
}

# helm_sets <file> — every `values:` entry as a --set argument.
helm_sets() {
    awk '
        /^values:/ { inside = 1; next }
        /^[A-Za-z]/ { inside = 0 }
        inside && /^  [A-Za-z]/ {
            line = $0
            sub(/^  /, "", line)
            idx = index(line, ":")
            name = substr(line, 1, idx - 1)
            value = substr(line, idx + 1)
            gsub(/^[ \t]+|[ \t]+$/, "", value)
            gsub(/^"|"$/, "", value)
            printf "--set\n%s=%s\n", name, value
        }' "$1"
}

run() {
    if [[ "$dry_run" == true ]]; then
        printf '  would run:'
        printf ' %q' "$@"
        printf '\n'
        return 0
    fi
    "$@"
}

# ── Verifying a pin ───────────────────────────────────────────────────────────────────────────
#
# One HTTP HEAD per pinned artefact. This is the check that answers "does the pin still resolve",
# which is the one thing about this directory that decays on its own — Task #109's "a version pin
# that was verified and points at a tag that does not exist".

verify_url() {
    local what="$1" url="$2" code
    code=$(curl -sSL -o /dev/null -w '%{http_code}' --max-time 60 "$url" || echo 000)
    if [[ "$code" == "200" ]]; then
        printf '  ✔ %-14s %s\n' "$what" "$url"
    else
        printf '  ✘ %-14s %s -> HTTP %s\n' "$what" "$url" "$code"
        return 1
    fi
}

verify_component() {
    local dir="$1" file="$1/component.yaml"
    local install repo chart version archive manifest extra
    install=$(key "$file" install)
    case "$install" in
        helm)
            repo=$(key "$file" repo); chart=$(key "$file" chart); version=$(key "$file" version)
            verify_url index "$repo/index.yaml" || return 1
            # `helm show chart` resolves the entry AND the version, which an index fetch does not.
            if ! helm show chart "$chart" --repo "$repo" --version "$version" >/dev/null 2>&1; then
                printf '  ✘ %-14s %s %s not in %s\n' chart "$chart" "$version" "$repo"
                return 1
            fi
            printf '  ✔ %-14s %s %s\n' chart "$chart" "$version"
            ;;
        helm-archive)
            archive=$(key "$file" archive)
            verify_url archive "$archive" || return 1
            ;;
        manifest)
            manifest=$(key "$file" manifest)
            verify_url manifest "$manifest" || return 1
            # ⚠ An `if` and not `[[ -n "$extra" ]] && { … }`, which is what this was and which reported
            # four false failures on its first run: a `&&` chain whose test is FALSE is the last
            # command of the branch, so the function returned 1 for every component that simply has no
            # second document. Four ✘ under eighteen ✔ — a verifier that fails when there is nothing
            # to verify is the same defect as one that passes when there is.
            extra=$(key "$file" manifestExtra)
            if [[ -n "$extra" ]]; then
                verify_url manifestExtra "$extra" || return 1
            fi
            ;;
        *)
            printf '  ✘ %-14s unknown install kind "%s"\n' install "$install"
            return 1
            ;;
    esac
}

# ── Installing a component ────────────────────────────────────────────────────────────────────

install_component() {
    local dir="$1" file="$1/component.yaml"
    local name install repo chart version archive manifest extra crds crdsVersion ns
    name=$(key "$file" component)
    install=$(key "$file" install)
    ns="${name}${namespace_suffix}"

    # ⚠ Not `mapfile`. It is bash 4, and macOS ships bash 3.2 — a script that installs a cluster is
    # the wrong place to discover that. The same reason every array below is expanded as
    # `${x[@]+"${x[@]}"}`: under `set -u`, bash 3.2 treats an empty array as unset.
    sets=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && sets+=("$line")
    done < <(helm_sets "$file")

    case "$install" in
        helm)
            repo=$(key "$file" repo); chart=$(key "$file" chart); version=$(key "$file" version)
            crds=$(key "$file" chartCrds)
            crdsVersion=$(key "$file" versionCrds)

            # ⚠ Definitions first when they are a separate chart, and --wait on them too. A controller
            # whose kinds are not established yet does not retry its watches on a schedule anybody
            # would want to wait for.
            if [[ -n "$crds" ]]; then
                run helm upgrade --install "$crds" "$crds" \
                    --repo "$repo" --version "$crdsVersion" \
                    --namespace "$ns" --create-namespace --wait --timeout 10m ${helm_args[@]+"${helm_args[@]}"}
            fi

            run helm upgrade --install "$name" "$chart" \
                --repo "$repo" --version "$version" \
                --namespace "$ns" --create-namespace --wait --timeout 10m \
                ${sets[@]+"${sets[@]}"} ${helm_args[@]+"${helm_args[@]}"}
            ;;
        helm-archive)
            archive=$(key "$file" archive)
            run helm upgrade --install "$name" "$archive" \
                --namespace "$ns" --create-namespace --wait --timeout 10m \
                ${sets[@]+"${sets[@]}"} ${helm_args[@]+"${helm_args[@]}"}
            ;;
        manifest)
            manifest=$(key "$file" manifest)
            run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side -f "$manifest"

            # ⚠ The second document, where there is one, is a custom resource that names a kind the
            # first document just defined. `kubectl apply` on both at once loses that race often
            # enough to matter, so they are separate applies with an establishment wait between them.
            extra=$(key "$file" manifestExtra)
            if [[ -n "$extra" ]]; then
                run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} wait --for=condition=Established --timeout=5m \
                    crd --all
                run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side -f "$extra"
            fi
            ;;
    esac
}

# ── The roster ────────────────────────────────────────────────────────────────────────────────
#
# Read from bundle.yaml so the order is the roster's rather than the filesystem's. `ls` would give
# alphabetical, which puts cert-manager before kube-ovn and installs a webhook onto a cluster with no
# CNI.

roster() {
    awk '
        /^components:/ { inside = 1; next }
        /^[a-z]/ && !/^components:/ { inside = 0 }
        inside && /^  - name:/ { name = $3 }
        inside && /^    phase:/ { print $2, name }
    ' "$here/bundle.yaml"
}

phases=$(roster | awk '{print $1}' | sort -n -u)
[[ -n "$only_phase" ]] && phases="$only_phase"

failures=0

for phase in $phases; do
    printf '\n── phase %s ──────────────────────────────────────────────────────────────\n' "$phase"

    while read -r p component; do
        [[ "$p" == "$phase" ]] || continue
        dir="$here/$component"

        if [[ ! -f "$dir/component.yaml" ]]; then
            printf '  ✘ %s has no component.yaml\n' "$component"
            failures=$((failures + 1))
            continue
        fi

        printf '\n  %s\n' "$component"

        if [[ "$verify_only" == true ]]; then
            verify_component "$dir" || failures=$((failures + 1))
        else
            install_component "$dir"
        fi
    done < <(roster)
done

printf '\n'

if [[ "$failures" -gt 0 ]]; then
    printf '%d component(s) failed. Nothing above this line is a claim about a cluster.\n' "$failures" >&2
    exit 1
fi

if [[ "$verify_only" == true ]]; then
    printf 'Every pin resolves. That is a claim about registries and about nothing else — no operator\n'
    printf 'was installed and no custom resource was reconciled. charts/bundle/README.md § Verification.\n'
elif [[ "$dry_run" == true ]]; then
    printf 'Dry run. No command above was executed.\n'
else
    printf 'Bundle applied. Run ./charts/bundle/install.sh --verify to re-resolve the pins.\n'
fi
