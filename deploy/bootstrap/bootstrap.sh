#!/usr/bin/env bash
#
# bootstrap.sh — everything that has to exist on a cluster before `helm install cybercloud` can work.
#
# ═══════════════════════════════════════════════════════════════════════════════════════════════
#  This is the script docs/plan/09 § The platform's own cluster promises "remains supported and
#  tested forever": what an operator runs to repair or reinstall the platform WITH NO PLATFORM
#  RUNNING. It therefore uses kubectl and nothing else — no `cyc`, no portal, no Orleans client, no
#  API that a platform outage takes with it.
# ═══════════════════════════════════════════════════════════════════════════════════════════════
#
# Usage:
#   ./deploy/bootstrap/bootstrap.sh --image <ref> --shards <file> [--namespace cybercloud]
#                                   [--context <kubectl-context>] [--dry-run]
#
# It is safe to run again. Every step is either a `kubectl apply` (converges) or a delete-then-create
# (the schema Job, whose `spec.template` is immutable once it exists). `deploy/README.md`
# § Idempotence has the layer-by-layer argument.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR

NAMESPACE="cybercloud"
IMAGE=""
SHARDS=""
CONTEXT=""
DRY_RUN="false"
# Long enough for a Postgres failover mid-run plus the Job's four attempts, short enough that a wrong
# password is a red job rather than a coffee break. `activeDeadlineSeconds` in the Job is the other
# half of this number.
SCHEMA_TIMEOUT_SECONDS=900

die() {
    printf '\nbootstrap: %s\n' "$1" >&2
    exit 1
}

step() {
    printf '\n── %s\n' "$1"
}

usage() {
    # The header comment, lines 3 to 18 — down to `set -euo pipefail`. One place to edit, not two.
    sed -n '3,18p' "${BASH_SOURCE[0]}"
    exit "${1:-0}"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --image) IMAGE="${2:-}"; shift 2 ;;
        --shards) SHARDS="${2:-}"; shift 2 ;;
        --namespace) NAMESPACE="${2:-}"; shift 2 ;;
        --context) CONTEXT="${2:-}"; shift 2 ;;
        --dry-run) DRY_RUN="true"; shift ;;
        -h|--help) usage 0 ;;
        *) die "unknown argument '$1'. Run with --help." ;;
    esac
done

# ── Preflight ──────────────────────────────────────────────────────────────────────────────────
#
# ⚠ EVERY CHECK HERE IS ONE AN OPERATOR WOULD OTHERWISE MAKE HALFWAY THROUGH, UNDER PRESSURE, FROM AN
# ERROR MESSAGE THAT NAMES SOMETHING ELSE. A missing `--image` discovered at step 4 has already
# created a namespace and a Role.

command -v kubectl >/dev/null 2>&1 || die "kubectl is not on PATH. This script deliberately needs nothing else."

[ -n "$IMAGE" ] || die "--image is required: the silo image the schema job runs. Prefer a digest — docs/plan/23 § Build."
[ -n "$SHARDS" ] || die "--shards is required: an env file of durable shard connection strings. See shards.env.example."
[ -f "$SHARDS" ] || die "--shards file '$SHARDS' does not exist."

case "$IMAGE" in
    *@sha256:*) : ;;
    *) printf 'bootstrap: ⚠ "%s" is a tag, not a digest. docs/plan/23 § Build pushes by digest, and a\n' "$IMAGE" >&2
       printf '           repair that came up on a different build than the one that broke is not a repair.\n' >&2 ;;
esac

# ⚠ `if`, not `[ … ] && …`. Under `set -e` a bare `test && action` that takes the false branch is a
# non-zero last command and kills the script — silently, at the point it was trying to be helpful.
KUBECTL=(kubectl)
if [ -n "$CONTEXT" ]; then
    KUBECTL+=(--context "$CONTEXT")
fi

"${KUBECTL[@]}" version -o yaml >/dev/null 2>&1 || die "cannot reach a cluster. Check your kubeconfig and --context."

# CRDs are cluster-scoped, so this needs rights no Cyber Cloud ServiceAccount has and the operator
# does. Better to say so now than after the namespace exists.
CAN_CREATE_CRDS="$("${KUBECTL[@]}" auth can-i create customresourcedefinitions.apiextensions.k8s.io 2>/dev/null || true)"

