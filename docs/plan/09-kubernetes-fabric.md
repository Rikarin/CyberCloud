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

⚠ **CORRECTED 2026-09-15 by #36, which built `AgentInitiated`: the agent dials out over a
WebSocket, not "over gRPC" as the table above says, and the table is left as written because it is
the design and this is the record of where the build departed from it.** Three reasons, each of
which would have been enough. The gateway already terminates WebSockets for its four hubs, so the
tunnel adds no listener and no package — and [02 § Dependency register](02-technology-decisions.md)'s
rule is that a package not in the register needs an ADR, which gRPC would have been. A bidirectional
gRPC stream needs HTTP/2 end to end, and the corporate egress proxy a NAT'd on-prem cluster sits
behind routinely downgrades to HTTP/1.1 — which is exactly the network this row exists for. And the
frame protocol is carrier-agnostic (`ITunnelTransport` in `CyberCloud.Kubernetes.Contracts`), so a
gRPC carrier is one more implementation if a network ever wants it, not a rewrite.

**What landed, and where the authorization sentence above is enforced.** The tenant creates
`CyberCloud.ContainerService/connectedClusters`, asks it for `listInstallCommand`, and runs the
`helm upgrade --install` it answers with in their cluster. That command carries a one-time enrollment
token; `charts/agent` runs `CyberCloud.Agent.Host`, which dials `GET /agent/v1/tunnel` on the gateway
with it. The gateway hashes it and asks `IAgentTunnelGrain` — keyed by the cluster resource id, the
same null-tenant key as the connection grain — whether that hash admits an agent to *that* cluster.
That is the binding "to the cluster resource id at the gateway". The token is spent on the first
admission and exchanged for a long-lived credential the agent keeps in a Secret in its own namespace;
the platform holds hashes only, never a plaintext. The resource reaches `Succeeded` when the first
heartbeat arrives, and on that pass the driver attaches an `AgentInitiated` connection under the
resource's id, which is then a `clusterId` other resources can be placed in.

⚠ **The tunnel carries `IKubeApiClient` calls, not HTTP, and that is the "scoped proxy".** The agent
serves seven typed operations — ping, get, apply, delete, set-owner, discover, list — and refuses
anything else by name, so a compromised platform, or a platform bug, cannot send a connected cluster a
request a reconciler could not have expressed. What it costs is the watch: a tunnel frame has one
answer and an informer is a stream, so **informers do not cross the tunnel yet** and
`IClusterConnectionGrain.WatchAsync` refuses an `AgentInitiated` connection before anything is sent
down it. ⚠ It has to refuse *there*, not in the client: establishing an informer is a list (the
watch is `SharedInformer.PumpAsync`, which nothing in production calls yet), and a list crosses the
tunnel like any other request — the first cut left the refusal in `TunnelKubeApiClient.WatchAsync`,
and a connected cluster handed out a lease and persisted a cursor for a watch nothing could pump.
`AgentTunnelGrainTests.AWatchOnAConnectedClusterIsRefusedBeforeAnyListCrossesTheTunnel` pins the
refusal and that the agent saw no list. The connection grain's
own tenancy check runs unchanged above the tunnel — an `AgentInitiated` descriptor gets a
`TunnelKubeApiClient` from the same factory a kubeconfig would — and the tunnel grain admits
`ExchangeAsync` from a null-tenant caller only, which in production is the connection grain after that
check. Both halves are pinned by `AgentTunnelGrainTests`; the resource's flow by
`ConnectedClusterConformance`; the protocol, both ends real over a pair of pipes, by
`TunnelEndToEndTests`. **No suite crosses a real NAT** — `charts/agent/conformance.yaml § owed` says
what a `kind` with no inbound route would add, and it is the "deliberately hostile BYO cluster" of
§ Testing the fabric, which does not exist yet.

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
    IKubeCommandBuilder CoWriting(KubeObject live);       // a fragment onto ANOTHER resource's object — see below
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
  cybercloud.io/resource-type:   cybercloud.dbforpostgresql_servers    # ⚠ fully lower case — see below
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

