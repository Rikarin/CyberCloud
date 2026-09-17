#!/usr/bin/env bash
#
# charts/bundle/crds.sh — the REAL CustomResourceDefinition of every operator-owned kind a managed
# chart renders, taken from the release the component pins and committed beside the component as
# charts/bundle/<component>/crds/<plural>.<group>.yaml. charts/bundle/README.md § The definitions
# the harness validates against.
#
# ⚠ WHY THESE FILES EXIST — ISSUE #91. Every reconciler in this repository renders a custom resource
# in C#, and the only API server most of them ever met was FakeKubeCluster, which held whatever it was
# handed. The cluster-backed suite derived a stub CustomResourceDefinition per kind with an OPEN
# schema (`x-kubernetes-preserve-unknown-fields`), so a wrong shape was accepted by construction:
# charts/managed/seaweedfs-bucket rendered `clusterRef` as a string, `versioning` as a boolean and
# `quota` as a string for a month, twenty-eight assertions per run were green, and the real operator's
# schema would have refused all three. The files this script writes are what closes that: the
# harness validates every apply against them (test/CyberCloud.Conformance § StructuralSchema), the
# cluster-backed suite installs them into k3s instead of a stub, and the Definitions gate re-fetches
# the pinned release and fails the build when a committed file no longer matches it.
#
# ⚠ ONLY THE KINDS A CHART RENDERS, NOT EVERY KIND THE COMPONENT SERVES. Strimzi ships ten
# definitions and charts/managed/kafka renders two of them; Cluster API ships twenty-odd and the two
# kubernetes charts render five. Committing the lot would be several megabytes of schema nothing
# reads, which is drift with a version number on it — the objection ClusterConformanceHarness raised
# against vendoring in the first place. The wanted set is DERIVED from charts/managed/*/templates/,
# the same scan the Bundle gate's coverage check performs, so a kind a chart starts rendering is a
# kind this script starts wanting, with no list for anybody to under-declare.
#
# ⚠ THE DOCUMENT IS COMMITTED AS THE RELEASE RENDERS IT, NOT RE-SERIALISED. A round trip through a
# YAML library would reorder keys, requote strings and lose the upstream's comments, and the file
# would then match nothing a person could fetch and diff by hand. What is normalised is the minimum a
# byte comparison needs: the `---` separators, helm's `# Source:` line, leading and trailing blank
# lines, and carriage returns (kube-ovn v1.16.2 emits CRLF). A single implementation of that rule —
# this script — both writes the file and checks it, so the gate compares bytes rather than a second
# reader's opinion of them.
#
# ⚠ `helm template --include-crds` RATHER THAN `helm show crds`, because seven of the fourteen
# components keep their definitions under templates/ behind a values switch (cloudnative-pg,
# mariadb-operator-crds, opensearch-operator, prometheus-operator-crds, seaweedfs-operator,
# victoria-metrics-operator, kube-ovn) and `show crds` reads only the crds/ directory. The render is
# done with the component's own `values:` block, exactly as install.sh installs it, so a definition
# a values key turns off is a definition this bundle does not install — and this script does not
# commit.
#
# Usage:
#   ./charts/bundle/crds.sh                       # check: fetch every pinned release and compare bytes
#   ./charts/bundle/crds.sh --refresh             # (re)write every crds/*.yaml from the pinned release
#   ./charts/bundle/crds.sh --component kube-ovn  # one component. Repeatable
#   ./charts/bundle/crds.sh --wanted              # print the kinds charts render, per component, fetch nothing
#
# Exit codes, which build/Build.Definitions.cs reads:
#   0  every committed definition matches the release its component pins
#   1  at least one differs, is missing, or is committed for a kind no chart renders
#   2  usage
#   3  the release could not be fetched — offline, or no helm/curl — so nothing was compared

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
managed="$(cd "$here/../managed" && pwd)"
mode="check"
only_components=()

usage() {
    cat <<'USAGE'
Usage: crds.sh [options]

  --component <name>  One component only. Repeatable.
  --refresh           Write charts/bundle/<component>/crds/<plural>.<group>.yaml from the pinned release.
  --wanted            Print the operator-owned kinds charts/managed/ renders and which component owns each.
  -h, --help          This.

With no mode, every pinned release is fetched and each committed definition is compared with it byte
for byte. Exit 0 when all match, 1 on any difference, 3 when the release could not be fetched.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --component) only_components+=("$2"); shift 2 ;;
        --refresh) mode="refresh"; shift ;;
        --wanted) mode="wanted"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "crds.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