if [ "$CAN_CREATE_CRDS" != "yes" ]; then
    die "you cannot create CustomResourceDefinitions on this cluster. Orleans membership needs
     silos.orleans.dot.net and clusterversions.orleans.dot.net, nothing creates them at runtime
     (docs/plan/02 § ADR-004), and they are cluster-scoped. Bootstrap is an operator's job."
fi

# One check for what the schema job cannot recover from: an empty shard file. The job's own guard
# catches it too and exits 1 with a good message, but that is a pod start-up and a red Job later.
if ! grep -qE '^CyberCloud__Storage__Durable__Shards__[A-Za-z0-9_]+=' "$SHARDS"; then
    die "'$SHARDS' has no CyberCloud__Storage__Durable__Shards__<id>= line. A silo started against
     that configuration has no durable tier at all. See shards.env.example."
fi

APPLY=("${KUBECTL[@]}" apply)
if [ "$DRY_RUN" = "true" ]; then
    APPLY+=(--dry-run=client)
fi

printf 'bootstrap: namespace=%s image=%s shards=%s dry-run=%s\n' "$NAMESPACE" "$IMAGE" "$SHARDS" "$DRY_RUN"

# ── Rendering ──────────────────────────────────────────────────────────────────────────────────
#
# Two substitutions, both of them things that genuinely cannot be a literal in a checked-in file: the
# image, and — only when the operator overrides it — the namespace in the three places RBAC
# validation forces it to appear. Everything else is applied byte-for-byte as it is committed, which
# is what lets a reviewer read the manifests instead of a template.
RENDERED="$(mktemp -d)"
trap 'rm -rf "$RENDERED"' EXIT

for manifest in "$SCRIPT_DIR"/[0-9][0-9]-*.yaml; do
    sed \
        -e "s|CYBERCLOUD_IMAGE_PLACEHOLDER|${IMAGE}|g" \
        -e "s|^  name: cybercloud\$|  name: ${NAMESPACE}|" \
        -e "s|^    namespace: cybercloud\$|    namespace: ${NAMESPACE}|" \
        "$manifest" > "$RENDERED/$(basename "$manifest")"
done

# ── 1. The namespace ───────────────────────────────────────────────────────────────────────────
step "1/5  namespace ${NAMESPACE}"
"${APPLY[@]}" -f "$RENDERED/00-namespace.yaml"

# ── 2. The Orleans membership CRDs ─────────────────────────────────────────────────────────────
step "2/5  Orleans membership CRDs (cluster-scoped)"
# ⚠ `--server-side`. CRDs are the one kind here where client-side apply's
# `last-applied-configuration` annotation is a real hazard: it stores the whole schema, and a schema
# that grows past 262 144 bytes turns a working `apply` into a hard failure on a later, unrelated
# edit. These two are tiny today. The habit is what matters, because the fix after the fact is
# `kubectl replace`.
#
# ⚠ AND `--dry-run=server` RATHER THAN THE `--dry-run=client` IN "${APPLY[@]}", ON THIS STEP ONLY.
# kubectl refuses the pair outright — "error: --dry-run=client doesn't work with --server-side" —
# so with --dry-run this line did not perform a weaker check, it exited 1 and took the whole script
# with it. Every nightly cluster-e2e and all four hostile-byo legs died here, three steps into a
# dry run they had built a cluster for. Server-side apply's dry run is the coherent partner
# anyway: it is the same admission path the real apply takes, minus the write.
CRD_APPLY=("${APPLY[@]}")
if [ "$DRY_RUN" = "true" ]; then
    CRD_APPLY=("${KUBECTL[@]}" apply --dry-run=server)
fi
"${CRD_APPLY[@]}" --server-side --field-manager=cybercloud-bootstrap -f "$RENDERED/10-orleans-crds.yaml"

if [ "$DRY_RUN" != "true" ]; then
    # Established, not merely created: the API server has to accept `orleans.dot.net/v1` before a silo
    # can write to it, and there is a window between the two in which a create 404s.
    "${KUBECTL[@]}" wait --for=condition=Established --timeout=60s \
        crd/silos.orleans.dot.net crd/clusterversions.orleans.dot.net
fi

# ── 3. RBAC ────────────────────────────────────────────────────────────────────────────────────
step "3/5  service accounts, roles and bindings"
"${APPLY[@]}" -n "$NAMESPACE" -f "$RENDERED/20-rbac.yaml"

