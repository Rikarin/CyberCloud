# 08 — The Resource Manager

The ARM equivalent, and per [01](01-azure-parity-catalogue.md) the largest single item in the plan.
Everything a tenant does passes through it exactly once.

## The write path, end to end

```
PUT /tenants/{t}/subscriptions/{s}/resourceGroups/{rg}
    /providers/CyberCloud.DBforPostgreSQL/servers/{name}?api-version=2026-08-01

  gateway
    ├─ authenticate → CallerContext (user or service principal, tenant, scopes)
    ├─ resolve tenant → region  (tenant directory cache — 05)
    └─ if not this region → proxy, done

  ResourceManager.WriteAsync
    1. parse path → ResourceId; check tenant == caller's; check the subscription is one this
       tenant has (ISubscriptionGrain, through ForTenant(caller))  → 404 if it is not
       look up the provider + type + api-version in the registry
    2. validate body against the type's JSON Schema for that api-version
    3. ReBAC Check(resource | parent rg, "write", caller)      → 404 if not readable
    4. locks: CanNotDelete / ReadOnly inherited from rg, sub, mg
    5. policy evaluation (M3) — deny / modify / audit
    6. quota: IQuotaGrain.TryReserveAsync                      → 429 with which meter
    7. IResourceIndexGrain.TryClaim(path)                      → 409 if taken
    8. write the ReBAC parent edge — resource:{id}#parent@resourceGroup:{sub}-{rg}
         → the manager writes it; nothing else in the platform does
         → BEFORE step 9, and that ordering is the point
    9. IResourceGrain.SubmitDesiredAsync(body, etag, operationId)
         → writes durable desired state, provisioningState = Creating|Updating
         → registers the reconcile reminder
   10. IOperationGrain.StartAsync(...)
   11. emit resource-changed to cc.{t}.res....
   12. → 202 Accepted, Azure-Async-Operation: /operations/{opId}, Retry-After: 10
```

Steps 3–7 are the entire reason this is one component rather than a shared library each provider calls.
A provider that could skip step 3 is a provider that eventually will.

**Step 1 checks two things the caller supplied and does it before anything else.** The tenant in the
path must be the caller's, and the subscription must be one that tenant has. Both refuse with `404`
and with the same message a missing resource gets — a subscription is exactly as enumerable as a
resource name and leaks more, because it is the billing boundary ([07](07-rebac-authorization.md)
§ The enforcement seam). The subscription lookup goes through `ForTenant(caller)`, which is what makes
"exists" and "belongs to this tenant" the same question. Both checks come before the registry, because
the registry's refusal names the api-versions the platform serves and that is a description handed out
through an address nobody has shown the caller owns.

**Step 8 is the only step that writes to the authorization store, and its position is a decision
rather than an ordering.** [07](07-rebac-authorization.md) § The model makes a resource's permissions
inherit through `From("parent", …)`, which follows a `resource:X#parent@resourceGroup:Y` tuple. The
resource's GUID exists from step 6 and its name is claimed at step 7, so the edge can be written before
any durable state does — and it must be:

- **After it (steps 9–12) a failure is not recoverable by the request.** There is a window in which
  the resource is durable and invisible to the person who just created it, and a silo lost inside that
  window leaves it invisible permanently: the operation grain that could re-drive the work has not been
  started yet either.
- **Before it, a failure is a clean refusal.** Nothing durable was written, the quota lease is
  released, the index claim expires, and the caller gets an error instead of a `202` for a resource
  they cannot see.

The cost is the mirror image: a silo lost between step 8 and step 9 leaves a tuple pointing at a GUID
no path resolves to. It grants nothing — the id is unaddressable, the claim expires, and the next create
of that path mints a different GUID — so it is inert storage rather than an authorization defect. The
write path removes it on the one failure that leaves no resource (step 9 refusing a create), and does
**not** remove it on failures after that, because a resource that exists with an odd-looking edge is
one its owner can still see and delete, and a resource that exists with no edge is one nobody can.

**The delete side belongs to the operation grain, not to `DeleteAsync`.** A delete is accepted long
before it converges and the resource stays visible in `Deleting` the whole time
([06](06-tenancy-and-resource-model.md) § Two-phase create), so unlinking at the request would blind
the owner to their own teardown. `IOperationGrain` removes the edge after `CompleteDeleteAsync` — when
the resource is *gone* — and retries from its reminder if that fails, because a tuple pointing at a
deleted object is a slow leak in the tenant's tuple store and "log it and move on" would grow one row
per resource ever deleted. The retry converges: on a re-drive the reconciler reports `Converged` for an
already-absent resource, `CompleteDeleteAsync` is idempotent, and the unlink is attempted again until
it lands or the sixty-minute ceiling reports a `Failed` operation that names the reason.

