# 15 — Storage: Object, File, Block

## The three kinds, and why they are three providers

| Kind | Resource | Backed by | Consumed as |
|---|---|---|---|
| **Object** | `CyberCloud.Storage/accounts` + `/buckets` | SeaweedFS + S3 gateway | HTTPS, S3 API |
| **File** | `CyberCloud.Storage/fileShares` | SeaweedFS FUSE/NFS, or LINSTOR RWX + an NFS server | Mounted by VMs and pods |
| **Block** | `CyberCloud.Compute/disks` ([13](13-compute-vm-containers.md)) | LINSTOR/DRBD PVC | Attached to one VM |

They look similar and they are not: object storage is a service the tenant calls over the network,
file storage is a mount with POSIX-ish semantics and a locking model, block storage is a device with
an exclusive owner. Merging them into "storage" produces an API where two thirds of the properties are
inapplicable.

## Object storage — M1 · 2.0 EM

> ⚠ **BUILT 2026-08-12, as `CyberCloud.Storage/accounts` — and the first thing the build found was
> about this document rather than about SeaweedFS.** Every provider before it took its row from
> [12 § The catalogue](12-managed-data-services.md), and that table has no storage row at all: its
> subject is *"databases, caches, brokers, search"*. So the authority for the fifth provider is
> **this** document's § The three kinds, and what 12 contributes is ADR-010 clause 1's operator survey
> and § The pattern, once's eight pieces — both of which applied unchanged. `charts/managed/seaweedfs`
> and `src/Providers/CyberCloud.Providers.Storage` are the result, and
> `charts/managed/seaweedfs/conformance.yaml § owed` carries eleven named debts. The four below are
> the ones that are this document's to answer rather than a provider's.
>
> * **`buckets` did not ship, and nothing is in the way of it.** The seaweedfs-operator ships
>   `Bucket`, `S3Identity`, `S3Credentials`, `S3Policy`, `S3PolicyBinding` and
>   `BucketLifecyclePolicy` under `seaweed.seaweedfs.com/v1`, and `BucketSpec` is
>   `(name, clusterRef, reclaimPolicy, adoptExisting, versioning, objectLock, quota, owner, access,
>   placement, anonymousRead)` — § The resource model's list almost line for line. The conformance
>   harness that could not address a depth-2 type **stopped being the blocker on the same day**, so
>   this is the first row in either catalogue whose child type is missing for scope reasons rather
>   than for a stated impossibility.
> * **⚠ "Consumed as HTTPS" is not met and the account is in-cluster only.** § Cross-cutting
>   decisions in [12](12-managed-data-services.md) requires an explicit CIDR allow-list on any
>   external exposure; the operator's `ServiceSpec` is `{type, annotations, loadBalancerIP,
>   clusterIP}` with **no `loadBalancerSourceRanges`**, so a `LoadBalancer` is renderable and a
>   *firewalled* one is not. Shipping the unfirewalled half is the one thing that paragraph forbids in
>   as many words, so no exposure property is declared at all. That still closes the gap this row was
>   most urgently needed for — `charts/managed/postgres`'s `s3://tenant-bucket/postgres` is an
>   in-cluster address.
> * **⚠ "Encryption at rest is on by default" is not true and no property claims it is.** SeaweedFS
>   encrypts behind `weed volume -encryptVolumeData`, and the operator's `VolumeServerConfig` has no
>   field for it — the only route is a free-form `extraArgs` list. A security guarantee carried by an
>   escape hatch that accepts any string is not a guarantee, and this document should either say so or
>   the operator should grow the field.
> * **⚠ The credential is what makes this row unusable, and its shape is worse than any earlier
>   service's.** `weed/s3api/auth_credentials.go` sets `isAuthEnabled = len(identities) > 0` and
>   `AuthenticateRequest` returns an **admin** identity when that is false — so a gateway with no
>   identities file answers every anonymous request as an administrator, over HTTP, on a protocol
>   every tool already speaks. ⚠ **CORRECTED 2026-08-13 — this is the row piece 5 was built against,
>   and it is built.** The paragraph used to end "the provider therefore renders `spec.s3.configSecret`
>   against a `Secret` nothing writes, and the account does not come up", with piece 5 as the blocker.
>   The reconciler now mints an S3 key pair into the tenant's vault **before** it applies anything,
>   renders the identities `Secret` from what the vault returned, and `listKeys` hands the pair back.
>   The ordering is chosen by which partial failure is survivable: a mint with no cluster leaves an
>   inert KV document the next pass reuses, while a cluster with no mint is the open gateway above.
>   [12](12-managed-data-services.md)'s piece 5 row named `ISecretResolver`, which reads and cannot
>   provision; it is corrected there to `ISecretWriter` plus `IResourceActionHandler`.

**ADR-008: the API is S3.** Not the Azure Blob dialect. Every SDK, backup tool, CI runner and framework
already speaks S3; a second dialect buys migration-from-Azure and costs a permanent second surface.

**SeaweedFS**, chosen over MinIO and Ceph RGW:

- MinIO's licence moved to AGPL and its recent direction has removed features from the community
  build. Offering it as a managed service is at best a legal review and at worst a rug-pull.
- Ceph RGW is excellent and is an order of magnitude more operational work — a Ceph cluster is a
  full-time role.
- SeaweedFS is Apache-2.0, has an O(1)-lookup design that stays fast with billions of small objects
  (which a container registry and a package feed both produce), and Cozystack already runs it, so the
  operational shape is known.

