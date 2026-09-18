# 13 — Compute: Clusters, VMs, Containers, Registry

## Managed Kubernetes — `CyberCloud.ContainerService/managedClusters` · M1 · 4.0 EM

The first provider written, because it is the one that proves the fabric ([09](09-kubernetes-fabric.md)).
Two flavours behind one resource type:

| `kind` | Meaning |
|---|---|
| `Managed` | We create it: Cluster API + Kamaji control plane + KubeVirt workers |
| `Connected` | The tenant brings it: kubeconfig, SA token, or the outbound agent |

Both expose the same properties and the same actions. A `Connected` cluster returns `null` for
node-pool operations rather than pretending — the API says what it cannot do instead of failing late.

**Sub-resources:** `agentPools` (a `MachineDeployment` + instancetype + autoscale bounds),
`credentials` (a `listCredentials` action returning a short-lived, scoped kubeconfig — never the admin
one), `addons` (ingress, cert-manager, monitoring agents, GPU operator — each a bundle chart the
tenant opts into).

**Upgrades.** Control-plane version and node version are separate properties; the control plane must
be upgraded first and by at most one minor. Node upgrades are a rolling `MachineDeployment` update with
a max-unavailable and a drain timeout. ⚠ Kubernetes' own version-skew policy is the hard constraint
and the API enforces it with a clear error, rather than letting a tenant break their cluster and open
a ticket.

**What a tenant gets that they would not get from `kubeadm`:** the cluster is a resource, so it has
ReBAC scoping, quota, metering, backup policy, an audit trail, monitoring wired in, and a `kubectl`
credential that expires. That list is the product.

## Virtual Machines — `CyberCloud.Compute/virtualMachines` · M2 · 3.0 EM

KubeVirt. A VM is a `VirtualMachine` CR plus `DataVolume`s from CDI plus a Kube-OVN interface.

| Property | Backed by |
|---|---|
| Size | KubeVirt `VirtualMachineInstancetype` + `Preference`, from the [12](12-managed-data-services.md) family catalogue |
| Image | `CyberCloud.Compute/images` → a CDI `DataVolume` source. Platform catalogue (Ubuntu, Debian, Rocky, Windows Server ⚠ licensing) + tenant-private images |
| Disks | `CyberCloud.Compute/disks` → LINSTOR PVCs, hot-attachable |
| Networking | A NIC per subnet, optional floating IP, security groups ([14](14-networking.md)) |
| Init | cloud-init user-data; ⚠ **SSH keys and passwords are `SecretRef`s resolved at render** and never stored in grain state or in the CR's plaintext |
| Console | Serial + VNC over the terminal hub ([19](19-cloud-terminal-and-virtual-desktop.md)) |

**Actions:** `start`, `stop` (graceful ACPI), `restart`, `deallocate` (release compute, keep disks —
the billing-relevant one), `snapshot`, `resize` (⚠ requires a stop for CPU/memory; live-resize is not
offered because KubeVirt's support for it is version-dependent and a half-working resize is worse than
none).

**Scale sets — `/scaleSets` · M2.** A replica count over one template. Deliberately *not* an
autoscaler in M2: autoscaling a VM pool needs a metric source and a scale-in safety story, and it is
M3. A fixed-size set is 80 % of the value for 20 % of the risk.

**Live migration** is supported by KubeVirt for maintenance drains and is used by the platform, but is
not a tenant-facing action. It is an operational capability, not a feature to document and support.

