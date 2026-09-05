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
# test/CyberCloud.Bundle.Cluster.Conformance for THREE of the nineteen components: cert-manager
# (`--phase 15`), openebs-localpv (`--phase 25`), and openebs-localpv with cloudnative-pg in one run
# over two phases (`--component` twice), each against a fresh k3s. The phase ORDER is asserted over
# all nineteen rows by a full `--dry-run`.
#
# ⚠ AND ON 2026-09-05 THE `kubectl` BRANCH BELOW RAN FOR THE FIRST TIME — BY HAND, NOT BY A TEST, AND
# AGAINST AN API SERVER WITH NO KUBELET. All SIX `manifest:` components were applied through this
# script, one `--component` run each, onto one `rancher/k3s:v1.35.7-k3s1` started with
# `--disable-agent`: the host's Docker reports `Cgroup Version: 1` and 1.35's kubelet refuses to
# start on such a host, so an agentless server was the only k3s available. What that made firsthand
# is the apply, the two-document path, and the establishment wait below. What it could not make
# firsthand is a running pod — NO OPERATOR THIS BRANCH INSTALLS HAS EVER STARTED, under test or by
# hand. bundle.yaml § owed, `the-manifest-path-waits-for-nothing`, carries every reading and what
# they leave owed.
#
# ⚠ THE COUNT, ON 2026-09-05, FROM THE TWO LISTS ABOVE: nineteen components, of which three are
# applied by a test, six were applied by that hand run, and TEN have never been applied by anything —
# kube-ovn, prometheus-operator-crds, kamaji, clickhouse-operator, mariadb-operator,
# opensearch-operator, redis-operator, seaweedfs-operator, strimzi-kafka-operator and
# victoria-metrics-operator. It goes stale the moment a suite or a person applies one more.
# charts/bundle/README.md § Verification, and its honest limit. `--verify` is the half that is
# reproducible with no cluster at all, and it is the half to run first.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dry_run=false
verify_only=false
only_phase=""
only_components=()
namespace_suffix="-system"
kubectl_args=()
helm_args=()

# ── The roster ────────────────────────────────────────────────────────────────────────────────
#
# Read from bundle.yaml so the order is the roster's rather than the filesystem's. `ls` would give
# alphabetical, which puts cert-manager before kube-ovn and installs a webhook onto a cluster with no
# CNI.
#
# ⚠ DEFINED ABOVE THE ARGUMENT LOOP RATHER THAN BESIDE THE CODE THAT SELECTS WITH IT, AND THE REASON
# IS `usage`. bash resolves a function name when the call runs, and `-h` is answered inside the loop
# — so a `roster` defined further down does not exist yet at the moment `--help` needs it. The usage
# text counts the phases out of bundle.yaml instead of stating them, which is the same rule this file
# already applies to versions and to the install order: a number written here would be a second place
# the roster lives, and #74's third finding is what happens when that second place goes stale.
roster() {
    awk '
        /^components:/ { inside = 1; next }
        /^[a-z]/ && !/^components:/ { inside = 0 }
        inside && /^  - name:/ { name = $3 }
        inside && /^    phase:/ { print $2, name }
    ' "$here/bundle.yaml"
}

