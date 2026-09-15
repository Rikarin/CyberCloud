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
# test/CyberCloud.Bundle.Cluster.Conformance for THREE of the twenty components: cert-manager
# (`--phase 15`), openebs-localpv (`--phase 25`), and openebs-localpv with cloudnative-pg in one run
# over two phases (`--component` twice), each against a fresh k3s. The phase ORDER is asserted over
# all twenty rows by a full `--dry-run`.
#
# ⚠ ON 2026-09-05 THE `kubectl` BRANCH BELOW RAN FOR THE FIRST TIME — BY HAND, AGAINST AN API SERVER
# WITH NO KUBELET: the host's Docker then reported `Cgroup Version: 1`, 1.35's kubelet refuses to
# start on such a host, and `--disable-agent` was the only k3s available. That made the apply, the
# two-document path and the establishment wait firsthand, and left every operator unstarted.
#
# ⚠ AND ON 2026-09-15 THE WHOLE ROSTER RAN, PHASE BY PHASE, AGAINST A `rancher/k3s:v1.35.7-k3s1`
# WITH A REAL KUBELET — the same laptop, after its WSL2 kernel was moved to cgroup v2 (docs/plan/23
# § The lane that needs a kubelet). Nineteen of the twenty serve; kube-ovn is the one that cannot
# on a k3s that already has a CNI, and its component.yaml says what it needs instead. Four things
# in this file exist because of what that run found, each recorded at the line that changed:
# the `${VAR:=default}` substitution before every manifest apply (four controllers crashlooped
# without it), the per-component `waitFor:` after it (three shapes, none of them derivable from a
# rule), `--force-conflicts` on a re-apply (an operator adopts fields of its own definition), and
# the roster's phase 30 running CDI before KubeVirt (a barrier that held for its full timeout
# found the dependency the wait-free run had hidden). Two pins moved: opensearch-operator to the
# chart that agrees with its operator, redis-operator to the chart helm can read. bundle.yaml
# § owed, `the-manifest-path-waits-for-nothing`, carries the readings.
#
# ⚠ THE COUNT, ON 2026-09-15: twenty components, of which three are applied by a test
# (test/CyberCloud.Bundle.Cluster.Conformance), nineteen were applied through this script by hand
# on that day, and ONE — kube-ovn — by nothing that has succeeded. It goes stale the moment a suite
# applies one more. charts/bundle/README.md § Verification, and its honest limit. `--verify` is
# the half that is reproducible with no cluster at all, and it is the half to run first.
#
# ⚠ THE RECORDED DIGEST IS CONSUMED HERE, SINCE ISSUE #17, AND UNTIL THEN IT WAS CONSUMED NOWHERE.
# Every component.yaml records the images its artefact renders as `repository:tag@sha256:…`, and
# bundle.yaml § owed, `images-are-not-pinned-by-digest`, was honest that this was "a record, not a
# pin: the tag is still what reaches the kubelet". It still is — several charts have no digest key,
# and an override that froze bytes upstream never chose would be a fork. What changed is that this
# script now REFUSES a component before it applies anything when any recorded image is not pinned
# (`@unresolved`, or no digest at all) or when its tag no longer serves the recorded digest. So a
# tag that moved is no longer a red run of images.sh that somebody has to remember to make; it is a
# component that does not install. `verify_images` below is the gate, `--verify` runs it over the
# whole roster with no cluster, and test/CyberCloud.Bundle.Cluster.Conformance § BundleImagePins
# asserts both refusals against sabotaged copies of this directory.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# The tag-to-digest resolver, shared with images.sh so the recorder and this refuser agree.
# shellcheck source=oci.sh
. "$here/oci.sh"
# The `${VAR:=default}` pass a `manifest:` document gets before `kubectl apply` — the substitution
# clusterctl performs, which the Cluster API family's release documents assume. Its header carries
# the four-controller crashloop that made it necessary.
# shellcheck source=substitute.sh
. "$here/substitute.sh"
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

  --dry-run          Print every command and run none. Checks that every image is recorded with a
                     digest; resolves nothing.
  --verify           Resolve every pin, and every recorded image's tag, against its registry and
                     apply nothing.
  --phase <n>        Install one phase only. Phases are listed in bundle.yaml.
  --component <name> Install one component only. Repeatable. Combines with --phase as an AND.
  --context <name>   kubectl/helm context.
  -h, --help         This.

