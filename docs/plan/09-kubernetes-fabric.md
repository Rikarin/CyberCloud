# 09 — The Kubernetes Fabric

Everything below `CyberCloud.Kubernetes`: how a cluster is connected, how objects are written to it,
how its state comes back, and how we create one when the tenant does not bring their own.

## Cluster connections

A cluster is a **resource** (`CyberCloud.ContainerService/managedClusters` for ours,
`/connectedClusters` for theirs) and a connection is how the platform reaches it.

| Kind | How the platform authenticates | For |
|---|---|---|
| `Kubeconfig` | A kubeconfig in Vault, with a service account the tenant created from our manifest | BYO clusters, simplest, works everywhere |
| `ServiceAccountToken` | A projected, short-lived token; the platform holds only the issuer + CA | BYO clusters that can reach us |
| `AgentInitiated` | An agent in the tenant's cluster dials **out** to the gateway over gRPC and the platform sends requests down that channel | ⚠ BYO clusters behind NAT with no inbound path — which is most of them |
| `InHouse` | Cluster API objects in the management cluster; credentials come from the CAPI-generated kubeconfig secret | Clusters we create (§ Kubernetes in Kubernetes) |

⚠ **`AgentInitiated` is not optional and is easy to defer into a crisis.** The brief's "connection
string to kubernetes" implies inbound reachability, and for a tenant's on-prem cluster that is usually
false. Budget it as part of the fabric (1.5 EM, M2) rather than discovering it at the first on-prem
customer. The agent is small — a reverse-tunnel client and a scoped proxy — but the *authorization*
on the platform side is not: a compromised agent must not be able to act as another tenant, which
means the tunnel identity is bound to the cluster resource id at the gateway.

```csharp
[Alias("K8s.ClusterConnection")]
public interface IClusterConnectionGrain : IGrainWithStringKey   // null-tenant, per 06
{
    Task<Result<ClusterHealth>> PingAsync();
    Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command);
    Task<Result> DeleteAsync(KubeCommand command);
    Task<Result<T>> GetAsync<T>(ObjectRef @ref) where T : IKubernetesObject;
    Task<Result<InformerLease>> WatchAsync(GroupVersionKind gvk, string labelSelector);
}
```

One activation per cluster platform-wide, pinned by a reminder, holding the client and the informers.
Its state carries the owning tenant and every call checks it — the one place tenancy is enforced by
code rather than by key ([06](06-tenancy-and-resource-model.md)).

**Connection health is a first-class resource property.** A cluster that has not answered a ping in
90 seconds is `Degraded`; its resources' reconciles are suspended (not failed) and the portal says
"cannot reach your cluster" instead of "provisioning failed". The distinction between *our* failure
and *unreachable* is what stops a tenant's network outage from looking like a platform bug.

## The command builder

ADR-013 in full. The requirement from the brief — every deployment marked with proper labels — is met
by making the unlabelled case not compile.

```csharp
public static class KubeCommand
{
    public static IKubeCommandNeedsTenant For(IKubeClusterConnection connection);
}

public interface IKubeCommandNeedsTenant   { IKubeCommandNeedsResource WithTenantId(Guid tenantId); }
public interface IKubeCommandNeedsResource { IKubeCommandBuilder WithResourceId(ResourceId resourceId); }

public interface IKubeCommandBuilder       // ← only here do Apply/Build/Delete exist
{
    IKubeCommandBuilder WithSubscriptionId(Guid id);      // inferred from ResourceId; override for platform objects
    IKubeCommandBuilder InNamespace(string ns);           // defaults to the resource's namespace
    IKubeCommandBuilder WithLabels(params (string, string)[] extra);
    IKubeCommandBuilder WithAnnotations(params (string, string)[] extra);
    IKubeCommandBuilder WithOwner(ResourceId parent);     // → ownerReferences + cascade
    IKubeCommandBuilder WithFieldManager(string manager); // defaults to cybercloud/{provider}
    IKubeCommandBuilder Chart(string chart, JsonElement values);   // render then apply
    IKubeCommandBuilder Object<T>(T obj) where T : IKubernetesObject<V1ObjectMeta>;
    KubeCommand Build();
    Task<Result<ApplyOutcome>> ApplyAsync(CancellationToken ct = default);
    Task<Result> DeleteAsync(CascadePolicy policy, CancellationToken ct = default);
}
```