usage() {
    cat <<'USAGE'
Usage: install.sh [options]

  --dry-run          Print every command and run none.
  --verify           Resolve every pin against its registry and apply nothing.
  --phase <n>        Install one phase only. Phases are listed in bundle.yaml.
  --component <name> Install one component only. Repeatable. Combines with --phase as an AND.
  --context <name>   kubectl/helm context.
  -h, --help         This.

Phases are barriers: every component in a phase is installed before the next phase begins, and
"installed" means helm waited for a `helm` component and this script waited for a `manifest:`
component's definitions to be Established. It does NOT yet mean a `manifest:` component's operator
has a running pod — bundle.yaml § owed, `the-manifest-path-waits-for-nothing`, is that half.

--phase narrows the run to one phase and --component to one component, so --component is the flag
for repairing a row. A phase is not a row:
USAGE
    roster | awk '{ held[$1]++ } END { for (p in held) print p, held[p] }' \
        | sort -n \
        | awk '{ printf "\n  phase %-3s %2d component%s", $1, $2, ($2 == 1 ? "" : "s") }'
    cat <<'USAGE'


A selector that matches no component is an error, not an empty success.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --dry-run) dry_run=true; shift ;;
        --verify) verify_only=true; shift ;;
        --phase) only_phase="$2"; shift 2 ;;
        # ⚠ REPEATABLE, AND IT FILTERS THE ROSTER RATHER THAN ORDERING IT. The roster's order is the
        # install order — bundle.yaml's header calls it "a property of the set" — so two --component
        # flags given the other way round still install in roster order. A flag that reordered the
        # roster would be a second place the order is written.
        --component) only_components+=("$2"); shift 2 ;;
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

            # ⚠ THE ESTABLISHMENT WAIT IS UNCONDITIONAL, AND UNTIL #74 IT RAN ONLY FOR A COMPONENT
            # THAT HAPPENED TO DECLARE A `manifestExtra`. `kubectl apply` returns when the API server
            # has STORED the objects, which is before a CustomResourceDefinition is Established and
            # long before an operator Deployment has a running pod; two of the six components that
            # reach this branch have a second document and four do not, so four applies were followed
            # by nothing at all and the phase they sit in ended with definitions that may not yet
            # have been served.
            #
            # ⚠ AFTER EVERY MANIFEST COMPONENT RATHER THAN ONCE AT THE PHASE BOUNDARY, AND THAT IS A
            # STRONGER PLACE RATHER THAN A LAZIER ONE. #74 words the defect as "it runs INSIDE the
            # component rather than at the phase boundary", and a boundary-only wait would still be
            # wrong for the case the issue itself calls out: phase 40 holds cluster-api, kamaji,
            # kamaji-control-plane-provider and cluster-api-provider-kubevirt, and the two providers
            # admit against definitions and webhooks the rows BEFORE THEM IN THE SAME PHASE
            # installed. A wait that only fires when the phase ends cannot order those four.
            #
            # ⚠ AND THE BOUNDARY PROPERTY DOES FOLLOW FROM THIS ONE — BUT NOT FOR THE REASON THIS
            # COMMENT GAVE UNTIL THE #74 REVIEW, WHICH WAS FALSE FOR SIX OF THE EIGHT PHASES. It read
            # "because the last component of any phase has run it", which is written as a general
            # property of the roster and is not one. COUNTED OUT OF bundle.yaml ON 2026-09-05, by
            # taking each phase's LAST row and reading its component.yaml's `install:` key: phase 10
            # ends on kube-ovn (helm), 15 on cert-manager (helm), 20 on prometheus-operator-crds
            # (helm), 25 on openebs-localpv (helm), 30 on containerized-data-importer (manifest), 40
            # on cluster-api-provider-kubevirt (manifest), 50 on strimzi-kafka-operator (helm) and 60
            # on victoria-metrics-operator (helm). TWO of the EIGHT phases end on a `manifest:` row
            # and SIX do not, and phase 50's only manifest row — rabbitmq-cluster-operator — is fifth
            # of that phase's eight, so for six phases the last row never reaches this line at all.
            # The conclusion survives on the argument that is actually available, which is per row
            # rather than per phase: EVERY manifest row waits immediately after its OWN apply, so no
            # phase can END holding a definition this script applied and did not wait for, whatever
            # kind its last row happens to be. The tail of those six phases is a `helm` or
            # `helm-archive` row, which needs no line here — `--wait` is helm's own barrier, and it
            # is the clause that was always true. ⚠ The reason mattered rather than the conclusion:
            # this argument is the whole of why #74's "wait at the phase boundary" is answered with a
            # per-component wait instead, so an argument stated as a property nothing has is the same
            # defect as a version pinned in two places.
            #
            # ⚠ `crd --all` AND NOT THE COMPONENT'S OWN DEFINITIONS, WHICH IS DELIBERATE AND IS THE
            # WEAKER OF THE TWO. A per-component list would be exact, and nothing here can spell it:
            # a component.yaml records `serves:` as group/version pairs, not definition names, so the
            # names would be a new key nothing checks — the exact defect bundle.yaml § owed,
            # `images-are-not-pinned-by-digest`, records about `imageDigest:`. `--all` is broader
            # than the component and cannot be narrower than it, so it cannot pass while this
            # component's definitions are unestablished, which is the property the barrier needs.
            # ⚠ AND IT CANNOT PASS VACUOUSLY, WHICH IS THE FAILURE #74 IS ABOUT — RE-MEASURED ON THE
            # FORM THIS LINE ACTUALLY RUNS. Until the #74 review the evidence cited here was a run of
            # `kubectl wait --for=condition=Established crd -l <label nothing carries>`, which is a
            # LABEL SELECTOR and not the `--all` below, so it said nothing about the shipped line.
            # Re-measured on 2026-09-05 against `rancher/k3s:v1.35.7-k3s1` started `--disable-agent`,
            # on an `--all` selection that really is empty — `kubectl wait --for=condition=Ready
            # --timeout=5s node --all`, on an agentless server, which has NO nodes: it prints "error:
            # no matching resources found" and exits 1, exactly as the label form does. Both forms
            # reach the same resource builder and it is the builder that refuses an empty result, so
            # the guard does cover `--all`.
            # ⚠ AND THE SHIPPED LINE DOES NOT REST ON THAT GUARD ANYWAY, which is worth writing down
            # because `--all` can hardly be empty here: a fresh k3s carries FOUR definitions before
            # anything in this bundle runs — addons.k3s.cattle.io, etcdsnapshotfiles.k3s.cattle.io,
            # helmchartconfigs.helm.cattle.io and helmcharts.helm.cattle.io, counted with
            # `kubectl get crd` on that same cluster the same day, as soon as its API server answered,
            # and the same four the 2026-09-05 hand run counted independently. What makes this line non-vacuous is the apply directly above it:
            # `kubectl apply --server-side` returns once the API server has STORED the objects, so
            # this component's own definitions are already in the selection the wait lists.
            # ⚠ ITS COST, MEASURED THE SAME DAY ON THE SAME CLUSTER, WITH ALL SIX MANIFEST COMPONENTS
            # ALREADY APPLIED AND EVERY DEFINITION ALREADY ESTABLISHED: 3.9 s, 4.4 s and 4.2 s over
            # THIRTY-SIX definitions across three runs, against 0.6 s over five earlier in the same
            # session. That is kubectl opening one watch per definition rather than any waiting, so a
            # full install pays it six times and it grows with the roster.
            #
            # ⚠ AND THE PRICE, WHICH IS A FALSE FAILURE, AND WHICH NOTHING HERE RECORDED UNTIL THE
            # #74 REVIEW ASKED FOR IT. Everything above argues the two directions that would make this
            # line too WEAK — it cannot pass while this component's definitions are unestablished, and
            # it cannot pass over an empty set. The direction left unargued is the one `--all` creates
            # by being cluster-wide: it waits on definitions this bundle never installed, so ANY
            # definition on the cluster that is not Established and never will be blocks the run. The
            # terminal case is a name conflict — a CustomResourceDefinition whose plural, singular,
            # kind or shortName collides with one already served is marked NamesAccepted=False and
            # Established=False and does not recover — which is exactly what a half-installed operator
            # leaves behind; a definition mid-deletion is the other. MEASURED ON 2026-09-05 ON THAT
            # SAME AGENTLESS k3s, by applying two definitions in one group sharing the shortName
            # `dup`: the second reports Established=False and NamesAccepted=False, and `kubectl wait
            # --for=condition=Established --timeout=20s crd --all` then burned the full 20.09 s and
            # exited 1 — while reporting "timed out" for THREE definitions that were Established=True,
            # because one invocation shares one deadline across every object it walks. The failure
            # does not reliably even name the definition that caused it.
            # ⚠ WHAT THAT COSTS, COUNTED ON 2026-09-05. Before #74 this wait ran only for a component
            # declaring a `manifestExtra`, which is TWO of the six manifest rows — kubevirt and
            # containerized-data-importer, the only two component.yaml files with that key. It now
            # runs for all SIX, so one stuck definition anywhere on the cluster goes from breaking two
            # of six manifest components to breaking six of six. Under `set -e` the first failing wait
            # ends the run, so the bill is ONE 5 m timeout charged to the first manifest row the run
            # reaches — kubevirt, phase 30, on a run with no selector — a component that is fine and
            # is not what went wrong; and the bound on a run where
            # every wait is slow but succeeds went from 2 × 5 m to 6 × 5 m. It is ACCEPTED rather than
            # fixed, because the only narrower selector is the per-component definition list the
            # paragraph above explains this file cannot spell without a key nothing checks. bundle.yaml
            # § owed, `the-manifest-path-waits-for-nothing`, carries it as owed rather than as an
            # accident, and test/CyberCloud.Bundle.Cluster.Conformance § BundleInstaller.Budget
            # records what it does to the harness budget.
            run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} wait --for=condition=Established --timeout=5m \
                crd --all

            # ⚠ The second document, where there is one, is a custom resource that names a kind the
            # first document just defined. `kubectl apply` on both at once loses that race often
            # enough to matter, so they are separate applies with the wait above between them. Run
            # firsthand for the first time on 2026-09-05 for kubevirt and containerized-data-importer,
            # the two components that have one: `kubevirt.kubevirt.io/kubevirt` and
            # `cdi.cdi.kubevirt.io/cdi` were both admitted after the wait, and the whole component
            # took under four seconds each with nothing to pull.
            extra=$(key "$file" manifestExtra)
            if [[ -n "$extra" ]]; then
                run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side -f "$extra"
            fi

            # ⚠ AND THE OTHER HALF OF THE BARRIER IS STILL NOT HERE, WHICH IS #74'S FINDING 2 AND IS
            # NOT CLOSED BY THE LINE ABOVE. "Established" says the API server will now serve the kind.
            # It says nothing about the operator that reconciles it, and a phase-40 provider whose
            # controller has no running pod fails exactly like one whose CRD is missing, only later.
            # The fix is `--for=condition=Available` on each component's own Deployments, and it is
            # STILL a guess for the reason the issue gives: the 2026-09-05 run had no kubelet, so not
            # one of those Deployments has ever had a pod. What that run did settle is that the
            # obvious spelling is wrong — the six components put EIGHT Deployments in EIGHT
            # namespaces and not one namespace is the `${name}${namespace_suffix}` this script
            # computes. bundle.yaml § owed, `the-manifest-path-waits-for-nothing`, lists all eight.
            ;;
    esac
}