Phases are barriers: every component in a phase is installed before the next phase begins, and
"installed" means helm waited for a `helm` component, and for a `manifest:` component this script
waited for its definitions to be Established and then for what its component.yaml's `waitFor:`
names — a Deployment Available, or a custom resource's phase — with the same 10 m helm gets.

A `manifest:` document is downloaded and run through the `${VAR:=default}` substitution clusterctl
performs before it is applied; a variable in the environment overrides the document's default, and
a variable with neither ends the run naming it.

--phase narrows the run to one phase and --component to one component, so --component is the flag
for repairing a row. A phase is not a row:
USAGE
    roster | awk '{ held[$1]++ } END { for (p in held) print p, held[p] }' \
        | sort -n \
        | awk '{ printf "\n  phase %-3s %2d component%s", $1, $2, ($2 == 1 ? "" : "s") }'
    cat <<'USAGE'


A selector that matches no component is an error, not an empty success.

A component is refused — before anything is applied — when any image its component.yaml records
carries no digest, or when the tag no longer serves the recorded digest. Re-review the move, then
`images.sh --resolve` to re-record it.
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

# recorded <file> — the image references under `images:`, one per line, digest included. Mirrored
# from images.sh for the reason `key` is.
recorded() {
    awk '
        /^images:/ { inside = 1; next }
        /^[A-Za-z]/ { inside = 0 }
        inside && /^  - / { line = $0; sub(/^  - /, "", line); gsub(/^"|"$/, "", line); print line }
    ' "$1"
}

# waits <file> — the `waitFor:` entries, one per line: each is the argument list of one
# `kubectl wait`, minus the timeout this script owns. `<kind>/<name> [-n <namespace>] --for=…`.
# The Bundle gate (build/Build.Bundle.cs § WaitForViolations) requires the block on every
# `manifest:` component and checks each entry's shape; this reader runs it.
waits() {
    awk '
        /^waitFor:/ { inside = 1; next }
        /^[A-Za-z]/ { inside = 0 }
        inside && /^  - / { line = $0; sub(/^  - /, "", line); gsub(/^"|"$/, "", line); print line }
    ' "$1"
}

# Where a `manifest:` document lands between its download and its apply. One directory per run,
# removed on exit whatever the exit is; a file per document, named for the component so that a
# dry run reads as a recipe. Created under --dry-run too — an empty directory is not a command.
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