**`PUT` is a full replacement and is idempotent.** `PATCH` is a JSON Merge Patch and is not. `POST`
appears only for actions on an existing resource (`/restart`, `/rotateKeys`, `/listKeys`), never for
creation. This is Azure's grammar and it is copied because it makes retry-safety a property of the
verb rather than of each provider's care.

## The reconcile loop

The resource grain never provisions inline. It records intent and returns; a reminder drives
convergence.

```csharp
public interface IResourceReconciler          // implemented once per resource type
{
    ResourceTypeName Type { get; }
    Task<ReconcileOutcome> ReconcileAsync(ReconcileContext ctx, CancellationToken ct);
    Task<ReconcileOutcome> DeleteAsync(ReconcileContext ctx, CancellationToken ct);
    Task<ObservedState> ObserveAsync(ObserveContext ctx, CancellationToken ct);
}

public readonly record struct ReconcileContext(
    ResourceId Id, JsonElement Desired, ObservedState? Observed,
    IKubeClusterConnection Cluster, ISecretResolver Secrets, IReconcileLog Log);
```

`ReconcileOutcome` is one of `Converged`, `InProgress(reason, retryAfter)`, `Failed(error, retryable)`.
Nothing else. A reconciler that wants to say something else wants to log, and `IReconcileLog` is how —
those entries stream to `operation-progress` and appear in the portal and in `cyc --wait`, which is what
turns a four-minute cluster creation from a spinner into a story.

**The contract every reconciler must satisfy**, checked by the conformance suite:

1. **Idempotent.** Called twice with identical inputs, the second call is `Converged` and changes
   nothing. This is not aspirational — the reminder *will* fire on a grain that already converged, and
   the silo *will* die between the apply and the state write.
2. **No hidden state.** Everything it needs comes from `ReconcileContext`. A reconciler with a field
   is a reconciler that breaks when the grain moves silo.
3. **Bounded.** Returns within 30 seconds or returns `InProgress`. A reconciler that blocks on a
   four-minute cluster creation blocks that grain's turn, and Orleans grains are single-threaded.
4. **Observes, never assumes.** `Converged` means it *read back* the desired shape, not that the apply
   returned 200.

**Backoff.** 10 s → 30 s → 2 min → 10 min, capped, with ±20 % jitter. After 60 minutes in `InProgress`
the operation fails with a timeout error naming the last progress entry — a resource stuck forever is
worse than a resource that failed, because a failure is actionable.

**Drift.** Per [04 § Reminders](04-orleans-topology.md), drift detection is **per-cluster, not
per-resource**. The cluster's informer bridge holds a live view; an hourly per-cluster reminder diffs
labelled objects against the resource grains that own them (the `cybercloud.io/resource-id` label from
ADR-013 is what makes this a hash join rather than a scan) and pokes only what diverged. It also
surfaces the two things nothing else would find: **orphans** (labelled objects whose resource grain is
gone — deleted and billed for) and **strays** (resources whose objects vanished — someone `kubectl
delete`d production).

## Long-running operations

```csharp
[Alias("Rm.Operation")]
public interface IOperationGrain : IGrainWithStringKey
{
    Task<Result> StartAsync(OperationSpec spec);
    Task<Result<OperationStatus>> GetAsync();
    Task<Result> ReportAsync(OperationProgress progress);
    Task<Result> CancelAsync(string reason);
}
```

State is **durable** (ADR-003) and includes everything needed to re-drive: the resource id, the
desired body, the quota lease, the index claim, the step cursor. On activation after a silo loss the
grain re-registers its reminder and continues. This is the mechanism behind the
[00](00-vision-and-principles.md) non-negotiable *every LRO is resumable*, and the chaos suite kills
silos mid-provision specifically to test it.

Statuses are Azure's — `NotStarted`, `Running`, `Succeeded`, `Failed`, `Canceled` — plus a progress
array. Cancellation is cooperative: it sets a flag the reconciler observes, and for anything already
applied it runs the delete path. A "cancelled" create that leaves resources running is a billing
dispute waiting to happen, so cancellation *completes* rather than abandoning.