⚠ **CORRECTED: the example above used to print `cyberCloud.dbforpostgresql_servers`, with a capital
C** — contradicting the sentence immediately before this one, which has always said the resource type
is lowercased. The document disagreed with itself and the example was the wrong half. It matters
because `resource-type` is a **selector**, not a display string: label matching is exact and
case-sensitive, so a mixed-case value makes
`kubectl get -l cybercloud.io/resource-type=cybercloud.dbforpostgresql_servers` — and every
label-filtered list the platform issues — return nothing at all, *successfully*. A selector that
matches zero objects is not an error anywhere in Kubernetes. Found by writing `KubeLabelTests`, which
now pins the fully lower-cased form, leading `c` included.

**Why not `HelmRelease` and Flux**, which is what Cozystack does: because then desired state lives in
the target cluster's etcd (contradicting ADR-001), the reconcile loop is Flux's rather than ours (so
progress reporting, cancellation and quota are outside our control), and a `HelmRelease` in a tenant's
BYO cluster requires installing Flux in it. We render charts **in-process** with a Helm library and
apply the resulting objects with server-side apply. The chart stays the packaging format — which is
what makes Cozystack's charts reusable — without the GitOps controller.

**Server-side apply, always**, with a stable field manager per provider. That gives us conflict
detection for free: if a tenant hand-edits a field we own, the next apply reports a conflict rather
than silently reverting, and *that* becomes a drift event with a name.

### A second writer on an object — the co-owned apply

