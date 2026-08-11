# 06 — Tenancy and the Resource Model

## The hierarchy

Azure's, with one level renamed and one added.

```
Tenant                      ← the customer. Identity boundary. Homed to a region.
 └─ Management Group        ← optional tree, for policy and role inheritance
     └─ Subscription        ← billing + quota boundary. Owns a payment method.
         └─ Resource Group  ← lifecycle boundary. Delete it, delete its contents.
             └─ Resource    ← the thing. Owned by exactly one provider.
```

Two things Azure does that we copy exactly, because they are load-bearing and non-obvious:

- **A resource group is a lifecycle unit, not a folder.** Deleting it deletes everything in it, in
  dependency order, as one operation. This is what makes "spin up an environment and tear it down"
  work, and it is why resource groups exist at all.
- **A subscription is the quota and billing boundary, not the tenant.** One tenant, many
  subscriptions — production, staging, per-team — is the shape every real customer wants within a
  month, and retrofitting it later means renumbering every resource id.

One thing we add: **a Cluster is a first-class resource that other resources are placed into.**
Azure hides the fabric; we cannot, because the brief's whole premise is that a tenant may bring their
own. A managed Postgres therefore has a required `clusterId` property, and the portal's default is
"the subscription's default cluster" so the common case does not feel like plumbing.

## Identifiers

Every entity has a **GUID** — the brief's requirement, and the right one: GUIDs are generated without
coordination, which is what a system with no central sequence needs.

Every addressable thing *also* has a **resource ID**, an Azure-shaped path:

```
/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{rgName}
  /providers/{providerNamespace}/{resourceType}/{resourceName}
```

Both exist and they answer different questions. The GUID is the identity — stable across renames,
used in tuples, in metering records, in grain keys. The path is the *address* — human-readable,
hierarchical, what appears in a URL, what a role assignment scopes to, and what a support engineer
pastes into a ticket. `IResourceIndexGrain` maps path → GUID; the mapping changes when a resource is
renamed and the GUID does not.

```csharp
readonly record struct ResourceId(
    Guid TenantId, Guid SubscriptionId, string ResourceGroup,
    ResourceTypeName Type, string Name, Guid Id)
{
    public string Path => $"/tenants/{TenantId:D}/subscriptions/{SubscriptionId:D}" +
                          $"/resourceGroups/{ResourceGroup}/providers/{Type.Namespace}/{Type.Type}/{Name}";

    /// ⚠ Recovers five of the six fields. `Id` comes back `Guid.Empty` — see below.
    public static bool TryParsePath(string path, out ResourceId id) { … }

    /// Resolves the parsed address to an identity, once the index has been consulted.
    public ResourceId WithId(Guid id) => this with { Id = id };

    /// Lower-cases the structural segments. Hash THIS for the index key, never `Path`.
    public string CanonicalPath => …;
}
```

⚠ **`TryParsePath` cannot fully satisfy its own signature, and that is inherent rather than a
defect to fix.** `Path` carries no resource GUID — by design, since the whole point of the split
above is that the path is the address and the GUID is the identity. So parsing a path yields
`Guid.Empty` for `Id`, and resolving it to a real identity is a lookup through
`IResourceIndexGrain`. `WithId` is that resolution step. Round-tripping is exact for the five
address fields; asserting a six-field round-trip is a test that cannot pass.