**Nested operations.** Deleting a resource group is one operation with N child operations, ordered by
the dependency graph. The parent's progress is the children's. Deployments ([01](01-azure-parity-catalogue.md) § A, M2) use the same machinery.

### Deleting a parent resource that has children

The same question one level down, and it needed answering the moment step 1 started refusing a create
whose parent does not exist. Before that check a dangling child was one of several ways a child could
be wrong; now it is the *only* one, because it is the only one the create path cannot see coming.

**Decided: a delete is refused while the resource still has children — `409`, not a cascade, and not
a silent orphan.** The refusal names how many children there are and their type, so the caller can go
and delete them. It belongs one step before the lock check, and reuses the shape `ScopeLocked` already
has — "you cannot delete this yet, here is what is holding it" — rather than inventing a second one.

Why refuse, when the resource *group* cascades:

- **A resource group is a declared lifecycle boundary and a parent resource is not.**
  [06](06-tenancy-and-resource-model.md) § The hierarchy makes the group the thing you delete in order
  to delete what is inside it; that is what a group is *for*, and deleting one says so. Nobody
  deleting a Postgres server has said anything about the databases on it.
- **The blast radius is unbounded and invisible at the call site.** `DELETE …/servers/pg-main` is a
  single-resource URL. Cascading would tear down an unknown number of resources the caller never
  named, with the data in them, and the `202` would look identical either way.
- **Quota and billing would move for resources nobody mentioned.** Each child returns its committed
  quota, so a cascade silently rewrites a subscription's usage — and the operation record would
  attribute it to a delete of something else.
- **The refusal is recoverable and the cascade is not.** A caller told "this server has 3 databases"
  can delete them and retry. A caller who cascaded by accident has nothing to retry.

⚠ **Refusing is not the safe default merely because it does less.** It creates a real failure mode: a
child whose own delete is stuck holds its parent undeletable. That is why the answer is a `409` *with
a count* rather than a bare refusal — the caller has to be able to see what is holding it.

**Implemented, and the counter is on the parent's own index grain.** The blocker recorded here was
that `IResourceIndexGrain` is path→GUID and one-way ([06](06-tenancy-and-resource-model.md) § Grain
keys), so "what are this resource's children" was answerable only from the resource-graph projection,
which is *eventually consistent* — and a delete gate reading a stale index either orphans a child it
did not see or refuses over a child that is already gone. Of the two honest options this named — a
per-parent counter maintained where the index claim and release already happen, or a
strongly-consistent child index keyed on the parent's address — **`IResourceIndexGrain` turns out to
be both at once**, so the counter lives there rather than in a grain of its own:

