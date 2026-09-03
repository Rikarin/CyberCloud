#!/usr/bin/env bash
#
# charts/bundle/images.sh — every container image this bundle would pull, resolved to the digest its
# registry serves today and compared with the digest that was reviewed.
# charts/bundle/README.md § What this bundle pulls.
#
# ⚠ WHY THIS IS NOT install.sh --verify, AND THE DIFFERENCE IS THE WHOLE POINT.
# `--verify` asks "does every pin still resolve" — one HTTP HEAD per chart or manifest URL. It is a
# question about the artefacts THIS DIRECTORY NAMES. This script asks the question one level down:
# what does that artefact PULL, and is it the same bytes as when somebody looked? A chart version is
# immutable once published; the image tag inside it is not, so a component whose every pin resolves
# can still be running an image that was rebuilt last night by somebody else.
#
# ⚠ WHY IT IS NOT install.sh AT ALL. install.sh installs a cluster and is on the critical path of a
# platform build-out. This renders every chart and speaks the registry API to about thirty
# repositories; it takes minutes, it needs no cluster, and nothing it does can change one. Two
# questions, two scripts — the same split `--verify` and the apply path already have.
#
# ⚠ WHAT IT IS NOT: ADMISSION. docs/plan/18 § Platform security asks for images "verified at
# admission", which is #15 and is a policy controller on a cluster. This is the checked-in record
# that such a policy would be written against, and a drift detector over it. A digest recorded here
# and served by nobody is caught the next time this runs; a digest swapped on a cluster is not.
#
# Usage:
#   ./charts/bundle/images.sh                       # compare every component against its record
#   ./charts/bundle/images.sh --component kamaji    # one component. Repeatable
#   ./charts/bundle/images.sh --record              # print the images: block to paste, resolve none
#   ./charts/bundle/images.sh --resolve             # print the images: block WITH digests

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mode="compare"
only_components=()

usage() {
    cat <<'USAGE'
Usage: images.sh [options]

  --component <name>  One component only. Repeatable.
  --record            Print each component's rendered image list and resolve nothing.
  --resolve           Print each component's image list with the digest each tag serves today.
  -h, --help          This.

With no mode, every image is resolved and compared with the `images:` block in its component.yaml.
A tag whose digest has moved, or an image the record does not mention, is a failure.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --component) only_components+=("$2"); shift 2 ;;
        --record) mode="record"; shift ;;
        --resolve) mode="resolve"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "images.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

# ── Reading a component.yaml ──────────────────────────────────────────────────────────────────
#
# ⚠ The same deliberately narrow reader install.sh carries, mirrored rather than sourced. Sourcing
# install.sh would run its argument parser and its roster loop; extracting the reader into a third
# file would make the installer depend on a file that is not on any install path. The format is one
# the Bundle gate already rejects anything outside of.

key() {
    sed -n "s/^$2: *//p" "$1" | head -1 | tr -d '"'
}

# helm_sets <file> — every `values:` entry as a --set argument, exactly as install.sh derives them.
#
# ⚠ WITHOUT THIS THE SCRIPT REPORTS THE WRONG IMAGE, AND redis-operator IS THE PROOF. Its chart's
# default values name `quay.io/spotahome/redis-operator:v1.3.0`, a tag that does not exist — which is
# why the component carries `image.tag: v1.3.0-rc1` in the first place. A render without the
# overrides resolves the broken default and reports the component as unpullable, which is a true
# sentence about the chart and a false one about this bundle.
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

# recorded <file> — the image references under `images:`, one per line, digest included.
recorded() {
    awk '
        /^images:/ { inside = 1; next }
        /^[A-Za-z]/ { inside = 0 }
        inside && /^  - / { line = $0; sub(/^  - /, "", line); gsub(/^"|"$/, "", line); print line }
    ' "$1"
}

# ── Rendering a component ─────────────────────────────────────────────────────────────────────

render() {
    local file="$1" install repo chart version crds crdsVersion archive manifest extra line
    install=$(key "$file" install)

    sets=()
    while IFS= read -r line; do
        [[ -n "$line" ]] && sets+=("$line")
    done < <(helm_sets "$file")

    case "$install" in
        helm)
            repo=$(key "$file" repo); chart=$(key "$file" chart); version=$(key "$file" version)
            crds=$(key "$file" chartCrds); crdsVersion=$(key "$file" versionCrds)
            if [[ -n "$crds" ]]; then
                helm template x "$crds" --repo "$repo" --version "$crdsVersion" 2>/dev/null
                printf '\n---\n'
            fi
            helm template x "$chart" --repo "$repo" --version "$version" \
                ${sets[@]+"${sets[@]}"} 2>/dev/null
            ;;
        helm-archive)
            archive=$(key "$file" archive)
            helm template x "$archive" ${sets[@]+"${sets[@]}"} 2>/dev/null
            ;;
        manifest)
            manifest=$(key "$file" manifest)
            curl -sSL --max-time 120 "$manifest"
            extra=$(key "$file" manifestExtra)
            if [[ -n "$extra" ]]; then
                printf '\n---\n'
                curl -sSL --max-time 120 "$extra"
            fi
            ;;
    esac
}