# ── Reading a component.yaml ──────────────────────────────────────────────────────────────────
#
# ⚠ The same deliberately narrow reader install.sh and images.sh carry, mirrored rather than sourced,
# for the reason images.sh gives: sourcing install.sh runs its argument parser and its roster loop.

key() {
    sed -n "s/^$2: *//p" "$1" | head -1 | tr -d '"'
}

# sequence <file> <key> — the entries of a top-level block sequence, one per line.
sequence() {
    awk -v key="$2" '
        $0 ~ "^" key ":" { inside = 1; next }
        /^[A-Za-z]/ { inside = 0 }
        inside && /^  - / { line = $0; sub(/^  - /, "", line); gsub(/^"|"$/, "", line); print line }
    ' "$1"
}

# helm_sets <file> — every `values:` entry as a --set argument, exactly as install.sh derives them.
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

# ── What the charts render ────────────────────────────────────────────────────────────────────
#
# rendered_kinds — every `apiVersion` + `kind` pair a charts/managed/*/templates/ file declares, as
# `group/version kind chart`, one per line. A text scan of the templates and not a `helm template`,
# for the reason build/Build.Bundle.cs § ReadRenderedApiGroups gives: rendering would miss every
# object behind a conditional the default values switch off, and the bundle has to cover those too.
# The `kind:` taken is the first one at the same indent after an `apiVersion:` line, which is the
# shape every template in the tree uses; a `kind:` inside a spec is indented deeper and is skipped.

rendered_kinds() {
    local chart
    for chart in "$managed"/*/; do
        [[ -d "$chart/templates" ]] || continue
        awk -v chart="$(basename "$chart")" '
            /^[ \t]*-?[ \t]*apiVersion:[ \t]*[^ \t#{]+[ \t]*$/ {
                line = $0
                match(line, /apiVersion:[ \t]*/)
                version = substr(line, RSTART + RLENGTH)
                gsub(/[ \t]+$/, "", version)
                match(line, /^[ \t]*-?[ \t]*/)
                indent = RLENGTH
                waiting = 1
                next
            }
            waiting && /^[ \t]*kind:[ \t]*[^ \t#{]+[ \t]*$/ {
                line = $0
                match(line, /^[ \t]*/)
                if (RLENGTH <= indent) {
                    match(line, /kind:[ \t]*/)
                    kind = substr(line, RSTART + RLENGTH)
                    gsub(/[ \t]+$/, "", kind)
                    if (index(version, "/") > 0) print version, kind, chart
                    waiting = 0
                }
            }
        ' "$chart"/templates/*.yaml "$chart"/templates/*.yml "$chart"/templates/*.tpl 2>/dev/null
    done | sort -u
}

