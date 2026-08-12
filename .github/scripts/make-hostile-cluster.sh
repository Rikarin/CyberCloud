#!/usr/bin/env bash
#
# docs/plan/23 § Test layers, row Hostile BYO: "Old Kubernetes minor, restrictive PSA, no default
# storage class, a rejecting webhook | Nightly | The brief's core premise".
#
# Creates a kind cluster that is hostile in exactly one of those four ways, and then PROVES it is.
#
# Usage: make-hostile-cluster.sh <leg> <cluster-name>
#        legs: old-minor | restrictive-psa | no-default-storage-class | rejecting-webhook
#
# ── ⚠ THE VERIFICATION AT THE END IS THE POINT OF THE SCRIPT ─────────────────────────────────────
#
# A hostile-cluster job that creates a cluster, fails to apply the hostility, and then runs the
# suite against a perfectly friendly cluster is green, fast, and worthless — and it looks exactly
# like a passing hostile test. `kubectl apply` of a ValidatingWebhookConfiguration succeeds whether
# or not anything will ever consult it; patching a storage class succeeds when the annotation was
# already absent; `kind create --image` succeeds on whatever tag it was given.
#
# So every leg ends by demonstrating the hostility from outside: an actual rejected write, an
# actually empty default-storage-class list, an actual server version. If the demonstration does not
# fire, the script fails and the leg never runs the suite. That is the same instinct as
# Build.E2E.cs § Refuses, which asserts on the refusal MESSAGE rather than on a non-zero exit,
# because "some guard fired" is not a check on any particular guard.

set -euo pipefail

leg="${1:?usage: make-hostile-cluster.sh <leg> <cluster-name>}"
cluster="${2:?usage: make-hostile-cluster.sh <leg> <cluster-name>}"
context="kind-$cluster"
work="${RUNNER_TEMP:-/tmp}/hostile-$leg"
mkdir -p "$work"

fail() {
    echo "::error title=Hostile BYO ($leg)::$1"
    exit 1
}

# ⚠ Pinned digests, not tags. A kind node tag is mutable, and the whole content of the `old-minor`
# leg is which Kubernetes minor it runs — a tag that quietly moved would turn this leg into a second
# copy of the others without changing a line here.
#
# The current minor is whatever the reconciler suite runs against: K3sFixture.cs pins
# rancher/k3s:v1.35.7-k3s1. `old-minor` sits four minors behind it, which is roughly the oldest
# release a supported cluster is likely to be on.
OLD_MINOR_IMAGE="kindest/node:v1.31.6"
CURRENT_IMAGE="kindest/node:v1.35.0"

image="$CURRENT_IMAGE"
config="$work/kind.yaml"

cat > "$config" <<'YAML'
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
YAML

case "$leg" in
    old-minor)
        image="$OLD_MINOR_IMAGE"
        ;;

    restrictive-psa)
        # Pod Security admission at `restricted` for every namespace that does not opt out. This is
        # the setting that rejects a pod for running as root, for not dropping capabilities, or for
        # an unset seccomp profile — the three things a chart written against a permissive cluster
        # gets wrong.
        cat > "$config" <<YAML
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    kubeadmConfigPatches:
      - |
        kind: ClusterConfiguration
        apiServer:
          extraArgs:
            admission-control-config-file: /etc/kubernetes/psa/psa.yaml
          extraVolumes:
            - name: psa
              hostPath: /etc/kubernetes/psa/psa.yaml
              mountPath: /etc/kubernetes/psa/psa.yaml
              readOnly: true
              pathType: File
    extraMounts:
      # ⚠ Mounted from the work directory rather than written to the host's /etc/kubernetes. That
      # avoids sudo, which is what makes this leg runnable on a workstation — and a hostile-cluster
      # script nobody can run locally is one nobody debugs.
      - hostPath: $work/psa.yaml
        containerPath: /etc/kubernetes/psa/psa.yaml
        readOnly: true
YAML
        ;;

    no-default-storage-class | rejecting-webhook)
        : # Created normally, made hostile after the API server is up.
        ;;

    *)
        fail "unknown leg '$leg'. The four are old-minor, restrictive-psa, no-default-storage-class and rejecting-webhook — docs/plan/23 § Test layers, row Hostile BYO."
        ;;
esac

if [ "$leg" = "restrictive-psa" ]; then
    # kind bind-mounts this into the node container, so it has to exist before the cluster is made.
    cat > "$work/psa.yaml" <<'YAML'
apiVersion: apiserver.config.k8s.io/v1
kind: AdmissionConfiguration
plugins:
  - name: PodSecurity
    configuration:
      apiVersion: pod-security.admission.config.k8s.io/v1
      kind: PodSecurityConfiguration
      defaults:
        enforce: restricted
        enforce-version: latest
        audit: restricted
        audit-version: latest
        warn: restricted
        warn-version: latest
      exemptions:
        # Without this the cluster cannot finish coming up: kube-proxy and the CNI are privileged
        # by necessity. Exempting kube-system and leaving every other namespace restricted is what a
        # hostile-but-functional BYO cluster actually looks like.
        namespaces: [kube-system, local-path-storage]
YAML
fi