# ── Which `image:` lines count ────────────────────────────────────────────────────────────────
#
# ⚠ A CustomResourceDefinition's schema is DROPPED, and getting that wrong is the difference between
# a fact and a scare. The Kamaji control-plane provider's CRD carries
# `default: registry.k8s.io/kas-network-proxy/proxy-agent` — no tag, so `latest` — inside the
# OpenAPI schema for a KamajiControlPlane's konnectivity block. It is a real hazard and it is not an
# image this component pulls: nothing runs until a tenant creates a control plane that asks for
# konnectivity. Counting it here would have reported two untagged images in the bundle's own install,
# which is false. See charts/bundle/README.md § What this bundle pulls.
#
# ⚠ `tr -d '\r'` IS NOT DEFENSIVE PROGRAMMING, IT IS kube-ovn. Chart v1.16.2 emits its
# `vpc-nat-gateway` image line with a CRLF ending, so the reference reaches curl with a carriage
# return glued to the tag and the request fails with "Malformed input to a URL function" — which
# reads exactly like a tag that does not exist.

workload_images() {
    awk '
        function emit() {
            if (!crd && doc != "") printf "%s", doc
            doc = ""; crd = 0
        }
        /^---[ \t]*$/ { emit(); next }
        {
            doc = doc $0 "\n"
            if ($0 ~ /^kind:[ \t]*CustomResourceDefinition[ \t]*$/) crd = 1
        }
        END { emit() }
    ' \
    | sed -n 's/^[ \t]*-\{0,1\}[ \t]*image:[ \t]*//p' \
    | tr -d '"\r' \
    | grep -v '{{' \
    | grep '/' \
    | sort -u
}

# ── Resolving a tag to the digest a registry serves ───────────────────────────────────────────
#
# ⚠ curl and nothing else, for the reason install.sh gives about yq: the machine that has to answer
# "what is this bundle about to pull" is frequently the machine that has nothing installed. This is
# the OCI distribution token dance — an unauthenticated request for the challenge, a token from the
# realm it names, then a HEAD whose Docker-Content-Digest header is the answer. Exercised firsthand
# on 2026-09-03 against quay.io, ghcr.io, registry.k8s.io and docker.io.
#
# ⚠ `-L` on both requests, and it is not decoration: registry.k8s.io answers 307 and a resolver
# without it reports every Kubernetes-hosted image as unresolvable — which reads as a broken pin.

accept='application/vnd.oci.image.index.v1+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.docker.distribution.manifest.v2+json'