# ── The digest gate ───────────────────────────────────────────────────────────────────────────
#
# verify_images <dir> — every image the component records is pinned, and its tag still serves the
# pin. Returns 1, having printed why, when the component must not be installed.
#
# ⚠ THREE REFUSALS, AND THE FIRST TWO NEED NO NETWORK. (1) An entry with no `@sha256:<64 hex>` —
# which is how `@unresolved` is spelled when somebody records an image on a machine that cannot reach
# its registry — is a pin nobody has. (2) A component that records no `images:` and does not argue
# in `rendersNoWorkloadImages:` that it renders none is a component whose pulls nobody has looked at.
# Both are checked under --dry-run too, so a dry run over a tree with an unresolved pin is red and
# says which image. (3) The tag resolved today serves a different digest from the one recorded — a
# rebuild upstream, or a moved tag — which is the one that costs a registry round-trip per image and
# is therefore NOT made under --dry-run, whose contract is that it executes nothing and needs no
# network. It is made by --verify and by every real apply.
#
# ⚠ WHY THE APPLY PATH PAYS THE ROUND-TRIPS RATHER THAN TRUSTING A GREEN images.sh. images.sh is a
# script a person runs, and bundle.yaml § owed records that nothing runs it on a schedule; between
# its last green run and this install the tag can move, and `bitnami/kubectl:latest` did so within
# forty-eight hours of being recorded. Checking at the moment of install is the only check whose
# timing is not somebody's memory. It costs about a second per image against the four registries
# this bundle pulls from — measured on 2026-09-15: a full `--verify` over the roster, thirty-two
# resolves plus the pin checks, took 46 s wall-clock, and its first pass found two versioned tags
# rebuilt upstream (clickhouse-operator/component.yaml).
#
# ⚠ IT IS A CHECK AND NOT A PIN, STILL. The kubelet pulls by tag, and between this resolve and that
# pull the tag can move again. What closes that window is a digest in the values the chart renders,
# which bundle.yaml § owed, `images-are-not-pinned-by-digest`, explains most charts cannot carry, or
# admission — docs/plan/18 § Platform security, "verified at admission", which is #15.
verify_images() {
    local dir="$1" file="$1/component.yaml"
    local entry ref pin digest refused=0 count=0

    if [[ -z "$(recorded "$file")" ]]; then
        if [[ -n "$(key "$file" rendersNoWorkloadImages)" ]]; then
            printf '  ✔ %-14s (records no workload image, and says why)\n' images
            return 0
        fi
        printf '  ✘ %-14s component.yaml records no images: block and does not say it renders none.\n' images
        printf '      Nothing here knows what this component would pull. `images.sh --component %s --resolve`.\n' \
            "$(basename "$dir")"
        return 1
    fi

    while read -r entry; do
        [[ -n "$entry" ]] || continue
        count=$((count + 1))
        ref="${entry%%@*}"
        pin="${entry#*@}"
        [[ "$entry" == *@* ]] || pin=""

        if [[ ! "$pin" =~ ^sha256:[0-9a-f]{64}$ ]]; then
            printf '  ✘ %-14s %s is recorded with no digest (`%s`), so there is nothing to hold it to.\n' \
                image "$ref" "${pin:-none}"
            printf '      A pin nobody resolved is not a pin. `images.sh --component %s --resolve` on a machine\n' \
                "$(basename "$dir")"
            printf '      that can reach the registry, review what it found, and record it.\n'
            refused=$((refused + 1))
            continue
        fi

        if [[ "$dry_run" == true ]]; then
            printf '  ✔ %-14s %s@%s (recorded; not resolved under --dry-run)\n' image "$ref" "${pin:0:19}…"
            continue
        fi

        digest=$(digest_of "$ref")

        if [[ -z "$digest" ]]; then
            printf '  ✘ %-14s %s -> the registry serves no manifest for that tag\n' image "$ref"
            refused=$((refused + 1))
        elif [[ "$digest" != "$pin" ]]; then
            printf '  ✘ %-14s %s no longer serves the digest that was reviewed. A tag is mutable;\n' image "$ref"
            printf '      this is the move it exists to catch, and the component is refused rather than installed.\n'
            printf '      recorded %s\n' "$pin"
            printf '      serves   %s\n' "$digest"
            refused=$((refused + 1))
        else
            printf '  ✔ %-14s %s@%s\n' image "$ref" "$digest"
        fi
    done < <(recorded "$file")

    if [[ "$refused" -gt 0 ]]; then
        printf '  ✘ %-14s %d of %d recorded image(s) failed the digest gate; nothing was applied for this component.\n' \
            refused "$refused" "$count"
        return 1
    fi
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

    # ⚠ Images first, and every image, before the artefact pin. The artefact half needs `helm` for a
    # `helm` component and this half needs only curl, so on a machine with no helm the image lines
    # are still real answers rather than lines that never printed. `|| return 1` after the loop and
    # not per image: one moved tag is one refusal, and the reader wants all of them.
    verify_images "$dir" || return 1

    case "$install" in
        helm)
            repo=$(key "$file" repo); chart=$(key "$file" chart); version=$(key "$file" version)
            verify_url index "$repo/index.yaml" || return 1
            # `helm show chart` resolves the entry AND the version, which an index fetch does not.
            # ⚠ A missing helm is reported as a missing helm. Until 2026-09-15 this branch printed
            # "not in <repo>" for every helm component on a machine without helm — eleven false
            # "the pin is gone" lines, indistinguishable from the real one this exists to find.
            if ! command -v helm >/dev/null 2>&1; then
                printf '  ✘ %-14s %s %s could not be checked: `helm` is not on PATH, and the index\n' \
                    chart "$chart" "$version"
                printf '      fetch above does not say whether that version is in it\n'
                return 1
            fi
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
            # ⚠ AND THE DOCUMENT IS FETCHED AND RUN THROUGH THE SUBSTITUTION, because "the pin
            # resolves" said nothing about the four release documents that reached a container
            # as `${CAPI_INSECURE_DIAGNOSTICS:=false}`. A variable with no default and no value
            # is the one refusal substitute.sh makes, and it is answerable with no cluster, so
            # --verify answers it: a bumped pin whose new release introduced a defaultless
            # variable goes red here rather than at phase 40 of a real install.
            local document
            for document in "$manifest" $extra; do
                if ! curl -fsSL --retry 3 --max-time 120 -o "$staging/verify.yaml" "$document"; then
                    printf '  ✘ %-14s %s could not be downloaded for the substitution check\n' variables "$document"
                    return 1
                fi
                if ! substitute_manifest "$staging/verify.yaml" /dev/null; then
                    printf '  ✘ %-14s %s — see the refusal above\n' variables "$document"
                    return 1
                fi
                printf '  ✔ %-14s %s (every ${VAR} has a default or a value)\n' variables "${document##*/}"
            done
            ;;
        file)
            # ⚠ A first-party document has no registry to resolve against, so "does the pin still
            # resolve" reduces to "is the file beside the manifest, and is every document in it an
            # object". The second half is the same narrow awk this script reads component.yaml with:
            # a document is the text between `---` lines, and an object names an `apiVersion:` and
            # a `kind:` at column 0. ⚠ NOT `kubectl apply --dry-run=client`, WHICH WAS THE FIRST
            # SPELLING AND FAILED ON THE FIRST RUN: even with `--validate=false`, kubectl resolves
            # every kind against a server's discovery before it will dry-run anything, so on a
            # machine with no cluster it reports "couldn't get current server API group list" —
            # which reads as a broken file — and `--verify` is the half of this script that must
            # need no cluster at all. The schema check is the API server's, at apply.
            local path count
            path=$(key "$file" file)
            if [[ ! -s "$dir/$path" ]]; then
                printf '  ✘ %-14s %s is missing or empty beside component.yaml\n' file "$path"
                return 1
            fi
            count=$(awk '
                BEGIN { docs = 0; bad = 0 }
                /^---[ \t]*$/ { if (seen) { docs++; if (!(api && kind)) bad++ } api = kind = 0; seen = 0; next }
                /^apiVersion:/ { api = 1 }
                /^kind:/ { kind = 1 }
                /^[^#[:space:]]/ { seen = 1 }
                END { if (seen) { docs++; if (!(api && kind)) bad++ } print docs, bad }
            ' "$dir/$path")
            if [[ "${count#* }" != 0 || "${count% *}" == 0 ]]; then
                printf '  ✘ %-14s %s: %s document(s), %s without an apiVersion and a kind\n' \
                    file "$path" "${count% *}" "${count#* }"
                return 1
            fi
            printf '  ✔ %-14s %s (%s object(s))\n' file "$path" "${count% *}"
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
    local name install repo chart version archive manifest extra crds crdsVersion ns wait_entry wait_args
    name=$(key "$file" component)
    install=$(key "$file" install)
    ns="${name}${namespace_suffix}"

    # ⚠ THE DIGEST GATE IS NOT HERE. It runs in the roster loop, immediately before this function is
    # called, and the placement is bash rather than taste: a function invoked as the condition of an
    # `if` or behind `||` runs with `set -e` switched OFF for its whole body, so putting the gate in
    # here and calling this function in a condition would let a failed `helm upgrade` fall through
    # to the next `run` line. Called plainly, a failing helm or kubectl still ends the run at once.

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
            extra=$(key "$file" manifestExtra)

            # ⚠ THE `waitFor:` BLOCK IS CHECKED BEFORE ANYTHING IS APPLIED, so a component that
            # cannot say what "serving" means for it installs nothing rather than half of itself.
            # The Bundle gate refuses the same file at build time; this is the same rule at the
            # moment it matters, for a tree the gate has not seen.
            if [[ -z "$(waits "$file")" ]]; then
                printf '  ✘ %-14s component.yaml declares no waitFor:, so "installed" would mean "stored" for\n' waitFor
                printf '      this component and the phase after it would be admitted against an operator that may\n'
                printf '      not be running. Name the Deployment or the custom resource that says it serves —\n'
                printf '      charts/bundle/README.md § What a component owes.\n'
                exit 1
            fi

            # ⚠ FETCHED, SUBSTITUTED, THEN APPLIED FROM DISK — NOT `kubectl apply -f <url>`, WHICH IS
            # WHAT THIS LINE WAS UNTIL 2026-09-15 AND WHICH CRASHLOOPED FOUR OF THE SIX. The Cluster
            # API family's release documents are clusterctl templates: `${CAPI_INSECURE_DIAGNOSTICS:=false}`
            # reaches the container verbatim and strconv.ParseBool refuses it. substitute.sh performs
            # the pass clusterctl would have, using the document's own defaults, so the applied bytes
            # are a function of the pin alone. It runs for every manifest component and not only the
            # four that need it today, because "which manifests are templates" is a property of the
            # upstream release, not of this file, and a pass over a document with no variables is the
            # identity — measured over all six on 2026-09-15: kubevirt, containerized-data-importer,
            # cluster-api-provider-kubevirt and rabbitmq-cluster-operator come back byte-identical.
            #
            # ⚠ `--retry 3 -f`: a 5xx from GitHub's release CDN mid-phase is a broken barrier, and
            # curl exits 0 on an HTTP error unless told otherwise. `kubectl apply -f <url>` had
            # neither, so a 503 used to be "error: unable to read URL", which at least failed. This
            # keeps that property.
            #
            # ⚠ `--force-conflicts`, MEASURED RATHER THAN ADDED FOR COMFORT. The second apply of
            # cdi-operator.yaml onto a cluster where CDI already ran — the repair case `--component`
            # exists for — was refused: `Apply failed with 1 conflict: conflict with "cdi-operator"
            # using apiextensions.k8s.io/v1: .spec.versions`. The operator adopts fields of its own
            # definition after it starts, so without this flag a re-run of install.sh fails forever
            # on a healthy cluster, which is the wrong way round: this script is the installer of
            # the document and the operator is free to touch the fields again afterwards. The
            # first apply onto a fresh cluster never conflicts, so the flag changes nothing there.
            run curl -fsSL --retry 3 -o "$staging/$name.yaml" "$manifest"
            run substitute_manifest "$staging/$name.yaml" "$staging/$name.substituted.yaml"
            run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side --force-conflicts \
                -f "$staging/$name.substituted.yaml"

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
            # reaches — containerized-data-importer, phase 30, since the 2026-09-15 reorder — a
            # component that is fine and is not what went wrong; and the bound on a run where every
            # wait is slow but succeeds went from 2 × 5 m to 6 × 5 m. It is ACCEPTED rather than
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
            if [[ -n "$extra" ]]; then
                run curl -fsSL --retry 3 -o "$staging/$name.extra.yaml" "$extra"
                run substitute_manifest "$staging/$name.extra.yaml" "$staging/$name.extra.substituted.yaml"
                run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side --force-conflicts \
                    -f "$staging/$name.extra.substituted.yaml"
            fi

            # ⚠ THE OTHER HALF OF THE BARRIER — #74'S FINDING 2 — AND IT IS PER COMPONENT BECAUSE
            # THE RUN THAT FINALLY HAD A KUBELET SAID SO. "Established" says the API server serves
            # the kind; it says nothing about the operator that reconciles it, and a phase-40
            # provider whose controller has no running pod fails exactly like one whose CRD is
            # missing, only later. Until 2026-09-15 this line was owed rather than written, because
            # no pod of any manifest component had ever run and the obvious spelling was already
            # known to be wrong: the six components put eight Deployments in eight namespaces, none
            # of them `${name}${namespace_suffix}`. Measured on the first run with a kubelet, the
            # wait is THREE SHAPES and not one — a Deployment `Available` for rabbitmq-cluster-operator
            # and the four Cluster API controllers; the `KubeVirt` custom resource's
            # `status.phase == Deployed` for kubevirt, reached about seven minutes after apply while
            # `virt-operator` was Available at 42 s and is NOT the barrier (it goes on to render six
            # more workloads); and the `CDI` resource's `Deployed` for containerized-data-importer,
            # where a Deployment wait would have gone RED during a healthy install on a transient
            # `secret "cdi-api-signing-key" not found` that the operator resolves itself.
            #
            # So the wait is read out of the component's own `waitFor:` block — one `kubectl wait`
            # argument list per entry, `<kind>/<name> [-n <namespace>] --for=…` — and this script
            # supplies the timeout, the same 10 m helm's `--wait` gets. The record is read here, its
            # shape is checked by the Bundle gate, and `BundleInstallSelection` asserts the dry run
            # prints one wait per entry after the apply: all three readers exist, which is what
            # separates this key from the `imageDigest:` that bundle.yaml § owed,
            # `images-are-not-pinned-by-digest`, records nothing ever read.
            #
            # ⚠ `read -r -a` splits the entry on whitespace and nothing else, so an entry cannot
            # carry a quoted argument with a space in it. None does; a jsonpath is written
            # `--for=jsonpath={.status.phase}=Deployed`, braces and all, and reaches kubectl as one
            # argument because no shell ever re-parses it.
            while IFS= read -r wait_entry; do
                [[ -n "$wait_entry" ]] || continue
                read -r -a wait_args <<< "$wait_entry"
                run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} wait --timeout=10m "${wait_args[@]}"
            done < <(waits "$file")
            ;;
        file)
            # A document this repository owns, applied from beside its component.yaml. The only kind
            # whose pin is a path rather than a version, because the artefact is in the same commit
            # as the manifest and has nothing to resolve — charts/bundle/README.md § What a component
            # owes, and cybercloud-admission/component.yaml for why the first one exists.
            #
            # ⚠ NO WAIT, AND THAT IS ARGUED RATHER THAN FORGOTTEN. The one file here is a pair of
            # ValidatingAdmissionPolicies and their bindings. A policy is served from the API
            # server's own informer within seconds of being stored, it defines no kind for a later
            # component to be admitted against, and `kubectl wait` has no condition that names
            # "this policy now refuses". Nothing in the roster depends on it — both bindings select
            # namespaces only the platform creates — so there is no barrier for a wait to hold.
            local path
            path=$(key "$file" file)
            run kubectl ${kubectl_args[@]+"${kubectl_args[@]}"} apply --server-side -f "$dir/$path"
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
            continue
        fi

        # ⚠ THE DIGEST GATE, BEFORE ANY helm OR kubectl LINE AND BEFORE ANY `run`. A refusal here
        # is the whole of what "pinned by digest" means for a chart that cannot carry a digest in
        # its values: the tag the kubelet is about to pull was resolved a moment ago and serves the
        # reviewed bytes. It runs under --dry-run too, where it resolves nothing and refuses only a
        # record with no digest in it.
        #
        # ⚠ A refused component ENDS the run rather than being counted and skipped, because a phase
        # is a barrier: everything after this row in the roster assumes it is installed, and a run
        # that skipped kube-ovn and went on to install eighteen more rows onto a cluster with no CNI
        # would fail everywhere except at the line that says why. A helm or kubectl failure already
        # ends the run under `set -e`; this makes the digest gate end it the same way, with a
        # sentence in front.
        if ! verify_images "$dir"; then
            printf '\n%s (phase %s) was refused by the digest gate above. Nothing was applied for it, and\n' \
                "$component" "$phase" >&2
            printf 'nothing after it in the roster was attempted: a phase is a barrier, and a component\n' >&2
            printf 'the next phase needs is not one this run skips. charts/bundle/README.md § What this bundle pulls.\n' >&2
            exit 1
        fi

        install_component "$dir"
    done < <(printf '%s\n' "$selection")
done

printf '\n'

if [[ "$failures" -gt 0 ]]; then
    printf '%d component(s) failed. Nothing above this line is a claim about a cluster.\n' "$failures" >&2
    exit 1
fi

if [[ "$verify_only" == true ]]; then
    printf 'Every pin resolves, and every recorded image tag serves the digest reviewed for it. That is\n'
    printf 'a claim about registries and about nothing else — no operator was installed and no custom\n'
    printf 'resource was reconciled. charts/bundle/README.md § Verification.\n'
elif [[ "$dry_run" == true ]]; then
    printf 'Dry run. No command above was executed.\n'
else
    printf 'Bundle applied. Run ./charts/bundle/install.sh --verify to re-resolve the pins.\n'
fi