echo "Creating kind cluster '$cluster' for leg '$leg' on $image"
kind create cluster --name "$cluster" --image "$image" --config "$config" --wait 180s

kubectl --context "$context" cluster-info

# ── Post-creation hostility, and the proof of it ─────────────────────────────────────────────────

case "$leg" in
    old-minor)
        version=$(kubectl --context "$context" version -o json | jq -r '.serverVersion.gitVersion')
        echo "server version: $version"
        minor=$(kubectl --context "$context" version -o json | jq -r '.serverVersion.minor' | tr -d '+')
        current_minor="${CURRENT_IMAGE##*v}"
        current_minor="${current_minor%.*}"
        current_minor="${current_minor#*.}"
        if [ "$minor" -ge "$current_minor" ]; then
            fail "this leg is supposed to run an OLD Kubernetes minor and the API server reports 1.$minor, which is not older than the current 1.$current_minor. The pin in this script has drifted, and the leg would have tested nothing that the others do not."
        fi
        echo "✔ hostility proven: the API server is 1.$minor, behind the current 1.$current_minor."
        ;;

    restrictive-psa)
        # The proof: a pod that a permissive cluster accepts without comment.
        cat > "$work/root-pod.yaml" <<'YAML'
apiVersion: v1
kind: Pod
metadata:
  name: psa-probe
  namespace: default
spec:
  containers:
    - name: probe
      image: registry.invalid/never-pulled:latest
      securityContext:
        runAsUser: 0
        privileged: true
YAML
        if kubectl --context "$context" apply -f "$work/root-pod.yaml" 2>"$work/psa.err"; then
            kubectl --context "$context" delete pod psa-probe -n default --ignore-not-found
            fail "Pod Security admission accepted a privileged root pod, so this cluster is not restricted and the leg would have tested nothing. The admission config in this script did not take effect."
        fi
        grep -q 'violates PodSecurity' "$work/psa.err" \
            || fail "the privileged pod was rejected, but not by PodSecurity: $(cat "$work/psa.err"). Rejected-for-the-wrong-reason is not the hostility this leg is about."
        echo "✔ hostility proven: PodSecurity rejected a privileged root pod."
        ;;

    no-default-storage-class)
        # kind ships `standard` as the default. Un-defaulting it is the BYO cluster whose operator
        # never set one, which is the case a chart with a bare `storageClassName: ""` PVC hangs on.
        kubectl --context "$context" patch storageclass standard \
            -p '{"metadata":{"annotations":{"storageclass.kubernetes.io/is-default-class":"false"}}}'

        defaults=$(kubectl --context "$context" get storageclass \
            -o jsonpath='{range .items[?(@.metadata.annotations.storageclass\.kubernetes\.io/is-default-class=="true")]}{.metadata.name}{"\n"}{end}')
        if [ -n "$defaults" ]; then
            fail "a default storage class is still marked default ($defaults), so the patch did not take and the leg would have run against a friendly cluster."
        fi
        echo "✔ hostility proven: no storage class is marked default."
        ;;

    rejecting-webhook)
        # A webhook whose service does not exist, with failurePolicy: Fail. Every create of the
        # resources it covers is refused by the API server. This is the corporate policy webhook
        # that is down, or the one that does not like your labels.
        cat > "$work/webhook.yaml" <<'YAML'
apiVersion: admissionregistration.k8s.io/v1
kind: ValidatingWebhookConfiguration
metadata:
  name: hostile-byo-rejects-everything
webhooks:
  - name: reject.hostile.cybercloud.io
    admissionReviewVersions: [v1]
    sideEffects: None
    failurePolicy: Fail
    timeoutSeconds: 5
    # ⚠ Scoped away from kube-system so the cluster stays alive. A webhook that also rejects the
    # CNI's writes tests the webhook, not the platform's behaviour under one.
    namespaceSelector:
      matchExpressions:
        - key: kubernetes.io/metadata.name
          operator: NotIn
          values: [kube-system, kube-public, kube-node-lease, local-path-storage]
    rules:
      - operations: [CREATE, UPDATE]
        apiGroups: ["", "apps"]
        apiVersions: ["v1"]
        resources: [configmaps, secrets, deployments, statefulsets]
    clientConfig:
      service:
        name: no-such-admission-service
        namespace: default
        path: /reject
        port: 443
YAML
        kubectl --context "$context" apply -f "$work/webhook.yaml"

        if kubectl --context "$context" create configmap hostile-probe \
            --from-literal=k=v -n default 2>"$work/webhook.err"; then
            kubectl --context "$context" delete configmap hostile-probe -n default --ignore-not-found
            fail "the rejecting webhook accepted a ConfigMap, so it is not rejecting anything and the leg would have tested nothing."
        fi
        grep -qE 'failed calling webhook|no endpoints available|context deadline' "$work/webhook.err" \
            || fail "the ConfigMap was refused, but not by the webhook: $(cat "$work/webhook.err")."
        echo "✔ hostility proven: the API server refused a ConfigMap because the webhook is unreachable."
        ;;
esac

echo "Cluster '$cluster' is up and demonstrably hostile in the '$leg' way. Context: $context"