**What landed (#28, 2026-09-17): the three nouns of this row's table, and not the scale sets.**
`CyberCloud.Compute/virtualMachines`, `CyberCloud.Compute/disks` and `CyberCloud.Compute/images` are
published at `2026-08-01`, with the charts under `charts/managed/virtual-machine`, `charts/managed/disk`
and `charts/managed/image`. What the table above says and what shipped differ in five places, each
recorded at `charts/managed/virtual-machine/conformance.yaml § owed` rather than left to be
discovered. **Size** is a closed set — `s1.small` to `s1.xlarge`, whole cores — rendered as
`domain.cpu.cores` and `domain.memory.guest` rather than as an instancetype name, because the bundle
installs no catalogue object and a name nothing resolves is a machine that never starts; what quota
reserves is what the guest gets. **Image** is a CDI `DataVolume` a tenant imports once per resource
group, from the platform catalogue (Ubuntu 24.04/22.04, Debian 13/12 — `quay.io/containerdisks`
references pinned by digest, which is the checksum the puller verifies) or from a `docker://` or
`http(s)://` address of their own; a machine's root disk is a clone of it in the same namespace.
⚠ #28 predicted this row would get stuck on building images, and it did not: a plain cloud image
carries no kubelet, so the catalogue is a table and not a pipeline. **Disks** are blank `DataVolume`s
attached by name in the machine's body and taking effect at its next start — hotplug is not rendered.
**Networking** is one interface on one subnet, joined through `ovn.kubernetes.io/logical_switch` with
the Network family's own object name; no floating IP and no security group on the machine yet.
**Init** is as written: `cloudInit.userData` is a `SecretRef` handle, resolved at render into a Secret
the machine mounts, and the value reaches no body and no grain. **Actions** are `start`, `stop` and
`restart`, the first handlers in the tree that write a cluster; ⚠ the power state is
`spec.runStrategy` on the KubeVirt object and not a body property, because an action cannot change a
desired body, and the reconciler reads it back before every render so a tag change does not boot a
stopped machine. `stop` is this row's `deallocate` — a halted KubeVirt machine holds no compute and
keeps every disk, and there is no stopped-but-allocated state for a second verb to mean. `snapshot`
needs a `VolumeSnapshotClass` the node-local storage stage has none of, and `resize` is a mutable
`size` that KubeVirt applies at the next start rather than a refused PUT on a running machine.
⚠ **The first machine ran, on the lane the plan said could not run one.** The first `VirtualMachine`
this platform rendered was admitted by KubeVirt's webhooks on a real CDI and KubeVirt installed
through `charts/bundle/install.sh` — the first family whose chart-schema half is measured rather than
owed, for one render of each of its three charts — and reported `Running` under KVM 46 seconds after
the apply, on k3s-in-Docker, with a blank disk from `charts/managed/disk` attached, populated once
the machine consumed it, and mounted by the launcher. Issue #95 and `charts/bundle/bundle.yaml
§ owed` said Docker Desktop's VM lends no `/dev/kvm`; a privileged container on a WSL2 host with
nested virtualization has it, and nobody had measured. What `Running` does not prove — the guest
finishing its boot, cloud-init taking effect, the disk appearing inside as a block device — is
`charts/managed/virtual-machine/conformance.yaml § owed`, `the-guest-is-not-reached`; kube-ovn,
LINSTOR and the node-pool Machines stay the VM lane's (#95). ⚠ **And that measurement is nightly**:
the class costs eight minutes on a serial chain the PR runner had already spent 26 minutes on against
[23 § CI shape](23-build-ci-and-testing.md)'s 25-minute budget, so #28's review opened the
`*.Nightly` lane and put it there.
⚠ **What the review of #28 corrected in this row.** `cloudInit.userData` is the one place in the
tree where a vault path a *tenant* spelled is resolved, by a resolver holding one platform-wide
token, into a Secret the tenant's own guest mounts; the reconciler now refuses any path outside
`tenants/{tenantId}/` before the vault is asked (`VirtualMachines.TenantVaultPrefix`), which is the
scoping [18 § Shape](18-security-vault-and-malware-scan.md)'s namespace-per-tenant topology
would give and this platform's single namespace does not. And the two nouns of #28 this row does
not land are recorded with what each waits for at `charts/managed/virtual-machine/conformance.yaml
§ owed` — scale sets are this row's (`scale-sets-are-not-landed`); container instances are the next
row's, a provider namespace of their own.

## Container Instances — `CyberCloud.ContainerInstance/containerGroups` · M2 · 0.8 EM

The cheapest real provider and a good second one to write. A container group is a `Pod` (or a `Job`
for `restartPolicy: Never`) in the tenant's namespace with resource limits, env from `SecretRef`s, an
optional volume, an optional public IP, and logs streamed to the portal.

Its value here is disproportionate: it is the provider used to prove the reconciler contract, the
label discipline, the log-streaming path and the metering hook, in a resource type simple enough that
a bug is obviously a platform bug.

## Container Apps — `CyberCloud.App/containerApps` · M3 · 2.5 EM

Knative Serving. ⚠ **Scale-to-zero and revision traffic-splitting are the actual product**; without
them this is a `Deployment` with a nicer form, and the catalogue already has better ways to run a
`Deployment`. If Knative's operational cost proves too high, the honest move is to cut this row rather
than ship the degraded version.

## Container Registry — `CyberCloud.ContainerRegistry/registries` · M1 · 1.5 EM

**Harbor**, one instance per tenant (or a shared instance with per-tenant projects for small plans —
a plan attribute, decided by cost). Harbor already does OCI, replication, retention policies, robot
accounts, signing and scanner integration; writing a registry instead would be a year for a worse one.

- Robot accounts map to service principals; `docker login` uses a platform token.
- Storage backend is the tenant's SeaweedFS bucket, so registry storage is billed like any other blob.
- Vulnerability scanning via Trivy, with results surfaced on the resource and shared with
  [18](18-security-vault-and-malware-scan.md)'s verdict model.
- Replication between a tenant's registries in different regions is a Harbor feature exposed as a
  sub-resource.

### Artifact feeds — `CyberCloud.ContainerRegistry/feeds` · M2 · 1.5 EM

NuGet, npm, Maven. ⚠ **This is the row most likely to be underestimated.** Harbor does OCI only;
these are three protocols with three auth schemes, three upstream-proxy semantics and three
versioning models. The decision: implement the three protocols in a single .NET service backed by
SeaweedFS, rather than running three third-party artifact servers — because the auth integration is
the hard part and doing it once against our own token model is cheaper than three times against three
plugin systems.

Scope, stated so it does not creep: **proxy + host + retention.** No build integration, no license
scanning, no dependency graph. Those are a different product.

**What landed (#29, 2026-09-15): the host third.** `CyberCloud.ContainerRegistry/feeds` is published
at `2026-08-01` with an immutable `kind` of `nuget`, `npm` or `maven` — one feed is one protocol,
because the three versioning models do not share a catalogue — and `CyberCloud.Registry.Feeds.Host`
is the single service: NuGet v3 (service index, push, unlist, flat container, registration, search),
the npm registry protocol (publish, packument, tarball, dist-tags) and the Maven layout (PUT/GET with
release immutability — checksums and signatures beside a release included — generated
`maven-metadata.xml` and checksums). Artefacts go to the platform's object store under
`{tenant}/{feed}/`, an immutable artefact under a key that carries its own SHA-256 so two racing
pushes of one version cannot land on each other's bytes, through the `IObjectStore` seam ([15](15-storage-blob-file.md)'s
SeaweedFS behind Signature V4), the catalogue is a durable `FeedGrain` the provider owns, and every
request carries the gateway's bearer token — as `Bearer`, as a Basic password, or in
`X-NuGet-ApiKey`, because that is how the three clients send one — and is authorised through the
resource manager with the type's own `read`/`write`, never a feed-local user list. ⚠ **It is the first
type whose data plane is a platform host and not a tenant's cluster**, so it declares no `clusterId`
and its chart renders nothing. ⚠ **Proxy and retention are not declared:** an `upstream` property
nothing fetches through would publish a feature nobody can have. Both, and the storage and egress
meters the host does not yet emit, are recorded at `charts/managed/feeds/conformance.yaml § owed`.

## Cross-cutting

**Placement.** Every compute resource names a `clusterId` ([09](09-kubernetes-fabric.md)). VMs and
scale sets additionally name a node pool or an availability-zone hint. There is no cross-cluster
scheduler before M3.

**Metering.** vCPU-hours, memory-GB-hours, storage-GB-months, egress-GB, public-IP-hours, plus
per-service meters. Emitted by the provider on state transitions and by a sampler on a 5-minute tick —
**never** derived from Kubernetes metrics alone, because a resource that exists but is not running is
still billed for its disk, and metrics do not know that ([22](22-billing-metering-and-quota.md)).

**Quotas.** Reserved before create ([06](06-tenancy-and-resource-model.md)), released on failure. The
error names the meter, the request and the remainder.

**Images and licensing.** ⚠ Windows Server images are a licensing arrangement, not a technical task.
The platform image catalogue ships Linux only until that arrangement exists, and the API returns a
clear "not available in this catalogue" rather than a mysterious absence.
