#!/usr/bin/env bash
#
# node-image/build.sh — build the container disk a worker VM boots, because upstream does not.
# charts/managed/kubernetes-agentpool/node-image/README.md.
#
# ⚠ WHY THIS EXISTS. charts/managed/kubernetes/conformance.yaml § owed,
# `offered-minors-are-out-of-support`: `quay.io/capk/ubuntu-2404-container-disk` publishes one tag
# per minor and its newest is v1.34.1, pushed 2025-09-27. Re-read on 2026-09-03 through the quay tag
# API: four tags, `has_additional: false`, nothing newer. Cluster API Provider KubeVirt automates no
# container-disk build at all — its image workflow pushes manager images — so the two minors worth an
# api-version, 1.35 and 1.36, have no bootable node image anywhere under that namespace and never
# will unless somebody builds one.
#
# ⚠ THE DEFAULT MODE RESOLVES INPUTS AND BUILDS NOTHING, and that is the opposite of install.sh on
# purpose. A build is half an hour of packer, a QEMU-capable host and about 4 GB of disk; a run that
# started one because somebody typed the script's name would be a bad trade. `--build` is the flag
# that means it.

set -euo pipefail

# ── The pin ───────────────────────────────────────────────────────────────────────────────────
#
# ⚠ A RELEASE TAG AND NOT `main`, for the reason every other pin in this repository is one: `main`
# is a moving target and "the recipe worked last month" is not a claim anybody can act on. Read at
# this tag on 2026-09-03: packer/config/kubernetes.json carries kubernetes_semver v1.36.1,
# kubernetes_series v1.36 and kubernetes_deb_version 1.36.1-1.1, and the Makefile carries
# build-kubevirt-qemu-ubuntu-2404. `--verify` re-reads all of that rather than trusting this comment.

image_builder_ref="v0.1.55"
flavour="qemu-ubuntu-2404"

# ⚠ EMPTY, AND IT IS THE ITEM THIS SCRIPT CANNOT CLOSE. There is nowhere to push. See README
# § Hosting is owed, and issue #25.
repository=""

minor=""
patch=""
do_build=false
do_push=false
workdir="${TMPDIR:-/tmp}/cybercloud-node-image"

usage() {
    cat <<'USAGE'
Usage: build.sh [options]

  --ref <tag>          kubernetes-sigs/image-builder ref. Default: the pin in this script.
  --patch <vX.Y.Z>     Exact Kubernetes patch. Default: whatever the pinned ref defaults to.
  --repository <ref>   Registry repository the container disk is tagged for. No default.
  --build              Actually run packer. Needs packer, qemu, ansible, python3 and docker.
  --push               Push after building. Needs --repository and credentials.
  --workdir <path>     Where image-builder is cloned. Default: $TMPDIR/cybercloud-node-image.
  -h, --help           This.

With no --build, every input is resolved against GitHub and the registry and nothing is built.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --ref) image_builder_ref="$2"; shift 2 ;;
        --patch) patch="$2"; shift 2 ;;
        --repository) repository="$2"; shift 2 ;;
        --build) do_build=true; shift ;;
        --push) do_push=true; shift ;;
        --workdir) workdir="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "build.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

raw="https://raw.githubusercontent.com/kubernetes-sigs/image-builder/$image_builder_ref/images/capi"
target="build-kubevirt-$flavour"

# ── Resolving the recipe's inputs ─────────────────────────────────────────────────────────────

fetch() {
    curl -sSfL --max-time 60 "$1"
}

printf 'image-builder %s\n' "$image_builder_ref"

# ⚠ `|| true`, and it is not sloppiness. Under `set -e` a failing command substitution ends the
# script where it stands, so a ref that does not exist would print curl's "error: 422" and nothing
# else — the one diagnosis the reader cannot derive is which of the inputs was wrong, and that is
# what the branch below says.
sha=$(curl -sSfL --max-time 60 \
    "https://api.github.com/repos/kubernetes-sigs/image-builder/commits/$image_builder_ref" \
    2>/dev/null | sed -n 's/.*"sha"[: ]*"\([0-9a-f]\{40\}\)".*/\1/p' | head -1 || true)