Everything above assumes one object has one owning resource. A VNet peering does not fit: it is an
entry in `Vpc.spec.vpcPeerings` plus static routes on **both** networks' `Vpc` objects, each already
owned by the `virtualNetworks` resource that rendered it. Under the ordinary build a peering applying
its parent's `Vpc` claims `resource-id`, `resource-type` and `reconcile-hash` at values that differ
from the owner's — a `FieldManagerConflict` on every one, which is exactly the conflict the paragraph
above exists to produce (#31 found it; #89 built the way out). `CoWriting(live)` switches the builder
to the **co-owned** mode, whose rules are:

- **Labels are the owner's, and so is the rest of the metadata.** The builder injects none and refuses
  `WithLabels`, `WithTemplateLabels`, `WithOwner`, `WithAnnotations`, `WithFieldManager` and
  `WithSubscriptionId` by name: a co-writer adds only its own fragment, and an annotation applied
  beside a fragment would ride under the shared manager without being in the fragment the next
  co-writer merges, which prunes it. The live object must carry the seven — a co-writer writes only onto an object this
  platform owns — and its `tenant-id` must be the co-writer's own. Who the owner is comes off the
  live object's labels, never from the caller.
- **One field manager per co-owned object, named for the owner:**
  `cybercloud/{ownerType}/{ownerId}` — `cybercloud/cybercloud.network_virtualnetworks/3a8f0c22-…` —
  shared by every co-writer of that object and distinct from the owner's `cybercloud/{provider}`.
  ⚠ **A manager per co-writer is the obvious shape and it does not converge.** `Vpc.spec.vpcPeerings`
  and `Vpc.spec.staticRoutes` declare no `x-kubernetes-list-type`, so each is one atomic value with one
  set of owners; a second manager applying the list with its own entry added is a
  `FieldManagerConflict` on the whole list, forever, because `Force` is unreachable. Measured against
  `rancher/k3s:v1.35.7-k3s1` in `CoOwnedApplyTests.AManagerPerCoWriterWouldConflictOnTheAtomicListWhichIsWhyTheyShareOne`.
- **A fragment, a hash and a path per co-writer**, as annotations keyed by the co-writer's GUID —
  `cybercloud.io/fragment.{id}`, `cybercloud.io/fragment-hash.{id}`, `cybercloud.io/fragment-path.{id}`
  — beside the owner's two rather than over them. The hash is over *this* fragment alone, so a
  co-writer's no-op question is answered about its own slice. The fragment itself is written down
  because the co-writers share a manager and a manager's apply is the whole set of fields it owns:
  each apply merges every *other* stored fragment with the caller's and applies the union — objects
  recursively, arrays by concatenation in co-writer order, a scalar two fragments set differently
  refused naming the path and both co-writers. It is bookkeeping, not desired state; the grain holds
  the truth (ADR-001).
- **`metadata.resourceVersion` is carried** from the live object, so two co-writers racing onto one
  object lose loudly: the API server's optimistic lock is a `409` with no `FieldManagerConflict`
  cause, which `KubeApiClient` reports as `ApplyResult.Stale` — not a drift event; nothing is owned
  wrongly — and `KubeCoWriter` reads again and applies again, three times, before handing it back.
- **Teardown withdraws.** `DeleteAsync` in this mode applies the other co-writers' union without this
  one's and drops only this co-writer's three annotations; it never deletes the owner's object. When
  the last fragment goes the shared manager owns nothing and the API server drops its entry.
- **The owner's delete wins.** ⚠ The API server does **not** hold the lock against an object that is
  absent: an apply carrying a `resourceVersion` against a name that is not there goes down the
  create-on-update path, which clears the version and *creates* — measured in
  `CoOwnedApplyTests.TheApiServerDoesNotHoldTheLockAgainstAnAbsentObjectWhichIsWhyTheClientRefuses`.
  A co-writer that created the owner's object would create it under the owner's name with none of
  the seven labels and none of the owner's spec. So `KubeApiClient` refuses a co-owned apply whose
  read-before-write found nothing (`KubeCommand.OwnerResourceId` marks the command), and the tunnel
  agent refuses a co-owned command that carries labels. The window between that read and the `PATCH`
  remains, and is the one thing here the API server does not close.

A child reconciler reaches this through `ReconcileContext.CoWriter` — `ApplyFragmentAsync` and
`WithdrawFragmentAsync` over the pass's own connection, read-then-apply with the stale retry inside
— rather than through the builder directly. The full round trip — owner applies, two co-writers add
fragments, all three slices and the seven labels coexist, each withdraws only its own, the owner's
re-apply is `Unchanged` — is `CoOwnedApplyTests.TwoCoWritersFragmentsCoexistWithTheOwnersAndEachRemovesOnlyItsOwn`
against a real k3s; the rules without a cluster are `CoOwnedCommandBuilderTests`.

⚠ **What the conformance harness can and cannot do with it yet.** `IProviderCaseSource.Siblings` lets
a case create the second network a peering names, beside the ancestor chain, converged before the
first assertion. Two gaps remain and are named where they bite: `FakeKubeCluster` stores a body
verbatim and so *refuses* a co-owned command by name rather than replacing the owner's object with a
fragment, and `ConformanceState.Reset` empties the fake cluster between assertions, so a sibling's
*objects* are gone when a test starts. A Docker-free peering case needs both closed;
`charts/managed/kube-ovn-vpc/conformance.yaml § owed`, `peerings-need-a-second-writer-on-the-vpc`.

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
│   ├─ kubevirt-csi            ← tenant PVCs backed by management-cluster volumes  ⚠ see below
│   └─ the bundle              ← CNI, CSI, metrics-server, cert-manager, monitoring agents
```

⚠ **`kubevirt-csi`'s line used to say "backed by LINSTOR volumes", and as of 2026-08-20 that is a plan
rather than a fact.** What `charts/bundle/` installs is `openebs-localpv` — a **single-replica,
node-local** class with no DRBD and no kernel module, which is what lets the bundle install onto a
cluster the platform did not build (phase 0 below). The consequence for this diagram is concrete: a
tenant cluster's PVCs, and the VM root disks under them, **do not survive the loss of the management
node they landed on**. That is right for design partners and wrong at GA;
[24 § The replicated-storage switch](24-roadmap.md) holds the trigger and
`charts/bundle/openebs-localpv/component.yaml` holds the parts list.

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

⚠ **CORRECTED: phase 0's "the charts install" cannot happen until the Orleans membership CRDs exist,
and this table never mentioned them.** The omission is easy to make because the natural assumption —
that the clustering provider creates its own definitions — is false. Verified by reading the shipped
assembly of `Orleans.Clustering.Kubernetes` 10.0.1: it makes exactly five Kubernetes calls
(`Create`, `Get`, `List`, `Replace` and `Delete` `NamespacedCustomObjectAsync`) and **zero
`apiextensions.k8s.io` calls anywhere**. A silo started against a cluster without the definitions
neither creates them nor degrades — it fails, and the failure names a missing *custom resource* rather
than a missing CRD, which is the version of this bug that costs an afternoon.

The package does *ship* them, at `lib/Definitions/SiloEntryCRD.yaml` and
`lib/Definitions/ClusterVersionCRD.yaml`. They are copied verbatim into
`deploy/bootstrap/10-orleans-crds.yaml`, and re-copied and diffed whenever the pinned package version
moves — the `orleans.dot.net/v1` schema is the package's, not ours. They are deliberately not in
`charts/platform` either: Helm installs `crds/` once and never upgrades or deletes it, so a chart-owned
CRD is a CRD nobody can change.

**So phase 0's first step is applying two cluster-scoped objects**, which needs rights the platform's
own identity does not have and never will — see [02 § ADR-004](02-technology-decisions.md), where the
cost sentence that omitted this is corrected.

**Phase 3 has a circular dependency and it needs a written answer, not a shrug.** If the platform runs
on cluster B and cluster B is managed by the platform, then a platform outage means cluster B cannot
be repaired through the platform. The answer, in three parts:

1. **The platform's own resources are marked `self-managed`** and are excluded from tenant-facing
   reconciliation. The platform does not provision itself.
2. **`deploy/bootstrap/` remains supported and tested forever** — it is what an operator runs to repair
   or reinstall the platform with no platform running. ~~It is exercised by every e2e run, so it cannot
   rot.~~

   ⚠ **CORRECTED, as of 2026-08-11: nothing exercises it.** Checked rather than assumed — `build/` was
   grepped for `bootstrap` and the only hit is `_build.csproj` describing where Nuke writes its *own*
   bootstrapping scripts. `Build.E2E` is a stub that reports itself unimplemented and names
   `test/CyberCloud.E2E`, a project that does not exist yet; `deploy/bootstrap/bootstrap.sh` is run by
   hand and by nothing else. Read the sentence as the intention it is: **wiring `E2E` to stand its
   environment up from `deploy/bootstrap/` is what would make it true**, and it is the cheapest way to
   buy the guarantee. Until that edge exists the directory rots at exactly the rate of anything nobody
   runs. Same claim, same correction, in [03 § deploy/](03-repository-layout.md).
3. **Cluster B's control plane is not Kamaji-hosted by us.** It is a standalone cluster, because a
   hosted control plane whose host is the thing that broke is not recoverable. In-house *tenant*
   clusters are Kamaji-hosted; the platform's cluster is not. ⚠ **This used to read "Talos or whatever
   the operator runs", and the vagueness is now spent:** [ADR-020](02-technology-decisions.md) makes
   Talos the node OS on every machine the platform owns, which includes this one. What does **not**
   change is the exception ADR-020 names — the worker nodes *inside* an in-house tenant cluster are
   KubeVirt VMs booting `quay.io/capk/ubuntu-2404-container-disk` through Cluster API's kubeadm
   bootstrap provider, and they stay that way.

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