# owner_of <group/version> — the component whose `serves:` names the pair, or nothing for a built-in.
owner_of() {
    local file
    for file in "$here"/*/component.yaml; do
        if sequence "$file" serves | grep -qxF "$1"; then
            basename "$(dirname "$file")"
            return
        fi
    done
}

# wanted — `component group/version kind`, one per line, for every rendered pair a component serves.
wanted() {
    local gv kind chart owner
    while read -r gv kind chart; do
        owner=$(owner_of "$gv")
        [[ -n "$owner" ]] && echo "$owner $gv $kind"
    done < <(rendered_kinds) | sort -u
}

# ── Fetching a component's definitions ────────────────────────────────────────────────────────
#
# render <component.yaml> — the documents the pinned release installs, on stdout. Fails (non-zero,
# nothing on stdout) when the release cannot be fetched, which the caller reads as "offline" rather
# than "different".

render() {
    local file="$1" install repo chart version crds crdsVersion archive manifest extra line
    install=$(key "$file" install)

    local sets=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && sets+=("$line")
    done < <(helm_sets "$file")

    case "$install" in
        helm)
            repo=$(key "$file" repo); chart=$(key "$file" chart); version=$(key "$file" version)
            crds=$(key "$file" chartCrds); crdsVersion=$(key "$file" versionCrds)
            # ⚠ The definitions chart FIRST and the operator chart as well: mariadb-operator keeps its
            # definitions in a sibling chart and nothing else, so the operator render carries none —
            # but the split is a property of that one publisher, and a component that carries both
            # keys and a definition in each is a component this script would otherwise half read.
            if [[ -n "$crds" ]]; then
                helm template x "$crds" --repo "$repo" --version "$crdsVersion" --include-crds || return 1
                printf '\n---\n'
            fi
            helm template x "$chart" --repo "$repo" --version "$version" --include-crds \
                ${sets[@]+"${sets[@]}"} || return 1
            ;;
        helm-archive)
            archive=$(key "$file" archive)
            helm template x "$archive" --include-crds ${sets[@]+"${sets[@]}"} || return 1
            ;;
        manifest)
            manifest=$(key "$file" manifest)
            curl -fsSL --max-time 120 "$manifest" || return 1
            extra=$(key "$file" manifestExtra)
            if [[ -n "$extra" ]]; then
                printf '\n---\n'
                curl -fsSL --max-time 120 "$extra" || return 1
            fi
            ;;
        file)
            cat "$(dirname "$file")/$(key "$file" file)"
            ;;
        *)
            return 1
            ;;
    esac
}

# definitions <render-file> <out-dir> <wanted-pairs-file> — splits the render into documents, keeps
# every CustomResourceDefinition whose `spec.group` and `spec.names.kind` are in the wanted file
# (`group kind` per line), and writes each to <out-dir>/<metadata.name>.yaml normalised as the header
# describes. Prints one `<file>	<group>	<kind>` line per file it wrote.
#
# ⚠ THE NORMALISATION IS THE WHOLE CONTRACT WITH THE GATE, so it is spelled out: carriage returns are
# dropped; `---` lines separate documents; a document's leading lines that are blank or `#` comments
# are dropped (helm's `# Source:` line, a licence header); trailing blank lines are dropped; the file
# ends in exactly one newline. Nothing INSIDE the document moves.
definitions() {
    local render="$1" out="$2" wantedPairs="$3"
    mkdir -p "$out"
    tr -d '\r' < "$render" | awk -v out="$out" -v wantedFile="$wantedPairs" '
        BEGIN {
            while ((getline line < wantedFile) > 0) { want[line] = 1 }
            close(wantedFile)
        }
        function emit() {
            if (crd && ((group " " kind) in want) && name != "") {
                # Drop leading blank and comment lines, and trailing blank lines.
                first = 1; last = n
                while (first <= n && (lines[first] ~ /^[ \t]*$/ || lines[first] ~ /^#/)) first++
                while (last >= first && lines[last] ~ /^[ \t]*$/) last--
                if (first <= last) {
                    path = out "/" name ".yaml"
                    printf "" > path
                    for (i = first; i <= last; i++) print lines[i] >> path
                    close(path)
                    print name ".yaml	" group "	" kind
                }
            }
            n = 0; crd = 0; name = ""; group = ""; kind = ""; section = ""
        }
        # A flow-style value: `metadata: {"name":"…"}`. victoria-metrics-operator renders every
        # definition through toJson, so the three identifiers are read out of one JSON line there
        # and out of block YAML everywhere else. Only the identifiers are read; the bytes are kept.
        function flow(line, keyPattern,   value) {
            if (match(line, keyPattern) == 0) return ""
            value = substr(line, RSTART, RLENGTH)
            sub(/^"[^"]*":"/, "", value); sub(/"$/, "", value)
            return value
        }
        /^---[ \t]*$/ { emit(); next }
        {
            lines[++n] = $0
            if ($0 ~ /^kind:[ \t]*"?CustomResourceDefinition"?[ \t]*$/) crd = 1
            if ($0 ~ /^[A-Za-z]/) { section = $0; sub(/:.*/, "", section); names = 0 }
            if (section == "metadata" && $0 ~ /^metadata:[ \t]*\{/ && name == "") name = flow($0, "\"name\":\"[^\"]*\"")
            if (section == "spec" && $0 ~ /^spec:[ \t]*\{/) {
                group = flow($0, "\"group\":\"[^\"]*\"")
                if (match($0, /"names":\{[^}]*\}/) > 0) kind = flow(substr($0, RSTART, RLENGTH), "\"kind\":\"[^\"]*\"")
            }
            if (section == "metadata" && $0 ~ /^  name:[ \t]*/ && name == "") {
                name = $0; sub(/^  name:[ \t]*/, "", name); gsub(/[ \t"]+$|^"/, "", name)
            }
            if (section == "spec" && $0 ~ /^  group:[ \t]*/) {
                group = $0; sub(/^  group:[ \t]*/, "", group); gsub(/[ \t"]+$|^"/, "", group)
            }
            if (section == "spec" && $0 ~ /^  names:[ \t]*$/) names = 1
            else if (section == "spec" && $0 ~ /^  [A-Za-z]/) names = 0
            if (names && $0 ~ /^    kind:[ \t]*/) {
                kind = $0; sub(/^    kind:[ \t]*/, "", kind); gsub(/[ \t"]+$|^"/, "", kind)
            }
        }
        END { emit() }
    '
}

# ── The roster ────────────────────────────────────────────────────────────────────────────────

selected() {
    local name="$1" wantedName
    [[ ${#only_components[@]} -eq 0 ]] && return 0
    for wantedName in "${only_components[@]}"; do
        [[ "$wantedName" == "$name" ]] && return 0
    done
    return 1
}

if [[ "$mode" == "wanted" ]]; then
    wanted
    exit 0
fi

scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT

wanted > "$scratch/wanted"
status=0
fetched=0
compared=0

for file in "$here"/*/component.yaml; do
    component=$(basename "$(dirname "$file")")
    selected "$component" || continue

    # `component group/version kind` → `group kind`, for this component only.
    awk -v c="$component" '$1 == c { split($2, gv, "/"); print gv[1], $3 }' "$scratch/wanted" | sort -u > "$scratch/pairs"
    committed="$here/$component/crds"

    if [[ ! -s "$scratch/pairs" ]]; then
        # Nothing rendered against this component; a crds/ directory here is a definition nothing reads.
        if [[ -d "$committed" ]] && compgen -G "$committed/*.yaml" > /dev/null; then
            for f in "$committed"/*.yaml; do
                echo "crds.sh: charts/bundle/$component/crds/$(basename "$f") STALE — no chart under charts/managed/ renders a kind this component serves"
                status=1
            done
        fi
        continue
    fi

    if ! render "$file" > "$scratch/render" 2> "$scratch/render.err" || [[ ! -s "$scratch/render" ]]; then
        echo "crds.sh: charts/bundle/$component could not be fetched — $(tr '\n' ' ' < "$scratch/render.err" | cut -c1-300)" >&2
        [[ "$status" -eq 0 ]] && status=3
        continue
    fi
    fetched=$((fetched + 1))

    rm -rf "$scratch/out"
    definitions "$scratch/render" "$scratch/out" "$scratch/pairs" > "$scratch/index"
    cut -f1 "$scratch/index" > "$scratch/written"

    # Every wanted pair has to have produced a file; a pair that did not is a kind the release does
    # not define — the Strimzi-drops-v1beta2 class of finding, one level down from `serves:`.
    while read -r group kind; do
        if ! awk -F'	' -v g="$group" -v k="$kind" '$2 == g && $3 == k { found = 1 } END { exit !found }' "$scratch/index"; then
            echo "crds.sh: charts/bundle/$component ABSENT — the pinned release defines no $kind in $group, and a chart under charts/managed/ renders one"
            status=1
        fi
    done < "$scratch/pairs"

    case "$mode" in
        refresh)
            mkdir -p "$committed"
            # Remove what the release no longer defines, then write what it does.
            for f in "$committed"/*.yaml; do
                [[ -e "$f" ]] || continue
                grep -qxF "$(basename "$f")" "$scratch/written" || { rm -f "$f"; echo "crds.sh: removed charts/bundle/$component/crds/$(basename "$f")"; }
            done
            while read -r written; do
                cp "$scratch/out/$written" "$committed/$written"
                echo "crds.sh: wrote charts/bundle/$component/crds/$written ($(wc -c < "$committed/$written") bytes)"
            done < "$scratch/written"
            ;;
        check)
            while read -r written; do
                compared=$((compared + 1))
                if [[ ! -f "$committed/$written" ]]; then
                    echo "crds.sh: charts/bundle/$component/crds/$written MISSING — the pinned release defines it and a chart renders it; run crds.sh --refresh"
                    status=1
                elif ! tr -d '' < "$committed/$written" | cmp -s "$scratch/out/$written" -; then
                    # ⚠ Carriage returns are dropped from the committed side too, so a checkout with
                    # core.autocrlf=true compares the same bytes a Linux runner does.
                    echo "crds.sh: charts/bundle/$component/crds/$written DIFFERS from the release charts/bundle/$component/component.yaml pins; run crds.sh --refresh and review the diff"
                    status=1
                else
                    echo "crds.sh: charts/bundle/$component/crds/$written matches"
                fi
            done < "$scratch/written"
            for f in "$committed"/*.yaml; do
                [[ -e "$f" ]] || continue
                if ! grep -qxF "$(basename "$f")" "$scratch/written"; then
                    echo "crds.sh: charts/bundle/$component/crds/$(basename "$f") STALE — the pinned release no longer defines it, or no chart renders it; run crds.sh --refresh"
                    status=1
                fi
            done
            ;;
    esac
done

if [[ "$mode" == "check" ]]; then
    echo "crds.sh: $compared definition(s) compared across $fetched fetched component(s)"
    if [[ "$status" -eq 3 ]]; then
        echo "crds.sh: at least one release could not be fetched, so the comparison is incomplete (exit 3)" >&2
    fi
fi

exit "$status"