if [[ -z "$sha" ]]; then
    echo "  ✘ ref $image_builder_ref does not resolve in kubernetes-sigs/image-builder. A recipe" >&2
    echo "    pinned to a tag that does not exist is the failure class this repository has shipped" >&2
    echo "    five times. (An unauthenticated GitHub API is also rate-limited at 60 requests an" >&2
    echo "    hour; if the tag is real, that is the other explanation.)" >&2
    exit 1
fi

printf '  ✔ %-22s %s\n' ref "$sha"

makefile=$(fetch "$raw/Makefile") || { echo "  ✘ no Makefile at that ref" >&2; exit 1; }

# ⚠ BASH PATTERN MATCHING AND NOT `printf … | grep -q`, AND THE REASON WAS MEASURED HERE RATHER THAN
# READ SOMEWHERE. `grep -q` exits at the first match; this Makefile is 74 KB and the match is a
# third of the way in, so the printf feeding it is killed by SIGPIPE and exits 141 — and under
# `set -o pipefail` that becomes the PIPELINE's status. The check then reports "that ref's Makefile
# has no KubeVirt build targets" about a Makefile that has three, INTERMITTENTLY, depending on
# whether the writer had already flushed into the pipe buffer. Two runs of this script a minute
# apart disagreed. It is the exact failure class this repository keeps finding — a check that
# answers a different question from the one it appears to — arriving through the shell rather than
# through the logic.
if [[ "$makefile" != *QEMU_KUBEVIRT_BUILD_TARGETS* ]]; then
    echo "  ✘ that ref's Makefile has no KubeVirt build targets, so this recipe does not apply" >&2
    echo "    to it. See README § What upstream does and does not automate." >&2
    exit 1
fi

build_names=$(printf '%s\n' "$makefile" | sed -n 's/^QEMU_BUILD_NAMES[^=]*=[ \t]*//p' | head -1)

if [[ "$build_names" != *"$flavour"* ]]; then
    echo "  ✘ that ref's QEMU_BUILD_NAMES does not list $flavour, so $target does not exist." >&2
    echo "    It lists: $build_names" >&2
    exit 1
fi

printf '  ✔ %-22s %s\n' target "$target"

config=$(fetch "$raw/packer/config/kubernetes.json")
default_semver=$(printf '%s' "$config" | sed -n 's/.*"kubernetes_semver"[: ]*"\([^"]*\)".*/\1/p')
default_series=$(printf '%s' "$config" | sed -n 's/.*"kubernetes_series"[: ]*"\([^"]*\)".*/\1/p')
default_deb=$(printf '%s' "$config" | sed -n 's/.*"kubernetes_deb_version"[: ]*"\([^"]*\)".*/\1/p')

printf '  ✔ %-22s %s (series %s, deb %s)\n' 'default kubernetes' \
    "$default_semver" "$default_series" "$default_deb"

if [[ -z "$patch" ]]; then
    patch="$default_semver"
    override=false
else
    override=true
fi

minor="${patch%.*}"
deb="${patch#v}-1.1"

printf '  → %-22s %s (series %s, deb %s)\n' 'building' "$patch" "$minor" "$deb"

# ⚠ THE OVERRIDE IS THREE VALUES AND NOT ONE, AND GETTING THAT WRONG PRODUCES A NODE WHOSE KUBELET
# IS NOT THE VERSION ITS TAG CLAIMS. `kubernetes_semver` is what the image records, `kubernetes_series`
# selects the pkgs.k8s.io repository, and `kubernetes_deb_version` is what apt installs. Setting only
# the first is a silent mismatch — the tag says one minor and the binaries are another — which is a
# worse outcome than the missing image this script exists to fix.
if [[ "$override" == true ]]; then
    printf '  ⚠ %-22s overriding the pinned ref default; all three of semver, series and\n' 'not the default'
    printf '    %-22s deb version are set together.\n' ''