⚠ **The index key must hash `CanonicalPath`, not `Path`.** The provider namespace is
case-preserving on the wire (`CyberCloud.Cache` reads better than `cybercloud.cache`), so
`CyberCloud.Cache/redis` and `cybercloud.cache/redis` are the same resource with two different
`Path` strings. Hashing `Path` would let both claim the name and defeat
[§ Two-phase create](#two-phase-create) — the one place a duplicate claim is a correctness bug
rather than a cosmetic one.

⚠ **The path grammar is ambiguous on its own, and the naming rule is what saves it.** Because
resource types nest (`servers/databases`), type `servers` with name `databases/orders` and type
`servers/databases` with name `orders` serialise to the *same* string. The naming rule below forbids
`/` in a name, which is what makes the parse unique. That rule is therefore load-bearing for
identifier integrity, not merely the ergonomics decision it is presented as — and the nesting depth
needs a cap, or a greedy read of the type path is unbounded.

**Naming rules**, decided once so every provider does not re-litigate them: resource group and
resource names are 1–63 characters, `[a-z0-9]([-a-z0-9]*[a-z0-9])?`, case-insensitive-unique within
their parent. That is the Kubernetes DNS-1123 label rule, chosen because these names end up as
Kubernetes object names and the alternative is a mangling function nobody can invert.

⚠ Azure allows uppercase and a much wider character set in resource names and then mangles them at the
fabric. We are stricter on purpose, and the error message says so, because the mangling is where the
"why is my resource called `pg-a7f3`" support tickets come from.

## Grain keys

From ADR-002: tenant-scoped grains are `IGrainWithStringKey`, keyed by `Orleans.Multitenant`'s
tenant-qualified key. `GrainKeys` is the only type allowed to build the within-tenant part.

| Grain | Key within tenant |
|---|---|
| `ITenantGrain` | `tenant/{tenantId:N}` |
| `ISubscriptionGrain` | `sub/{subscriptionId:N}` |
| `IResourceGroupGrain` | `sub/{subscriptionId:N}/rg/{name}` |
| `IResourceGrain` | `res/{resourceId:N}` |
| `IResourceIndexGrain` | `idx/path/{sha256(canonicalPath)[..16]}` |
| `IUserGrain` | `user/{userId:N}` |
| `IManagedIdentityGrain` | `mi/{managedIdentityId:N}` — [11 § Managed identity](11-identity.md) |
| `IEmailIndexGrain` | `idx/email/{sha256(tenantId + normalizedEmail)[..16]}` |
| `IOperationGrain` | `op/{operationId:N}` |
| `IQuotaGrain` | `sub/{subscriptionId:N}` — same key string as the subscription, different grain **type** |
| `ITenantDirectoryGrain` | *(null tenant)* `platform/tenant-directory` |
| `IShardMapGrain` | *(null tenant)* `platform/shard-map` |
| `IClusterConnectionGrain` | *(null tenant)* `cluster/{clusterId:N}` — see below |

⚠ This table is the closed set that `GrainKeys` implements, so a grain missing from it is a grain
that cannot be addressed. The first, tenth, eleventh and twelfth rows were absent from an earlier
version while [04 § Grain taxonomy](04-orleans-topology.md) named the grains — which made those
grains unbuildable until one document moved. If you add a grain, add its row here first.

⚠ **The `IManagedIdentityGrain` row was the same defect, and it is worth recording how it was
fixed rather than only that it was.** [11 § The object model](11-identity.md) names six identity
grains and this table carried a row for one of them. `GrainKeys` added `group/`, `app/`, `sp/` and
`session/` when those grains were built and deliberately left `mi/` out, on the argument that a key
shape with no grain behind it is a shape nothing can hold to its meaning — the same argument that
keeps the Leopard membership index of [07 § Storage](07-rebac-authorization.md) out of it. That was
right while [11 § Managed identity](11-identity.md) was a seam. The row is here now because the
grain is, which is the order this ⚠ asks for: the shape and the thing it addresses land together.

⚠ **`IManagedIdentityGrain` is keyed by its GUID and not by `(cluster, namespace, serviceAccount)`.**
The binding is what a token exchange arrives holding, so keying by it looks like the shape that
saves a lookup. It is wrong three ways: the triple is caller-influenced text on an unauthenticated
endpoint, so an attacker would choose which activation a request creates; a rebind — an ordinary
operation — would become a grain migration and orphan every tuple naming the old key; and "at most
one identity may hold a given triple" is a *uniqueness* question, which is an index's job. The GUID
is also the ReBAC subject id in `managedIdentity:{id}` ([11 § Managed identity](11-identity.md),
step 6), so re-keying would silently revoke every grant made to it.

Resource grains are keyed by GUID, not by path, so a rename is a metadata update rather than a grain
migration. The path index is a separate grain, and the two are updated in one flow with the index
claimed first — [§ Two-phase create](#two-phase-create).

Three things about the two `idx/` rows that the earlier version of this table got wrong, and that
matter because a wrong index key is a wrong *uniqueness* answer:

- **`idx/path/` hashes `canonicalPath`, never `path`** — the ⚠ under [§ Identifiers](#identifiers)
  explains why. The table used to say `path`, which is the spelling that defeats two-phase create.
- **`idx/email/` includes the tenant id.** The table used to say `sha256(normalized)`, which
  specifies a **global** email index — precisely the thing [11 § Sign-up](11-identity.md) says we do
  not have and do not want, because a global index is a global hot spot on the sign-up path. Email
  uniqueness is *per tenant*.
- **`normalized` means: trim, reject empty/over-254/control characters, require exactly one `@` with
  a non-empty local part and domain, then case-fold `A`–`Z` and nothing else.** ⚠ Not
  `ToLowerInvariant()` — U+212A KELVIN SIGN folds onto `k`, so `aK@x` and `ak@x` would collapse to
  one key, which at sign-up is one account silently claiming another's identity and is
  indistinguishable from a legitimate duplicate. Folding only ASCII letters merges exactly the
  equivalence every mail provider implements. Non-ASCII passes through uncased, so `Ä@x` and `ä@x`
  are two entries — a *missed* duplicate, which is the safe direction to be wrong in.

⚠ **`[..16]` is sixteen hex characters — 64 bits — not sixteen bytes.** Both index keys are scoped
*within a tenant*, so the birthday bound is over one tenant's entries: at 1 000 000 resources in a
single tenant the collision probability is ~3 × 10⁻⁸, and a tenant that large is already an outlier
([07 § ListObjects](07-rebac-authorization.md) sizes the big case at 200 000). A collision is a
correctness bug — two names claiming one index grain — so if that margin is ever judged too thin,
widen to 32 characters. It is a one-line change *before* anything ships and a re-key afterwards.

⚠ **`IClusterConnectionGrain` is a null-tenant grain and that is a deliberate exception.** A cluster
connection holds a live client and watches; there must be exactly one activation per cluster
platform-wide, and if it were tenant-qualified then a cluster shared between a tenant and the platform
(which every in-house cluster is, during creation) would have two. The grain therefore carries the
owning tenant as *state* and checks it on every call, and `PlatformCrossTenantAuthorizer` explicitly
allows the platform → connection edge and logs it. This is the single place tenancy is enforced by
code rather than by key, and it is called out here so nobody has to discover it.

## Two-phase create

Creating a resource must be atomic across two grains — the resource and its name index — without a
transaction. The order is fixed and the failure modes are enumerated:

1. **Claim the name.** `IResourceIndexGrain.TryClaim(path, newGuid)`. If taken → `409 Conflict`.
   The claim is durable and carries a 5-minute lease.
2. **Create the resource grain** in `Creating`, write durable desired state, create the operation.
3. **Confirm the claim.** `IResourceIndexGrain.Confirm(newGuid)` converts the lease into a permanent
   binding.
4. Return `202` with the operation URL.

If the silo dies between 1 and 3, the claim expires and the name is free again, and the orphaned
resource grain (durable state, no confirmed index) is swept by a per-subscription reaper reminder.
If it dies between 3 and 4, the resource exists and the caller retries the `PUT` — which is idempotent
because `PUT` with the same body on an existing resource is a no-op, which is exactly why the API is
`PUT` and not `POST`.

**Deletion is the same in reverse and it is the harder half**: release the index first (so the name
is immediately reusable), then tear down the data plane, then delete the grain state. A resource whose
data plane teardown fails is left in `Deleting` with a retry reminder and is *visible* in listings with
that state — never silently gone while its pods still run and its meter still ticks. That last clause
is a billing-dispute prevention measure as much as a correctness one.

## Tenant lifecycle

| State | Meaning | Effects |
|---|---|---|
| `Provisioning` | Directory entry written, shards assigned, bootstrap running | No API access yet |
| `Active` | Normal | — |
| `Warned` | Payment overdue | Portal banner; writes allowed |
| `Suspended` | Overdue past grace, or by admin | **Data plane keeps running**, control-plane writes rejected `403`. Deliberate: suspending a tenant should not take their production down without notice |
| `Disabled` | Explicit shutdown | Data plane scaled to zero. State retained |
| `PendingDeletion` | 30-day tombstone | Nothing runs, nothing is billed, everything is restorable |
| `Purged` | Gone | Grain state deleted, shards reclaimed, directory entry tombstoned forever (never reuse an id) |

Tenant creation is itself a long-running operation with a progress model, because it is: allocate
shards, create the identity realm, create the default subscription, create the default resource group,
optionally provision an in-house cluster (minutes, per ADR-009), seed ReBAC relations, emit the welcome
mail. Every step is idempotent and re-drivable, and the portal shows the steps rather than a spinner.

## Platform administration

The brief requires platform admins to manage tenants. The design decision is that **platform admin is
not a second API** — it is a provider.

`CyberCloud.Platform` exposes `tenants`, `regions`, `shards`, `clusters`, `quotaOverrides`,
`featureFlags` as ordinary resource types under a **platform tenant** (`Guid.Empty`).

⚠ **`Guid.Empty` and "the null tenant" are two different things, and an earlier version of this
sentence said they were one.** They cannot be, and both are needed:

| | What it is | Used for |
|---|---|---|
| **The platform tenant**, `Guid.Empty` | An ordinary tenant id that happens to be all zeroes. Its grains are tenant-qualified like any other, get a shard, and go through the same resource manager | This section's whole argument — "platform admin is not a second API, it is a provider". A provider needs a tenant to own its resources |
| **The null tenant** | The *absence* of tenant qualification. `Orleans.Multitenant` passes the literal string `"Null"` — ⚠ so `Guid.Parse(tenantId)` throws, and this is a live path | [04 § Grain taxonomy](04-orleans-topology.md)'s Platform row: the tenant directory and the shard map. Those must be reachable **before** any tenant is resolved, which is precisely why they cannot be qualified by one |

The distinction is load-bearing rather than pedantic: the directory is what tells you which shard a
tenant is on, so a directory grain that had to be addressed *by tenant* would be circular.
`IClusterConnectionGrain` is null-tenant for the different reason given below — one activation per
cluster, platform-wide.
Consequences, all good:

- Admin actions go through the same resource manager, so they get the same audit, the same LRO model,
  the same OpenAPI, the same CLI (`cyc platform tenant list`) and the same generated forms for free.
- Authorization is the same ReBAC engine — a platform operator holds `platform:root#operator@user:X`,
  and `Support` is a *different, weaker* relation with an expiry.
- The admin UI (`portal/apps/admin`) is a second Angular app against the same API with a different
  scope, not a privileged bypass.

**Cross-tenant access, which admins need, is the one thing that must be explicit.** `PlatformCrossTenantAuthorizer` allows:

| Edge | Allowed when | Logged |
|---|---|---|
| platform tenant → any tenant | The caller holds an active `platform:root#operator` relation | Always, with the operator's user id |
| tenant A → tenant B | A delegation resource exists and is unexpired (Lighthouse-shaped, P1) | Always |
| anything else | Never | `UnauthorizedAccessException` + a security event |

⚠ **Impersonation ("view as tenant") is the feature support will ask for on day one and it is the one
to be careful with.** The decision: it exists, it requires a second operator's approval for a
production tenant, it is time-boxed to 60 minutes, every request made under it carries an
`X-CyberCloud-Impersonated-By` header into the audit log, and **the tenant sees a notification**.
An impersonation system that the customer cannot see is a system nobody will trust after the first
incident.

## Tags, locks, and the small stuff that is not small

| Feature | Behaviour | Why it is here |
|---|---|---|
| **Tags** | Key/value on any resource, ≤ 50 pairs, indexed into the resource-graph projection | Cost allocation is impossible without them, and adding them later means re-tagging an estate |
| **Locks** | `CanNotDelete` / `ReadOnly`, inherited down the hierarchy | Prevents the demo-day incident; costs a check in the write path |
| **System metadata** | `createdBy`, `createdAt`, `modifiedBy`, `modifiedAt`, `provisioningState`, `etag` | `etag` enables `If-Match` and is the only way to make concurrent portal edits safe |
| **`provisioningState`** | `Creating` · `Updating` · `Deleting` · `Succeeded` · `Failed` · `Canceled` | The Azure vocabulary exactly. Every provider uses it and none invents its own |
| **Soft delete** | 7 days for resources carrying data (Vault, Storage, databases) | A dropped production database is not a support ticket you want to have to say no to |

**"Inherited down the hierarchy" reaches three scopes and not four.** `ILockResolver` walks
resource → resource group → subscription and takes the *strongest* lock found — `ReadOnly` outranks
`CanNotDelete`, which is not the enum's numeric order and is the one trap in the walk. It does **not**
walk the management group, because there is no management-group grain, no key for one and no parent
pointer from a subscription to one: § The hierarchy makes that tree optional and
[01](01-azure-parity-catalogue.md) puts it at M2. So a lock at that level is not merely unread — **it
cannot be set at all**, and the day the tree lands, closing the gap is one more scope on the same walk.
A scope with no record contributes no lock rather than failing the walk, deliberately: the group and
subscription records are created by an admin path the resource manager does not drive, and a walk that
fail-closed on a missing record would be a platform in which nothing can be created. Whether the
*subscription itself exists* is a separate question, asked at step 1 of
[08](08-resource-manager.md) § The write path, end to end, and answered with `404` rather than a lock.

## Quota

Enforced at the subscription, checked **before** the provider is called, in the resource manager.

```csharp
Task<Result<QuotaLease>> IQuotaGrain.TryReserveAsync(QuotaMeter meter, decimal amount, Guid operationId);
```

Reservation, not a counter increment — the lease is released if the operation fails, and expires on
its own if the operation grain dies. Meters are per-region and per-family (`vcpu`, `memoryGb`,
`storageGb`, `publicIps`, `clusters`, `resources`), with defaults per subscription tier and per-tenant
overrides as a `CyberCloud.Platform/quotaOverrides` resource.

⚠ **The quota grain is per-subscription and therefore serialises every create in that subscription.**
That is correct — quota is exactly the thing that needs a single writer — and it is fine because
creates are rare. What would *not* be fine is putting a per-tenant rate limiter in the same grain, and
that is why rate limiting lives in the gateway over Redis counters and never touches a grain
([10](10-gateway-and-api.md)).