- It is keyed on the parent's canonical path, so it is **one activation per parent address**: no new
  key shape, no new grain type, no new cardinality question ([04](04-orleans-topology.md) § Grain
  taxonomy's review question is already answered for this key).
- It is the same activation the parent's delete calls `ReleaseAsync` on, so **"is this name taken" and
  "does it still have children" are answered by one single-threaded entity that cannot disagree with
  itself**. That is the strong consistency the projection could not offer.
- `AddChildAsync` / `RemoveChildAsync` / `ChildrenAsync` count **per child type**, not per child. A
  list of names would put one entry per child on the parent's durable row and rewrite the whole row on
  every child create; a count per type is a handful of bytes at any fan-out and still answers "how
  many, and of what", which is all the refusal needs.

**The two endpoints are the moments the child starts and stops existing, and they are deliberately
asymmetric.** The increment is on the *create*, immediately after the index claim is confirmed — a
count raised before the durable write would survive a create that then failed, and nothing would ever
lower it, so the parent would answer `409` to its own delete forever with only an operator able to
clear it. The decrement is on the *delete*, in `OperationGrain` after `CompleteDeleteAsync`, beside
the ReBAC unlink and for the same reasons: a child that is still tearing down **still exists** — 06
§ Two-phase create keeps it visible, in `Deleting`, with its meter ticking — so it must still hold its
parent, and the removal has to be re-drivable, because a count left high is worse than a dangling
tuple. `RemoveChildAsync` clamps at zero so the re-drive is safe; `ReleaseAsync` clears the counts with
the binding, so a name reused by a new resource cannot inherit the old one's children.

⚠ **The residual is a count that is one too low, and it is the recoverable direction.** A silo lost
between the index confirm and the increment leaves a child that exists and is not counted — the orphan
this gate closes, in a microsecond-wide window, and no worse than the behaviour before the gate
existed. The opposite residual, a count too high, is what the ordering above is chosen to avoid.

What the platform must *not* do instead is re-check the parent on every write to a child: that turns a
deleted parent into a frozen child which answers `404` to a `GET` for a resource that plainly exists,
which is worse than the orphan. `ParentExistenceTests.AnUpdateOfAnExistingChildDoesNotRecheckTheParent`
pins that — and it now makes the parent stop resolving through the index grain directly, because the
refusal makes "delete the parent out from under a live child" unreachable through the API while
leaving every *other* way a parent can stop resolving intact.

⚠ **The refusal carries its own error code, `ResourceHasChildren`, and the sentence above about
reusing `ScopeLocked`'s shape means the message rather than the code.** Same `409`, same "here is what
is holding it" shape — but a caller with no lock anywhere in their hierarchy must not be told to go
and find one, and every generated SDK branches on the code. Different recovery, therefore different
code.

### Soft delete: where a deleted resource lives, what happens to its name, its quota and its authorization

[06](06-tenancy-and-resource-model.md) § Tags, locks asks for **"7 days for resources carrying data
(Vault, Storage, databases)"**, and the registry can already say so — `IProviderBuilder`'s
`SupportsSoftDelete(int days)`, `ResourceTypeRegistration.SoftDeleteDays`. **Nothing in
`CyberCloud.ResourceManager` reads it**, and all five providers independently declined to declare it
for the same stated reason: a type advertising `softDeleteDays: 7` through the generated document
while delete is irreversible is worse than one advertising nothing. **That instinct is right and the
fix is not to make them declare it.** Honour it first. Four decisions come before any code, and each
one decides the ones after it.

**Decided: a soft-deleted resource moves to a different address, out of its resource group.**
Azure has no ARM-wide soft delete; each provider builds its own, and Key Vault — the canonical one —
moves the vault to `/subscriptions/{sub}/providers/Microsoft.KeyVault/locations/{loc}/deletedVaults/{name}`,
a different resource *type* at subscription+location scope, returning `"type":
"Microsoft.KeyVault/deletedVaults"` and keeping the original address only as a property. Follow it.
The alternative — the resource stays where it is with a flag — puts an "unless deleted" clause on
every read path, every list, every ReBAC check and the index claim, and the feature is then only as
good as the least-remembered of them. Moving it out of the tree is more work once and less work
forever, and it makes the sharpest failure — *a soft-deleted resource that is still readable at its
old address* — unreachable by construction rather than a thing to remember. ⚠ The `404` on the old
address must stay the **canonical** `404` § The enforcement seam in [07](07-rebac-authorization.md)
requires: a `410 Gone` would tell an unauthorized caller that the name was taken, which is the
enumeration oracle the status code exists to close.

**Decided: the name is held for the whole window.** Azure holds it — *"You can't reuse the name of a
key vault that was soft-deleted, until the retention period expires"*, DNS record included. Releasing
it is the cheaper-sounding option and it breaks restore: a name taken by somebody else leaves a
restore with nowhere to go, so it would have to fail or overwrite, and both are worse than making the
tenant wait. `IResourceIndexGrain` is where this lands and it needs one new `IndexEntryState`, not a
new mechanism: `ResolveAsync` must refuse it — so the resource is not addressable, the `404` above is
free, and § Deleting a parent resource that has children reads it correctly with no change — while
`TryClaimAsync` must refuse it too, because the name is taken.

**Decided: committed quota is NOT returned on delete for a soft-deletable type. It is returned on
purge.** ⚠ **This is the decision most easily got wrong from Azure by analogy, because Azure does
three different things and the pattern is not the one it looks like.** A soft-deleted Key Vault bills
nothing during retention and consumes no vault quota — but only because there is no vault-count quota
in the first place and a vault reserves no capacity. Where the deleted thing *does* hold capacity,
Azure holds both: Managed HSM says *"These resources remain allocated even when the HSM is in a
deleted state"* and bills *"at their full hourly rate until they're purged"*, and soft-deleted blob
data is billed *"at the same rate as active data"*. **The rule is that soft delete is free exactly
when the deleted thing consumes no reserved capacity** — and a CyberCloud resource in its recovery
window consumes plenty, because handing the data back is the entire feature: the volumes, the PVCs
and the memory are all still allocated. So the quota stays committed.

The second reason is the one that matters more than the accounting: **quota held is what makes restore
total.** A restore that re-reserves would fail against an allowance the tenant has spent in the
meantime, which is a restore that works only when it is not needed. Concretely this moves
`OperationSpec.CommittedQuota`'s return from the delete's convergence to the purge, and it moves the
whole of it — `QuotaMeter.Resources` too, even though a soft-deleted resource is not one anybody can
use, because a per-meter split reintroduces the partial restore. `DeletePathTests`'
`ADeleteReturnsExactlyWhatTheCreateCommittedOnEveryMeter` and `MeteredAmountTests`'
`TenCreateDeleteCyclesLeaveTheMetersWhereTheyStarted` are the two tests that pin the amount and the
symmetry; for a soft-deletable type they become tests of the purge, with the arithmetic unchanged.
**Billing during retention is [13](13-compute-vm-containers.md)'s to state**, and the same principle
decides it: charge for capacity that is still allocated, not for a control plane that refuses every
call.

**Decided: the parent tuple is re-parented, not preserved and not dropped. Direct role assignments on
the resource are dropped.** These are two answers because they have two different reasons, and
running them together is how this gets decided wrongly.

The parent edge first. § The write path, end to end's step 8 writes
`resource:{id}#parent@resourceGroup:{sub}-{rg}` so a new resource is visible to whoever holds a role
on its group, and `DeleteTearsDownTheDataPlaneAndTheResourceIsGone` asserts that edge is gone after a
delete. ⚠ **The first decision above already settles what happens to it, and reading the two together
is what makes this obvious: a soft-deleted resource leaves its resource group.** So the tuple does
not survive unchanged — while the resource is deleted, a tuple naming the resource group as its
parent asserts a containment that is no longer true. Preserving it is not the conservative choice, it
is the wrong one. The edge moves with the resource, to `#parent@subscription:{sub}`, and moves back on
restore.

Two things fall out, and they are why this beats both alternatives. **The resource is never
parentless**, so the failure that made the parent tuple necessary in the first place — a resource
nobody can see, and a silo lost in that window leaving it that way — cannot happen during the
recovery window either. And the people who can see a deleted resource become the people who hold
subscription-scoped rights, which is exactly who Azure gives `deletedVaults/read` and
`purge/action` to. A restore is a subscription-scoped operation; the visibility should match.

Direct role assignments on the resource are a separate question with a security answer rather than a
modelling one, and Azure's behaviour is the right one to copy: they go with the resource and
*"must be recreated"* on recovery. ⚠ **The recovery window is used after a compromise or after a
decommission somebody wants to undo, and those are the cases that decide it.** Silently restoring a
grant an administrator deliberately removed is an error nobody observes. Making somebody re-grant
after a restore is an error everybody observes and can fix in a minute. Take the visible failure.

⚠ **Both are data rather than schema, so both are cheap to reverse — but only if a test pins the
intent now.** Unpinned, the behaviour becomes whatever the implementation happened to do and nobody
can tell later which way it was decided. Two tests: a restored resource is visible to a subscription
role holder and to its resource-group role holder, and a role assignment written directly on the
resource before the delete is absent after the restore.

Three smaller things follow from Azure and are worth taking as they stand. **Purge is a separate
operation with its own permission** — Azure's `Microsoft.KeyVault/locations/deletedVaults/purge/action`
is in Key Vault Contributor's `notActions`, so "may delete" and "may destroy permanently" are
genuinely separable rights and a role can hold the first without the second. **Purge protection is a
further opt-in flag that cannot be turned off once on**, which is the only version of it that is worth
anything. And **retention is set at creation and immutable afterwards** — a window a caller can
shorten under their own resource is not a recovery window.

⚠ **Refusing a delete with children (above) is what makes all of this tractable, and the reasoning
should not be lost.** It makes *"a soft-deleted parent with live children"* unreachable, so nothing
here has to answer it. A cascade could not have: hard-deleting children while the parent is
recoverable makes restore hand back an account whose buckets are gone — worse than not restoring,
because the tenant is *told* it came back — and soft-deleting them alongside it needs a restore that
is transactional over a set, which the platform has no shape for.

⚠ **No provider should declare `SupportsSoftDelete` until the above is built.** The five stated
reasons in the tree are correct and stay correct; the declaration is the last step, not the first.

## The provider registry

```csharp
public sealed class PostgresProvider : IResourceProvider
{
    public string Namespace => "CyberCloud.DBforPostgreSQL";

    public void Describe(IProviderBuilder b) => b
        .ResourceType("servers")
            .ApiVersion("2026-08-01", schema: Schemas.PostgresServer_2026_08_01)
            .Reconciler<PostgresServerReconciler>()
            .Meters(Meter.VCpuHours, Meter.StorageGbMonths, Meter.BackupGbMonths)
            .Permissions(read: "read", write: "write", delete: "delete")
            .Action("restart",   ActionKind.Post, permission: "write")
            .Action("listKeys",  ActionKind.Post, permission: "listKeys", secret: true)
            .Chart("managed/postgres")
            .SupportsSoftDelete(days: 7)
            .SupportsTags()
        .ResourceType("servers/databases")
            .ApiVersion("2026-08-01", schema: Schemas.PostgresDatabase_2026_08_01)
            .Reconciler<PostgresDatabaseReconciler>();
}
```

This object is the source for the four generated surfaces (ADR-012) *and* for the runtime write path
— the same registry that generates the CLI is the one that validates the request body. That identity
is what makes drift impossible rather than merely detectable.

**API versions are dates and they are immutable.** Adding a field is a new version. The registry keeps
every version and each has its own schema and its own body↔state mapping; the grain's state is a
*superset* and a read at an old version projects down. Removing a version needs a 12-month notice
window and a build gate that fails on a version removed without one. Yes, this is heavy. It is the
difference between an SDK that keeps working and a platform nobody automates against.

## The resource-graph projection

The list-and-search path, and it is separate from the write path on purpose.

`resource-changed` → a projector → a per-tenant ClickHouse table:

```
resource_id, tenant_id, subscription_id, resource_group, provider, type, name,
api_version, provisioning_state, location, cluster_id, tags Map(String,String),
created_at, modified_at, desired_hash, version
```

Portal lists, filters, tag queries, "show me every Postgres in this subscription", and the M3 resource
graph API all read this. **It is eventually consistent and the portal shows that** — a freshly created
resource appears in its own blade immediately (read from the grain by id) and in the list within a
second or two. Pretending otherwise produces the classic bug where a user creates something, is
redirected to a list, and does not see it.

Access filtering on this table comes from the denormalized column maintained by
[`ListObjects`](07-rebac-authorization.md), not from a per-row `Check`.

## Errors

One shape, everywhere, Azure's:

```json
{ "error": { "code": "QuotaExceeded",
             "message": "Subscription quota for 'vcpu' in region 'eu-central' would be exceeded (requested 8, available 2).",
             "target": "properties.sku",
             "details": [ … ] } }
```

Rules that make this useful rather than decorative:

- **`code` is a stable, documented, greppable identifier.** It is part of the API contract; changing
  one is a breaking change. There is a checked-in registry and a build gate on additions.
- **`message` is for a human and names the actual numbers.** "Quota exceeded" without the meter, the
  request and the remainder is a support ticket by construction.
- **`target` is a JSON Pointer into the request body** so the portal can highlight the field.
- **No exception details, ever.** A stack trace in an error body is an information leak and a
  support-cost multiplier. The correlation id goes in the response header; the details go to the trace.

## What the resource manager deliberately does not do

| Not this | Where it lives instead | Why |
|---|---|---|
| Talk to Kubernetes | `CyberCloud.Kubernetes`, via the reconciler | The manager must work for a provider with no cluster at all (a DNS zone, a mail domain, a role assignment) |
| Render Helm charts | `CyberCloud.Kubernetes.Charts` | Same |
| Hold secrets | `ISecretResolver` → OpenBao | [05](05-state-and-storage.md) |
| Rate limit | Gateway | Per-request work must not touch a grain |
| Emit metrics/logs for tenants | Providers → `CyberCloud.Telemetry` | Volume |
| Decide *where* a resource goes | The subscription's default cluster, or the explicit `clusterId` | Placement policy is M3 and would be a scheduler; the manager just carries the id |

## Effort

| Piece | EM |
|---|---|
| Resource ids, paths, registry, schema validation, api-version machinery | 1.5 |
| Write path: locks, tags, etags, two-phase create, delete ordering | 1.5 |
| Operation grains, progress, cancellation, nesting, resumability | 1.5 |
| Reconcile scheduler, backoff, per-cluster drift, orphan/stray detection | 1.5 |
| Resource-graph projection + list/filter API | 1.0 |
| Generation pipeline: OpenAPI, CLI, SDK, forms (ADR-012) | 1.5 |
| Conformance suite | 0.5 |
| **Total** | **9.0** |

This is the number that must not be compressed. Every provider in [01](01-azure-parity-catalogue.md)
is costed at 0.6–4 EM *on the assumption that this exists and works*. Shipping a provider before the
manager is finished means shipping the manager's missing half inside the provider, twenty times.