# ── 4. The shard secret ────────────────────────────────────────────────────────────────────────
step "4/5  secret cybercloud-durable-shards"
# ⚠ `create --dry-run=client -o yaml | apply -f -` IS THE IDEMPOTENT SPELLING, AND `kubectl create
# secret` ALONE IS NOT — it fails with AlreadyExists the second time, which is precisely the run an
# operator makes when rotating a password during an incident. The pipe re-renders the whole Secret and
# lets apply converge it.
#
# ⚠ THE SHARD SET IS DEFINED HERE, ONCE, FOR BOTH THE JOB AND THE SILOS. That identity is what makes
# "no shard the silos know about was missed by the job" structural instead of procedural — see
# `deploy/README.md` § The shard set is one object.
if [ "$DRY_RUN" = "true" ]; then
    "${KUBECTL[@]}" create secret generic cybercloud-durable-shards \
        --from-env-file="$SHARDS" --dry-run=client -o yaml >/dev/null
    printf '   (dry run — secret rendered, not applied)\n'
else
    "${KUBECTL[@]}" create secret generic cybercloud-durable-shards \
        -n "$NAMESPACE" --from-env-file="$SHARDS" --dry-run=client -o yaml \
        | "${KUBECTL[@]}" apply -n "$NAMESPACE" -f -
fi

# ── 5. The durable schema ──────────────────────────────────────────────────────────────────────
step "5/5  durable schema job"

if [ "$DRY_RUN" = "true" ]; then
    "${APPLY[@]}" -n "$NAMESPACE" -f "$RENDERED/30-durable-schema-job.yaml"
    printf '\nbootstrap: dry run complete. Nothing was written to the cluster.\n'
    exit 0
fi

# ⚠ DELETE FIRST. A Job's `spec.template` is immutable, so re-applying this one with a new image fails
# with `field is immutable` — the error that makes a second bootstrap run look like a broken script
# rather than a converging one. `--wait` matters: without it the create races the delete and fails
# with AlreadyExists instead.
"${KUBECTL[@]}" delete job cybercloud-durable-schema -n "$NAMESPACE" --ignore-not-found --wait=true

"${KUBECTL[@]}" create -n "$NAMESPACE" -f "$RENDERED/30-durable-schema-job.yaml"

# ⚠ NOT `kubectl wait --for=condition=complete`. That waits out the full timeout on a job that has
# already failed, so a wrong password costs fifteen minutes of watching a spinner instead of the forty
# seconds it took to be certain. Polling both conditions is the difference.
printf '   waiting for the schema job (up to %ss)…\n' "$SCHEMA_TIMEOUT_SECONDS"
deadline=$(( $(date +%s) + SCHEMA_TIMEOUT_SECONDS ))

while :; do
    succeeded="$("${KUBECTL[@]}" get job cybercloud-durable-schema -n "$NAMESPACE" -o jsonpath='{.status.succeeded}' 2>/dev/null || true)"
    failed="$("${KUBECTL[@]}" get job cybercloud-durable-schema -n "$NAMESPACE" -o jsonpath='{.status.conditions[?(@.type=="Failed")].status}' 2>/dev/null || true)"

    if [ "${succeeded:-0}" = "1" ]; then
        break
    fi

    if [ "$failed" = "True" ]; then
        printf '\nbootstrap: the durable schema job FAILED. Its log names the shard:\n\n' >&2
        "${KUBECTL[@]}" logs job/cybercloud-durable-schema -n "$NAMESPACE" --all-containers --tail=100 >&2 || true
        die "durable schema not applied. Nothing else was installed, and the shards that did succeed
     stay succeeded — a re-run resumes rather than restarts. deploy/README.md § When a shard fails."
    fi

    [ "$(date +%s)" -lt "$deadline" ] || die "the durable schema job did not finish within ${SCHEMA_TIMEOUT_SECONDS}s.
     \`kubectl -n ${NAMESPACE} describe job cybercloud-durable-schema\` and its pod events say why;
     a pod stuck in Pending is a scheduling problem, not a schema problem."

    sleep 5
done

"${KUBECTL[@]}" logs job/cybercloud-durable-schema -n "$NAMESPACE" --all-containers --tail=100 || true

cat <<EOF

bootstrap: done. The cluster now has the namespace, the Orleans membership CRDs, the silo and gateway
identities, the shard secret, and the Orleans grain-storage schema on every configured shard.

Next, and only now:

    helm install cybercloud charts/platform \\
        --namespace ${NAMESPACE} \\
        --set image=${IMAGE}

⚠ NO --create-namespace. Bootstrap owns the namespace, the RBAC and the secret; the chart consumes
them by name. deploy/README.md § What \`charts/platform\` must honour is the full list.
EOF