fi

# ── Does the answer already exist ─────────────────────────────────────────────────────────────

if [[ -n "$repository" ]]; then
    code=$(curl -sSL -o /dev/null -w '%{http_code}' --max-time 60 \
        "https://${repository%%/*}/v2/${repository#*/}/manifests/$patch" || echo 000)
    printf '  · %-22s %s:%s -> HTTP %s (401 means it exists and needs a token)\n' \
        'registry' "$repository" "$patch" "$code"
else
    printf '  ✘ %-22s no --repository. THE IMAGE HAS NOWHERE TO GO — README § Hosting is owed.\n' 'hosting'
fi

# ── Tools ─────────────────────────────────────────────────────────────────────────────────────

missing=()
for tool in git make packer python3 ansible-playbook qemu-system-x86_64 docker; do
    command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done

if [[ ${#missing[@]} -gt 0 ]]; then
    printf '  · %-22s %s\n' 'not on PATH' "${missing[*]}"
fi

if [[ "$do_build" != true ]]; then
    printf '\nInputs resolve. Nothing was built — pass --build for that, and read\n'
    printf 'charts/managed/kubernetes-agentpool/node-image/README.md first.\n'
    exit 0
fi

if [[ ${#missing[@]} -gt 0 ]]; then
    echo "build.sh: --build needs ${missing[*]} on PATH." >&2
    exit 1
fi

# ── The build ─────────────────────────────────────────────────────────────────────────────────
#
# ⚠ THIS PATH HAS NOT BEEN RUN IN THIS REPOSITORY, and saying so is the point of the line rather
# than a disclaimer. Every input above is resolved firsthand on every invocation; the packer run
# needs a QEMU-capable host, roughly half an hour and about 4 GB, and no CI lane here has one. The
# steps below are transcribed from the pinned ref's own Makefile and post-processor, not invented.

mkdir -p "$workdir"

if [[ ! -d "$workdir/image-builder/.git" ]]; then
    git clone --depth 1 --branch "$image_builder_ref" \
        https://github.com/kubernetes-sigs/image-builder.git "$workdir/image-builder"
fi

cat > "$workdir/kubernetes.json" <<JSON
{
  "kubernetes_semver": "$patch",
  "kubernetes_series": "$minor",
  "kubernetes_deb_version": "$deb"
}
JSON

# ⚠ PACKER_VAR_FILES is image-builder's own override mechanism — Makefile § ABSOLUTE_PACKER_VAR_FILES
# folds it into every packer invocation. Editing packer/config/kubernetes.json in the clone would
# work once and would be lost by the next `git clone --depth 1`.
(
    cd "$workdir/image-builder/images/capi"
    PACKER_VAR_FILES="$workdir/kubernetes.json" make "$target"
)

# The post-processor builds and tags `<build_name>-container-disk` locally — see
# packer/qemu/scripts/build_kubevirt_image.sh at the pinned ref, which writes a two-stage Dockerfile
# whose second stage is FROM scratch with the qcow2 at /disk/. That is the whole container-disk
# format; there is no KubeVirt-specific tooling in it.
local_tag="$flavour-container-disk"

printf '\nBuilt %s\n' "$local_tag"

if [[ -z "$repository" ]]; then
    printf '⚠ No --repository, so the disk exists on this machine and nowhere else. A container disk\n'
    printf '  that is not in a registry is not a node image — README § Hosting is owed.\n'
    exit 0
fi

docker tag "$local_tag" "$repository:$patch"
printf 'Tagged %s:%s\n' "$repository" "$patch"

if [[ "$do_push" == true ]]; then
    docker push "$repository:$patch"
    printf 'Pushed %s:%s\n' "$repository" "$patch"
    printf '⚠ Then, and not before, a new api-version may offer the minor — see\n'
    printf '  charts/managed/kubernetes/conformance.yaml § owed, offered-minors-are-out-of-support.\n'
fi