⚠ **The honest caveat:** SeaweedFS's S3 implementation is good but not complete. Object versioning,
object lock/WORM, S3 Select and some ACL semantics are partial or absent depending on version. **The
supported-operations table is part of the product documentation and is generated from a conformance
run against the deployed version**, not written by hand — because "S3-compatible" without a table is
how integrations fail in week three.

### The resource model

```
accounts/{name}                       ← the tenancy + billing unit; region, replication, tier defaults
  ├─ buckets/{name}                   ← globally-unique-per-account name, quota, versioning, lifecycle
  ├─ accessKeys/{name}                ← S3 credentials; secret into Vault, `listKeys` action
  ├─ lifecyclePolicies/{name}         ← expire, transition to cold
  └─ cors, publicAccess, encryption   ← account/bucket properties
```

**Access control is two layers, and the seam matters.** Platform-level (who can manage this bucket
resource) is ReBAC ([07](07-rebac-authorization.md)). Data-plane (who can `GetObject`) is S3 access
keys and bucket policies, enforced by SeaweedFS. They are not the same system and pretending otherwise
would put a ReBAC check on every object GET — which is the wrong place for it at object-storage rates.
Managed identities ([11](11-identity.md)) bridge the two by minting scoped S3 credentials on demand.

**Encryption at rest** is on by default with a platform-managed key; customer-managed keys from the
tenant's Vault are a bucket property (M2). **Public access is off by default at the account level and
requires an explicit two-step opt-in** — a publicly readable bucket is the most-reported cloud
misconfiguration in existence and the default is the whole mitigation.

## File storage — M2 · 1.2 EM

NFS first. SMB only if a customer asks, and it is a genuinely worse problem (Samba, AD integration,
locking semantics), so it is not promised.

- `fileShares/{name}` → size, performance tier, protocol, access rules by subnet and by managed identity.
- Backed by SeaweedFS's NFS/FUSE mount for scale-out shares, or a LINSTOR RWX volume with an NFS server
  pod for small shares that need real POSIX locking. The choice is derived from the tier, not exposed.
- Mountable from a tenant's VMs and pods; a CSI driver in the cluster bundle does the pod half.
- Snapshots and per-share backup policy through the same Velero binding as everything else.

⚠ **Performance expectations must be set in the product docs.** Network file storage is slower than
local disk in ways that surprise people, and the failure report is always "your storage is broken"
rather than "I put a database on NFS". The docs name the workloads it is for and the ones it is not.

## Block storage

Covered in [13](13-compute-vm-containers.md) as `CyberCloud.Compute/disks`. LINSTOR/DRBD, replication
factor as a tier property, hot-attach, snapshot, resize (grow only).

⚠ **CORRECTED 2026-08-20. This paragraph used to end "a support contract is a business decision to
make before customer data lives on DRBD".** That decision is made:
[ADR-011](02-technology-decisions.md) § footnote 1 — **no LINBIT contract; the platform runs LINSTOR
and DRBD unsupported**, with [ADR-020](02-technology-decisions.md)'s Talos system extension in place of
the failure mode support would cover. The licence half was never in doubt and is unchanged.

⚠ **What is not yet true, and this section is where it would be believed.** Nothing in
`charts/bundle/` installs LINSTOR today. The bundle's storage component is single-replica and local
(`charts/bundle/openebs-localpv/`), so **replication factor is not a tier property yet and a
node loss loses that node's disks**. [24 § The replicated-storage switch](24-roadmap.md) holds the
trigger and `charts/bundle/openebs-localpv/component.yaml` § the replicated stage holds the
parts list.

## Archive and tiering — M3

Lifecycle rules move objects to a cold SeaweedFS volume set with different replication and different
hardware. Retrieval is slower and cheaper, and — the part that must be right — **the API surfaces the
retrieval latency in the object's metadata**, so an application can decide rather than hang.

## Backup as a service — `CyberCloud.RecoveryServices/vaults` · M2 · 1.5 EM

Not a storage type; a *policy* resource that binds protected resources to schedules and retention.

- Backends: Velero for namespace-scoped Kubernetes state, volume snapshots for block, engine-native
  backup for databases ([12](12-managed-data-services.md)), bucket replication for object.
- A protected resource shows its backup status on its own blade — a backup system nobody can see the
  status of is a backup system that is quietly broken.
- **Restore always creates a new resource.** Restore-in-place is how people lose the good copy while
  trying to recover it.
- ⚠ **A restore that has never been tested is not a backup.** The platform runs an automated monthly
  restore of a sampled protected resource into a scratch resource group, verifies it, reports it on the
  vault's blade, and deletes it. This is a feature, not an internal practice, because it is the one
  thing that distinguishes a backup product from a backup checkbox.

## Metering

| Meter | Unit | Notes |
|---|---|---|
| `storage.object.gb_month` | GB-month | Sampled hourly from SeaweedFS volume stats per bucket |
| `storage.object.requests` | per 10 000 | Class A (write) and class B (read) priced separately, as S3 does |
| `storage.egress.gb` | GB | ⚠ The meter customers care most about and the one most easily wrong. It is measured at the gateway, per bucket, and excludes intra-region traffic — and the docs say exactly where the boundary is |
| `storage.file.gb_month` | GB-month | Provisioned, not used — file shares reserve |
| `storage.block.gb_month` | GB-month | Provisioned × replication factor |
| `storage.backup.gb_month` | GB-month | After compression |

Egress is the meter that generates disputes. The design decision that prevents most of them: **the
portal shows egress broken down by bucket and by hour, in near-real time**, from the same pipeline that
bills it. A customer who can see it accruing does not dispute it at month end.