Usage, from a reconciler:

```csharp
await KubeCommand.For(ctx.Cluster)
    .WithTenantId(ctx.Id.TenantId)
    .WithResourceId(ctx.Id)
    .InNamespace(ctx.Namespace)
    .Chart("managed/postgres", ctx.Desired)
    .ApplyAsync(ct);
```

**Every rendered object gets, injected and non-overridable:**

```yaml
labels:
  cybercloud.io/tenant-id:       9f2c1b7e-…
  cybercloud.io/subscription-id: 77de4a10-…
  cybercloud.io/resource-group:  prod
  cybercloud.io/resource-id:     3a8f0c22-…
  cybercloud.io/resource-type:   cyberCloud.dbforpostgresql_servers
  cybercloud.io/api-version:     2026-08-01
  cybercloud.io/managed-by:      cybercloud
annotations:
  cybercloud.io/resource-path:   /tenants/…/providers/CyberCloud.DBforPostgreSQL/servers/main
  cybercloud.io/reconcile-hash:  sha256:…      # of the desired body — cheap no-op detection
```

⚠ **Label values are limited to 63 characters and a restricted alphabet.** GUIDs in canonical form are
36 characters and legal. The *path* is not — hence path as an annotation, id as a label. Resource type
is lowercased and `/` replaced by `_` for the same reason. This is exactly the kind of detail that
becomes a two-day bug six months in, so it is decided here.

**Why not `HelmRelease` and Flux**, which is what Cozystack does: because then desired state lives in
the target cluster's etcd (contradicting ADR-001), the reconcile loop is Flux's rather than ours (so
progress reporting, cancellation and quota are outside our control), and a `HelmRelease` in a tenant's
BYO cluster requires installing Flux in it. We render charts **in-process** with a Helm library and
apply the resulting objects with server-side apply. The chart stays the packaging format — which is
what makes Cozystack's charts reusable — without the GitOps controller.

**Server-side apply, always**, with a stable field manager per provider. That gives us conflict
detection for free: if a tenant hand-edits a field we own, the next apply reports a conflict rather
than silently reverting, and *that* becomes a drift event with a name.

## Observing: informers, not polling

Each connection grain runs shared informers for the GVKs its tenant's resources use, filtered by
`cybercloud.io/managed-by=cybercloud`. Events go to `cc.{tenant}.k8s.{cluster}.{kind}`; the resource
grain for the owning `resource-id` consumes its own and updates hot-tier observed state.

**Why informers rather than each reconciler polling:** N resources polling is N × rate API calls
against a cluster we do not own and may be rate-limited by. One watch per kind is O(kinds). It is also
what makes per-cluster drift detection ([08](08-resource-manager.md)) a local diff instead of a
cluster-wide list.

⚠ **The informer cache is in one silo's memory and is lost when that silo dies.** Re-establishing it
is a full list + watch, which for a large cluster is seconds and a burst of API load. Mitigations:
resume from the last `resourceVersion` where the API server still has it, and stagger re-establishment
across clusters so a silo restart does not stampede every tenant's API server at once. The second one
matters more than it sounds — a 30-silo rolling deploy without staggering is a synchronized list storm.

## Kubernetes in Kubernetes

For tenants who do not bring a cluster. ADR-009: Cluster API + Kamaji + KubeVirt.

```
Management cluster (ours)
├─ Cluster API core + bootstrap(kubeadm) + control-plane(Kamaji) + infrastructure(KubeVirt)
├─ per tenant cluster:
│   ├─ etcd cluster            ← etcd-operator, dedicated, 3 replicas
│   ├─ KamajiControlPlane      ← kube-apiserver + controller-manager + scheduler AS PODS
│   ├─ KubevirtMachine ×N      ← real VMs, real kernels, real kubelets  ← the isolation boundary
│   ├─ kubevirt-csi            ← tenant PVCs backed by LINSTOR volumes in the management cluster
│   └─ the bundle              ← CNI, CSI, metrics-server, cert-manager, monitoring agents
```

**The shape, said plainly:** the control plane is *shared infrastructure running isolated processes*;
the workloads are *isolated VMs*. A tenant's pods never share a kernel with another tenant's. That is
the property vcluster does not give and the reason ADR-009 accepts Kamaji's slower provisioning.

**Creation is a long-running operation with real steps**, and the portal shows them:

| Step | Typical | Failure mode |
|---|---|---|
| Allocate VPC, subnet, API VIP | 10 s | Address pool exhausted → fail fast with the pool name |
| etcd cluster ready | 60 s | Storage class unavailable |
| Kamaji control plane ready | 45 s | Certificate issuance |
| First worker VM boots and joins | 90–180 s | Image pull, DHCP, cloud-init — ⚠ the flakiest step by far |
| Remaining workers join | 60 s | — |
| Bundle installed and healthy | 90 s | — |
| **Total** | **6–9 min** | |

⚠ **Six to nine minutes is the honest number** and every surface must be designed for it: the portal
shows the step list, the CLI streams progress, the SDK's `Operation<T>` has a sane default poll
interval, and the API docs say so. A "create cluster" button that looks like it should take five
seconds is a support burden.

**Node pools** are `MachineDeployment`s with a KubeVirt `VirtualMachineInstanceTemplate` and an
instancetype from a platform catalogue (which is where the `t1.micro`/`c1.large` vocabulary from
ADR-010 is defined once and reused by every provider). Autoscaling is cluster-autoscaler with the
Kamaji/CAPI provider.

**GPU** ([01](01-azure-parity-catalogue.md) § L): the NVIDIA GPU operator in the bundle, with HAMI for
fractional sharing. Passthrough to a KubeVirt VM requires VFIO and host configuration that is not a
software task — it is in M3 because it is a hardware programme with a software component.

## The platform's own cluster — the bootstrap

The brief settled this and the design leans on it: **Cyber Cloud is installed on an existing cluster,
manages a second one, and moves onto it once the second is boring.** That ordering is a forcing
function, not caution.

| Phase | Platform runs on | Manages | Proves |
|---|---|---|---|
| **0 — bootstrap** | An existing cluster, installed by hand from `deploy/bootstrap/` | Nothing | The charts install |
| **1 — managing** | The existing cluster | A second, in-house cluster created by CAPI | The fabric works against a cluster it is not in |
| **2 — dogfood** | The existing cluster | The second cluster runs *real tenant workloads* | Managed services work |
| **3 — migration** | The managed cluster | Itself + others | ⚠ The interesting one |

**Phase 3 has a circular dependency and it needs a written answer, not a shrug.** If the platform runs
on cluster B and cluster B is managed by the platform, then a platform outage means cluster B cannot
be repaired through the platform. The answer, in three parts:

1. **The platform's own resources are marked `self-managed`** and are excluded from tenant-facing
   reconciliation. The platform does not provision itself.
2. **`deploy/bootstrap/` remains supported and tested forever** — it is what an operator runs to repair
   or reinstall the platform with no platform running. It is exercised by every e2e run, so it cannot
   rot.
3. **Cluster B's control plane is not Kamaji-hosted by us.** It is a standalone cluster (Talos or
   whatever the operator runs), because a hosted control plane whose host is the thing that broke is
   not recoverable. In-house *tenant* clusters are Kamaji-hosted; the platform's cluster is not.

That third point is a constraint on the migration, and it is written down now because it is much
cheaper to honour than to discover.

## Multi-cluster placement

A subscription has a **default cluster** and every resource may name a `clusterId`. That is the whole
of placement in M1 and M2 — no scheduler, no bin-packing, no affinity rules.

M3 adds `CyberCloud.Platform/placementPolicies`: constraints (region, capability, capacity, tenancy
class) evaluated at create time into a concrete cluster id, which is then stored on the resource.
**Placement is decided once and recorded, never recomputed** — a resource that could migrate between
clusters is a resource whose data has to migrate too, and that is a different product.

## Testing the fabric

- **`k3s` in Testcontainers** for reconciler unit tests. Fast, real API server, real SSA semantics.
- **A `kind` cluster with CAPI + Kamaji + KubeVirt** in the nightly e2e, creating and destroying a real
  tenant cluster. Slow (~20 min) and the single most valuable test in the suite, because it is the one
  that catches operator version drift.
- **A deliberately hostile BYO cluster** in e2e: an older Kubernetes minor, a restrictive PSA, no
  default storage class, a webhook that rejects unlabelled objects. If the fabric only works against
  clusters we built, the brief's core premise is unmet.
- **Connection loss** in the chaos suite: blackhole a cluster mid-provision, assert `Degraded`,
  suspended reconciles, no failed operations, and clean resumption.
