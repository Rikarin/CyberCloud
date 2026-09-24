# 15 — Storage: Object, File, Block

## The three kinds, and why they are three providers

| Kind | Resource | Backed by | Consumed as |
|---|---|---|---|
| **Object** | `CyberCloud.Storage/accounts` + `/buckets` | SeaweedFS + S3 gateway | HTTPS, S3 API |
| **File** | ~~`CyberCloud.Storage/fileShares`~~ `CyberCloud.Storage/accounts/fileShares` | ~~SeaweedFS FUSE/NFS~~ SeaweedFS CSI (FUSE), or LINSTOR RWX + an NFS server | Mounted by pods; a VM only by running `weed mount` itself against what `listMountTargets` returns (`nfs-is-not-served`) |
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

> ⚠ **BUILT 2026-09-15, as `CyberCloud.Storage/accounts/fileShares` (#30) — and three sentences in
> this section were false the moment the sources were read rather than the README.**
> `charts/managed/seaweedfs-fileshare` and `StorageFileShares` in
> `src/Providers/CyberCloud.Providers.Storage` are the result; that chart's `conformance.yaml § owed`
> carries eleven named debts. The three corrections that are this document's to own:
>
> * **⚠ The table above spelled the type `CyberCloud.Storage/fileShares`, and it ships as a child of
>   the account.** A share's bytes live in a *filer*, and the only filer this platform runs is the one
>   inside an account's `Seaweed` — a top-level share would need a filer of its own or a platform-wide
>   one, which is a tenancy boundary [06](06-tenancy-and-resource-model.md) does not have. The same
>   argument put `buckets` under the account, and it holds harder here.
> * **⚠ "NFS first" and "SeaweedFS's NFS/FUSE mount" — SeaweedFS has no NFS server.** `weed`'s command
>   list at 4.41 is `mount`, `fuse`, `s3`, `webdav`, `sftp`, `iam`, `filer` and friends, and no NFS
>   anything; the community answer is nfs-ganesha over a `weed mount`, which is a second workload to
>   operate and is not built. What the engine has is a CSI driver that `weed mount`s a filer path into
>   a pod over FUSE and advertises `MULTI_NODE_MULTI_WRITER`, and the operator deploys it from a
>   `SeaweedCSIDriver` custom resource. So the **pod half** of "mountable from a tenant's VMs and pods"
>   ships — a `ReadWriteMany` claim — and the **VM half** is owed (`nfs-is-not-served`). No `protocol`
>   property is declared: an enum whose one value the cluster does not serve is the promise
>   `encryption-at-rest` refused to make on the account.
> * **⚠ "A CSI driver in the cluster bundle does the pod half" — it is per account, not in the
>   bundle.** `seaweedfs-csi-driver` takes its filer as a process argument, not a StorageClass
>   parameter, so one driver serves one account's filer. It is rendered by the account's *first share*
>   and removed by its *last* — and not before the last share's *released volume* is reclaimed, because
>   the provisioner that reclaims it runs inside the driver — because a driver is a controller
>   Deployment plus two DaemonSets on every node and an account with no shares should not pay for one.
>   What that costs — unmetered pods per account, and a driver whose resource-id label names whichever
>   share applied it last and is rewritten on every pass — is `one-driver-per-account` and
>   `the-driver-carries-one-shares-labels`.
>
> And one line below that is neither built nor a correction: "snapshots and per-share backup policy
> through the same Velero binding as everything else" has no Velero binding to go through yet — the
> account's `backup` row records why — and is `per-share-backup-policy`.
>
> What held: the size. The CSI mounter passes the claim's capacity to `weed mount` as a collection
> quota on a collection named for the volume, so `quota.size` is a real, master-enforced ceiling that
> grows online. "The choice is derived from the tier" holds vacuously — there is one tier, because
> [§ Block storage](#block-storage) below already says nothing installs LINSTOR — and `tier` is absent
> rather than an enum of one (`premium-tier-needs-linstor`). "Access rules by subnet and by managed
> identity" wait on a managed identity type that [24](24-roadmap.md) lists as not shipped
> (`access-rules-are-not-declared`).

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

> ⚠ **BUILT 2026-09-18, as `CyberCloud.RecoveryServices/vaults` (#30) — the first type whose
> reconciler reads another provider's resource, and two of this section's sentences did not survive
> the sources.** `charts/managed/recovery-vault` and `src/Providers/CyberCloud.Providers.RecoveryServices`
> are the result; that chart's `conformance.yaml § owed` carries twelve named debts. What a vault is:
> one policy — a five-field cron and a retention in days — over a list of protected items in its own
> resource group, each a `CyberCloud.DBforPostgreSQL/servers` the vault has been granted `read` on.
> The reconciler reads each item through `ReconcileContext.View` ([08 § What the resource manager
> deliberately does not do](08-resource-manager.md), issue #90's seam, used by nothing until this
> type), takes the *address* of the CloudNativePG `Cluster` that server's provider rendered, and
> renders one `ScheduledBackup` beside it under the vault's own id. `listRecoveryPoints` lists the
> `Backup` objects the operator's controller made, by the controller's own
> `cnpg.io/scheduled-backup` label; `recover` bootstraps a **new** cluster from a completed one.
> The two corrections that are this document's to own:
>
> * **⚠ "Volume snapshots for block" and the brief's "a PVC VolumeSnapshot" for file shares — the
>   file-share half has no snapshot story on this platform.** seaweedfs-csi-driver v1.4.20's
>   `pkg/driver/driver.go` advertises `CREATE_DELETE_VOLUME`, `EXPAND_VOLUME`,
>   `SINGLE_NODE_MULTI_WRITER` and `PUBLISH_UNPUBLISH_VOLUME` and **no `CREATE_DELETE_SNAPSHOT`**;
>   `charts/bundle/` installs no snapshot controller and no class that could serve one. A
>   `VolumeSnapshot` rendered against a share's claim would never reach `readyToUse` anywhere this
>   platform runs — the promise § File storage refused to make with `protocol: NFS`. So of the four
>   backends listed below **one ships — engine-native backup for databases** — a file-share item is
>   refused by name at its pointer, and the other three are `file-shares-have-no-snapshot-story` (the
>   block half waits on LINSTOR and the external-snapshotter, [§ Block storage](#block-storage)),
>   the account's `backup` row (Velero and bucket replication both need a destination object store
>   that is not the account itself).
> * **⚠ "Binds protected resources to schedules and retention" — the vault owns the schedule and the
>   recovery-point record, and the *store* is the server's.** CloudNativePG keeps a cluster's backup
>   destination and its `retentionPolicy` on the `Cluster` (`spec.backup.barmanObjectStore`), which
>   is the protected server's to write and which the view — read-only, by design — cannot reach.
>   The vault reads the server's published contract instead and refuses what it could not keep: a
>   server with `backup.enabled: false` (CloudNativePG fails every Backup of it with *"cannot proceed
>   with the backup as the cluster has no backup section"*) and a server whose `backup.retentionDays`
>   is shorter than the vault's. ⚠ And on this platform today the server's store is not wired:
>   `charts/managed/postgres` renders `destinationPath: ""` and no credentials, nothing fills the
>   empty destination in, and CloudNativePG's real definition refuses the result outright — the
>   empty string, and a filled-in one for *"missing credentials"* — so **no PostgreSQL server with
>   backups on can be created on a real cluster today**, found the first time a suite put one in
>   front of the operator the bundle installs, and recorded at `charts/managed/postgres/conformance.yaml
>   § owed`, `the-default-bucket-is-not-filled-in`, by this pass. Until that row closes, the vault's
>   operator lane asserts the only thing that is true: the server the operator admits is one the
>   vault refuses, and the vault's own rendering is admitted, scheduled and failed by the operator
>   with its own reason, which `listRecoveryPoints` reports per point (`the-store-is-the-servers`).
>
> What held: "Restore always creates a new resource" — `recover` refuses a target name a cluster
> already holds, and a restored cluster carries no `backup` block so it cannot archive over the
> source's WAL. What is a *cluster object* rather than a *resource* — nothing meters it, no
> `listKeys` knows it — is `a-restore-is-not-yet-a-resource`, and the property that closes it is the
> server's. "The platform runs an automated monthly restore" is `the-monthly-test-restore`: the
> primitive exists and the scheduled pass it needs is the manager-started pass
> [08](08-resource-manager.md) records as owed — which is also why retention is enforced on passes
> (`retention-is-enforced-on-passes`). One policy per vault where this section says "schedules and
> retention", because an array of objects is not expressible in a `ResourceSchema`
> (`one-policy-per-vault`); a vault carrying several is a `vaults/backupPolicies` child type.
>
> ⚠ What the review of the first cut added to the ledger: `recover` is gated by `write` on the
> *vault* and by nothing on the server whose bytes it materialises, because `ActionContext` carries
> no caller and no authorizer (`recover-is-gated-by-the-vault-alone` — the honest closing move is
> the same `restoreFrom` on the server that closes `a-restore-is-not-yet-a-resource`, at which point
> the manager gates the restore with `write` on the server's type); "shows its backup status on its
> own blade" below has only the vault's side, `listRecoveryPoints`, and nothing on the server's
> (`backup-status-is-not-on-the-servers-blade`); and `storage.backup.gb_month` is declared and not
> emitted (`backup-storage-is-not-metered`).
>
> ⚠ **Corrected 2026-09-24 (#30, end to end): the store is wired, a restore is a resource, and
> retention has a pass of its own.** Three sentences above stopped being true:
>
> * *"No PostgreSQL server with backups on can be created on a real cluster today."* A server's
>   reconciler now gives it a bucket of its own GUID (`pg-{id}`) on the **platform's** object store —
>   the SeaweedFS behind `CyberCloud:ObjectStorage`, not a tenant's `CyberCloud.Storage/accounts` —
>   and a key scoped to that one bucket, issued by the store's IAM API and minted into the vault
>   (`IObjectStoreGrants`, `ObjectStoreCredentials`), rendered into the `{name}-backup-s3` Secret the
>   `Cluster`'s `barmanObjectStore.s3Credentials` names. An empty `backup.destinationPath` means that
>   store; a named one is refused at its pointer, because no property of 2026-08-01 could carry its
>   credentials. The in-tree `barmanObjectStore` rather than the Barman Cloud plugin, because the
>   pinned CloudNativePG 1.30.0 serves it and the plugin needs a component the bundle does not
>   install (`charts/managed/postgres/conformance.yaml § owed`, `the-in-tree-archiver-goes-in-1-31`).
> * *"`recover` bootstraps a new cluster"* — it creates a new **server resource**:
>   `/properties/restore/recoveryPoint` on `CyberCloud.DBforPostgreSQL/servers` bootstraps the
>   `Cluster` from the point, and the vault's handler creates the server through
>   `ActionContext.Creator`, which the manager runs as the caller's own `PUT`
>   ([08](08-resource-manager.md) § The cross-resource seam, "An action may create, as its caller").
>   The copy archives to a bucket of its own, so "cannot archive over the source's WAL" holds by
>   construction rather than by leaving the backup section off.
> * *"The scheduled pass it needs is the manager-started pass 08 records as owed"* — it exists:
>   the vault declares `PassEvery(1h)` ([08](08-resource-manager.md) § The manager-started pass), so
>   retention is enforced with nobody writing to the vault. The monthly test restore still is not
>   built; its remaining decision is a platform principal to create the scratch copy as.
>
> `backupNow` takes an on-demand point. The CloudNativePG lane — `RecoveryVaultAgainstCloudNativePg`,
> a real operator from `install.sh` and a real SeaweedFS beside k3s — writes a row, protects the
> server, completes a `backupNow` point into the server's bucket, `recover`s it into a new server
> and reads the row back. One copy, one store, one failure domain: the off-site second copy is
> `the-store-is-the-servers`, rewritten to say so.
>
> ⚠ **Corrected 2026-09-24 (#30's reclaim): a restore no longer reads its source's Secret, so the
> source's teardown removes it.** The copy above bootstrapped from the point's `Backup` object, and
> CloudNativePG reads that object's credentials from the Secret its status names — the *source's*
> `{name}-backup-s3` — so every teardown left that Secret behind, and `NamespaceReclaim`
> ([08](08-resource-manager.md)) refused the resource group forever over a platform-written object.
> A restore now reads the source's key from the vault, where the source's teardown leaves it,
> renders it as the copy's own `{name}-restore-s3`, and bootstraps through `externalClusters` with the
> point's `backupID`; a teardown that ends a server (not one parking a soft delete) removes both
> Secrets. The CloudNativePG lane now deletes and purges the source and restores the same point a
> second time. What still outlives a server — the bucket, the store identity, the vault path — is
> `charts/managed/postgres/conformance.yaml § owed`, `the-backups-outlive-the-server-and-nothing-reclaims-them`.

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
