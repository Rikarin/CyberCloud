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
    public static bool TryParsePath(string path, out ResourceId id) { … }
}
```

**Naming rules**, decided once so every provider does not re-litigate them: resource group and
resource names are 1–63 characters, `[a-z0-9]([-a-z0-9]*[a-z0-9])?`, case-insensitive-unique within
their parent. That is the Kubernetes DNS-1123 label rule, chosen because these names end up as
Kubernetes object names and the alternative is a mangling function nobody can invert.

⚠ Azure allows uppercase and a much wider character set in resource names and then mangles them at the
fabric. We are stricter on purpose, and the error message says so, because the mangling is where the
"why is my resource called `pg-a7f3`" support tickets come from.

## Grain keys

From ADR-002: tenant-scoped grains are `IGrainWithStringKey`, keyed by `Orleans.Multitenant`'s
tenant-qualified key. `ResourceKey` is the only type allowed to build the within-tenant part.

| Grain | Key within tenant |
|---|---|
| `ISubscriptionGrain` | `sub/{subscriptionId:N}` |
| `IResourceGroupGrain` | `sub/{subscriptionId:N}/rg/{name}` |
| `IResourceGrain` | `res/{resourceId:N}` |
| `IResourceIndexGrain` | `idx/path/{sha256(path)[..16]}` |
| `IUserGrain` | `user/{userId:N}` |
| `IEmailIndexGrain` | `idx/email/{sha256(normalized)[..16]}` |
| `IOperationGrain` | `op/{operationId:N}` |
| `IClusterConnectionGrain` | *(null tenant)* `cluster/{clusterId:N}` — see below |

Resource grains are keyed by GUID, not by path, so a rename is a metadata update rather than a grain
migration. The path index is a separate grain, and the two are updated in one flow with the index
claimed first — [§ Two-phase create](#two-phase-create).

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
`featureFlags` as ordinary resource types under a **platform tenant** (`Guid.Empty`, the null tenant).
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