digest_of() {
    local ref="$1" registry repo tag host challenge realm service scope token headers digest

    ref="${ref%%@*}"

    if [[ "${ref##*/}" == *:* ]]; then
        tag="${ref##*:}"
        repo="${ref%:*}"
    else
        tag="latest"
        repo="$ref"
    fi

    if [[ "${repo%%/*}" == *.* || "${repo%%/*}" == "localhost" ]]; then
        registry="${repo%%/*}"
        repo="${repo#*/}"
    else
        registry="docker.io"
    fi

    host="$registry"
    if [[ "$registry" == "docker.io" ]]; then
        host="registry-1.docker.io"
        [[ "$repo" == */* ]] || repo="library/$repo"
    fi

    challenge=$(curl -sSL -o /dev/null -D - --max-time 60 "https://$host/v2/$repo/manifests/$tag" \
        | tr -d '\r' | sed -n 's/^[Ww][Ww][Ww]-[Aa]uthenticate: *//p' | head -1)

    token=""
    if [[ "$challenge" == Bearer* ]]; then
        realm=$(printf '%s' "$challenge" | sed -n 's/.*realm="\([^"]*\)".*/\1/p')
        service=$(printf '%s' "$challenge" | sed -n 's/.*service="\([^"]*\)".*/\1/p')
        scope=$(printf '%s' "$challenge" | sed -n 's/.*scope="\([^"]*\)".*/\1/p')
        [[ -n "$scope" ]] || scope="repository:$repo:pull"
        token=$(curl -sS --max-time 60 "$realm?service=$service&scope=$scope" \
            | sed -n 's/.*"token"[: ]*"\([^"]*\)".*/\1/p')
    fi

    headers=$(curl -sSL -o /dev/null -D - --max-time 60 -H "Accept: $accept" \
        ${token:+-H "Authorization: Bearer $token"} \
        "https://$host/v2/$repo/manifests/$tag" | tr -d '\r')

    digest=$(printf '%s' "$headers" | sed -n 's/^[Dd]ocker-[Cc]ontent-[Dd]igest: *//p' | head -1)
    printf '%s' "$digest"
}

# ── The roster ────────────────────────────────────────────────────────────────────────────────

roster() {
    awk '
        /^components:/ { inside = 1; next }
        /^[a-z]/ && !/^components:/ { inside = 0 }
        inside && /^  - name:/ { print $3 }
    ' "$here/bundle.yaml"
}

selected() {
    local component wanted found
    while read -r component; do
        if [[ ${#only_components[@]} -gt 0 ]]; then
            found=false
            for wanted in ${only_components[@]+"${only_components[@]}"}; do
                [[ "$wanted" == "$component" ]] && found=true
            done
            [[ "$found" == true ]] || continue
        fi
        printf '%s\n' "$component"
    done < <(roster)
}

for wanted in ${only_components[@]+"${only_components[@]}"}; do
    if ! roster | grep -qx -- "$wanted"; then
        echo "images.sh: --component $wanted is not in bundle.yaml." >&2
        exit 2
    fi
done

selection=$(selected)

if [[ -z "$selection" ]]; then
    echo "images.sh: --component selected no component of the $(roster | wc -l | tr -d ' ') in bundle.yaml." >&2
    exit 2
fi

failures=0
images_seen=0

while read -r component; do
    file="$here/$component/component.yaml"

    if [[ ! -f "$file" ]]; then
        printf '✘ %s has no component.yaml\n' "$component"
        failures=$((failures + 1))
        continue
    fi

    printf '\n%s\n' "$component"

    rendered=$(render "$file" | workload_images)

    if [[ -z "$rendered" ]]; then
        # ⚠ Not a failure by itself: prometheus-operator-crds installs definitions and no workload,
        # so it renders no image and its component.yaml says so. It IS a failure when the record
        # claims otherwise, which the comparison below catches from the other side.
        printf '  (renders no workload image)\n'
    fi

    while read -r image; do
        [[ -n "$image" ]] || continue
        images_seen=$((images_seen + 1))

        if [[ "$mode" == "record" ]]; then
            printf '  - %s\n' "$image"
            continue
        fi

        digest=$(digest_of "$image")

        if [[ -z "$digest" ]]; then
            printf '  ✘ %s -> the registry serves no manifest for that tag\n' "$image"
            failures=$((failures + 1))
            continue
        fi

        if [[ "$mode" == "resolve" ]]; then
            printf '  - %s@%s\n' "$image" "$digest"
            continue
        fi

        if recorded "$file" | grep -qx -- "$image@$digest"; then
            printf '  ✔ %s@%s\n' "$image" "$digest"
        elif recorded "$file" | grep -q "^${image}@"; then
            printf '  ✘ %s\n' "$image"
            printf '      recorded %s\n' "$(recorded "$file" | grep "^${image}@" | head -1 | sed 's/.*@//')"
            printf '      serves   %s\n' "$digest"
            failures=$((failures + 1))
        else
            printf '  ✘ %s@%s is not in this component.yaml images: block\n' "$image" "$digest"
            failures=$((failures + 1))
        fi
    done < <(printf '%s\n' "$rendered")

    if [[ "$mode" == "compare" ]]; then
        while read -r entry; do
            [[ -n "$entry" ]] || continue
            if ! printf '%s\n' "$rendered" | grep -qx -- "${entry%%@*}"; then
                printf '  ✘ %s is recorded and nothing renders it any more\n' "$entry"
                failures=$((failures + 1))
            fi
        done < <(recorded "$file")
    fi
done < <(printf '%s\n' "$selection")

printf '\n'

if [[ "$failures" -gt 0 ]]; then
    printf '%d image(s) disagree with the record. A tag is mutable; that is what this catches.\n' "$failures" >&2
    printf 'Re-review, then `images.sh --resolve` to regenerate the block.\n' >&2
    exit 1
fi

case "$mode" in
    record|resolve) printf 'Rendered %d image(s). Nothing was compared.\n' "$images_seen" ;;
    *) printf '%d image(s) serve the digest their component.yaml records. That is a claim about\n' "$images_seen"
       printf 'registries today and about no cluster — charts/bundle/README.md § What this bundle pulls.\n' ;;
esac
