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
factor as a tier property, hot-attach, snapshot, resize (grow only). ⚠ ADR-011's licensing note about
LINBIT applies: running GPL-3 LINSTOR is fine, and a support contract is a business decision to make
before customer data lives on DRBD.

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