# ── The selection ─────────────────────────────────────────────────────────────────────────────
#
# ⚠ THE ROSTER FILTERED, AND NEVER THE SELECTORS EXPANDED. `--phase 99` used to be spelled
# `phases="$only_phase"`, which took the caller's word for it: the run printed one empty phase header
# and exited 0 under "Bundle applied", and `--verify --phase 99` printed "Every pin resolves" having
# resolved none. That is the mirror image of the defect verify_component's own comment records — "a
# verifier that fails when there is nothing to verify is the same defect as one that passes when
# there is" — and it is the more dangerous half, because the output reads like a green run.

selects() {
    local wanted found
    while read -r p component; do
        [[ -z "$only_phase" || "$p" == "$only_phase" ]] || continue

        if [[ ${#only_components[@]} -gt 0 ]]; then
            found=false
            for wanted in ${only_components[@]+"${only_components[@]}"}; do
                [[ "$wanted" == "$component" ]] && found=true
            done
            [[ "$found" == true ]] || continue
        fi

        printf '%s %s\n' "$p" "$component"
    done < <(roster)
}

# ⚠ The name check runs FIRST and over every --component, so a misspelled name is reported as a
# misspelled name. Left until after the emptiness check below it would be reported as "selected no
# component", which is true and is the wrong sentence to hand somebody who typed `cloudnativepg`.
for wanted in ${only_components[@]+"${only_components[@]}"}; do
    if ! roster | awk '{print $2}' | grep -qx -- "$wanted"; then
        printf 'install.sh: --component %s is not in bundle.yaml. A component off the roster is one\n' "$wanted" >&2
        printf 'this script never installs — charts/bundle/README.md § What a component owes.\n' >&2
        exit 2
    fi
done

selection=$(selects)

if [[ -z "$selection" ]]; then
    printf 'install.sh: --phase/--component selected no component of the %d in bundle.yaml.\n' \
        "$(roster | wc -l | tr -d ' ')" >&2
    printf 'Nothing was installed and nothing was verified. Phases: %s.\n' \
        "$(roster | awk '{print $1}' | sort -n -u | tr '\n' ' ')" >&2
    exit 2
fi

phases=$(printf '%s\n' "$selection" | awk '{print $1}' | sort -n -u)

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
    done < <(printf '%s\n' "$selection")
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
