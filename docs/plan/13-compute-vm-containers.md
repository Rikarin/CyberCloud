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
