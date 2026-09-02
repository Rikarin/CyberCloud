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
   7b. IResourceGroupGrain.BeginCreateAsync(address)           → 404 if the group does not exist
         → records the resource as a member in Creating — 06 § Two-phase create, step 2
         → create only; a retried PUT does not move a live member back to Creating
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

**Step 7b is a step the write path did not have, and adding it added a refusal.**
`IResourceGroupGrain` has owned `BeginCreateAsync`, `CompleteCreateAsync`, `BeginDeleteAsync`,
`FailDeleteAsync`, `CompleteDeleteAsync`, `ListAsync` and `ListOrphansAsync` since
[06](06-tenancy-and-resource-model.md) § Two-phase create was written, and nothing in the platform
called any of them: the write path claimed the index, linked the ReBAC edge and submitted desired state
without recording membership anywhere. Every group's membership was therefore empty, which is worse
than a missing feature — a listing built on it would have answered "this group has no resources" for a
group full of them, and been internally consistent while doing it. The same emptiness is why the
reaper reminder that document asks for could not have worked: `ListOrphansAsync` is what it reads.

The refusal is the part that is new *behaviour* rather than new bookkeeping. A `PUT` into a resource
group nobody had created used to succeed — step 1 validates the tenant, the subscription, the type, the
api-version and a child's parent *resource*, and never the group — leaving a resource that belonged to
nothing, inherited no lock or role assignment from a group, and could not be reached by anything
walking the hierarchy downwards. It now answers `404`, which is Azure's behaviour and the same answer a
group the caller may not see gets.

⚠ **The refusal made a gap that already existed reachable one step sooner, and the gap was not the
resource manager's.** `ISubscriptionGrain.CreateResourceGroupAsync` existed with no production caller;
neither had `ISubscriptionGrain.CreateAsync` or `ITenantGrain.CreateAsync`, and the gateway served no
route that reached any of them — `GatewayRoute` parsed resources, actions, operations, the hub and the
OpenAPI document, and nothing else. So on a real silo a tenant, a subscription and a resource group all
had to be made by an operator calling grains directly, and step 1 has refused a write into a
subscription nobody made since it was written. Step 7b refuses one into a group nobody made, in the same
way and for the same reason.

**That gap is now closed for two of the three, by a component beside this one rather than inside it.**
`IScopeManager` serves a **scope** address — `/tenants/{t}/subscriptions/{s}` and that plus
`/resourceGroups/{rg}`, the first four and six segments of § Identifiers' path — under a sixth
`RouteKind`. It is not this write path and deliberately not: eight of the twelve steps have nothing to
act on for a scope (no provider, so no registry lookup; no schema per api-version; no meter; no index
entry, since a group's name is made unique by the subscription's own activation; no membership record;
no desired state, no reconciler and therefore no operation), and giving `WriteTrace.Canonical` a second
legal shape would cost the trace the property it exists for. What a scope *does* keep is steps 3, 4, 8
and 11 in that order — the check, the lock, the ReBAC edge and the change event.

**A tenant is the third and stays outside the request pipeline, which is a decision rather than the
remainder of the work.** [10](10-gateway-and-api.md) § Request pipeline resolves a request's tenant
from the token and answers `404` to every surface naming a different one — the path included, read
straight off the `/tenants/{id}` prefix — so a request that created tenant B would have to name B in a
path that has already been refused. Exempting the route means letting a caller-controlled value select
the tenant, which is the one change that document says must never be made. Tenant creation is therefore
`IScopeManager.CreateTenantAsync`: a platform-operator seam off the request path, checked against
[06](06-tenancy-and-resource-model.md) § Platform administration's `platform:root#operator` — the first
caller that relation has ever had — and taking the tenant's first owner **in its request** rather than
defaulting it to the operator, because `tenant` is the only type `CyberCloudSchema` gives no `parent`
relation and a direct `#owner` tuple is the only thing that can make a new tenant visible to anybody.
§ Platform administration's own answer, that tenants are a `CyberCloud.Platform/tenants` resource under
the platform tenant, remains where this should eventually move; it cannot be where it starts, because
that route's path names the platform tenant's own subscription and resource group and those are scopes.

**The position is between 7 and 8 and it is the recoverable one.** A member recorded before the durable
write and then abandoned is exactly the orphan `ListOrphansAsync` enumerates; a durable resource in no
group's membership is invisible to every listing and to the reaper both, permanently, with its meter
running. That is step 8's trade one line earlier. Unlike step 8, the refusal here *releases* the index
claim rather than leaving it to expire: "the group does not exist" is a standing fact about the request
rather than a transient failure, so the caller's next move is to create the group and send the identical
`PUT` — and a claim left to expire would answer that retry with a `409` naming a GUID that belongs to no
resource.

**The endings are the operation grain's, and there are four of them.** A converged create, update or
restore stamps the member with the terminal state the resource reached, which is what keeps the reaper
off a live resource. A converged delete or purge removes it — *after* `CompleteDeleteAsync`, never at
the accept, because a resource whose teardown is still running is still there. A failed teardown pass
goes through `FailDeleteAsync`, which cannot remove the member and cannot move it to `Failed`: both
would make the resource look finished while its pods still run, which
[06](06-tenancy-and-resource-model.md) § Two-phase create calls "a billing-dispute prevention measure as
much as a correctness one". Any other terminal failure stamps `Failed`. A soft delete removes the member
when it parks, because a parked resource hangs off its subscription rather than its group, and a restore
puts it back in `Creating` until its own operation converges.

**What this does not deliver is the reaper or a listing.** `ListOrphansAsync` now has something to
return and still has no production caller; `ListAsync` on the group is likewise uncalled, and
`IResourceManager` has no `ListAsync` at all, so the portal's resource list, `cyc resource list` and any
SDK enumeration still have nothing to call. What changed is that the inventory those three would read is
now populated — before this, each of them could have been built, shipped, and answered "empty" for every
group correctly.

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

⚠ **STATUS, BECAUSE THE PARAGRAPH ABOVE IS THE PROBLEM STATEMENT AND NOT THE STATE OF THE TREE.** The
mechanism is built and the manager reads `SoftDeleteDays`, so the reason to decline has expired.
Four types now declare a window, all at seven days: `CyberCloud.DBforPostgreSQL/servers`,
`CyberCloud.DBforMySQL/servers`, `CyberCloud.Storage/accounts` and
`CyberCloud.ContainerRegistry/registries`, plus `CyberCloud.Monitor/workspaces`, which also declares a
purge-protection pointer. Two declines stand and are decisions rather than omissions:
`CyberCloud.Cache/redis`, because a Valkey cache's data is reconstructible from its source of truth
and a window would charge a tenant for a recovery nobody asked for; and
`CyberCloud.ContainerService/managedClusters`, whose own refusal reads *"a soft-deleted cluster whose
worker VMs are gone is not a cluster anybody can be handed back."* `CyberCloud.KeyVault/vaults` is
the strongest case in the catalogue and does not exist yet.

**Decided: a soft-deleted resource stops resolving at its address. It does not move to a new one,
because this platform has no address for it to move to.**

⚠ **REWRITTEN 2026-08-18, AFTER THE IMPLEMENTATION. This decision was "a soft-deleted resource moves
to a different address, out of its resource group", following Key Vault, and it cannot be built as
written.** The reasoning was sound and the platform it assumed is not the one that exists. Azure has
no ARM-wide soft delete; each provider builds its own, and Key Vault — the canonical one — moves the
vault to
`/subscriptions/{sub}/providers/Microsoft.KeyVault/locations/{loc}/deletedVaults/{name}`, a different
resource *type* at subscription+location scope. **There is no subscription-scoped address in
CyberCloud at all.** `ResourceId.ParsePath` has `const int fixedPrefix = 8` and checks the literals
`tenants`, `subscriptions`, `resourceGroups` and `providers` at fixed indices, so `resourceGroups/{rg}`
is mandatory in every path the platform can parse and the shortest legal one is ten segments.
`GatewayRouter.ResolveResource` and `ResolveAction` both go through it, so nothing can be *addressed*
that it refuses; and the index grain key, the ReBAC object ids and the OpenAPI path templates all key
on `ResourceId`, so a second address shape is four changes rather than one. Following Key Vault here
was a decision about somebody else's identifier grammar.

**What was built instead: `IndexEntryState.SoftDeleted`, and two refusals.** `IResourceIndexGrain`
gains one state — no new mechanism, which is what the next decision already predicted — and
`ResolveAsync` refuses it, so the resource is not addressable and the old address answers `404`; and
`TryClaimAsync` refuses it, so the name is held. **That delivers the property the original decision
was *for*.** Its argument against a flag was that "unless deleted" would have to be remembered on
every read path, every list, every ReBAC check and the index claim, and the feature would be only as
good as the least-remembered of them. One state on the index and one refusal in the resolver reaches
the same place: everything downstream resolves a name to a resource *through* `ResolveAsync`, so
nothing downstream ever learns that soft delete exists. It is not a flag every reader has to
consult — it is a resolution that stops. ⚠ The `404` on the old address is the **canonical** `404`
§ The enforcement seam in [07](07-rebac-authorization.md) requires: a `410 Gone` would tell an
unauthorized caller that the name was taken, which is the enumeration oracle the status code exists to
close.

⚠ **AND THE PURGE COULD NOT BE PERFORMED BY ANYBODY UNTIL 2026-08-19, WHICH IS A SHARPER FINDING
THAN ANY OF THE OWED ITEMS BELOW.** The paragraph above separates "may delete" from "may destroy
permanently" and gives the second its own permission name; `CyberCloudSchema` defined no such
permission on `resource`, and an undeclared permission evaluates false. § The enforcement seam in
[07](07-rebac-authorization.md) turns a false into the canonical `404`, so every purge answered *"does
not exist"* — to every caller, for ever — and a recovery window therefore had no end: the name stayed
held and the committed quota was never returned. **Every purge test here ran against a doubled
authorizer**, which answers whatever its author believed about a permission name, so the whole feature
tested green. `test/CyberCloud.Isolation` drove one through the real schema and found it;
[07](07-rebac-authorization.md) § Azure RBAC, expressed in it records the fix and what it does and
does not deliver.

⚠ **What is genuinely lost, and it is not the address — it is the place a tenant lists and reaches
what is recoverable.** Key Vault's `deletedVaults` is a collection you can `GET`, which is how an
operator finds the thing they are about to restore. Here, the recoverable resource is by construction
not addressable, and there is no second collection it is addressable *in*. Concretely:

- **`RestoreAsync` and `PurgeAsync` exist, are implemented on `ResourceManagerService`, and are
  covered by `SoftDeletePathTests` — and neither had an HTTP route.** `DispatchStage` called
  `ReadAsync`, `WriteAsync` and `DeleteAsync` and nothing else, so both verbs were reachable from
  in-process callers and tests only. ⚠ **CLOSED, and the fix was a registry fact rather than a
  route.** `POST {resource}/restore` and `POST {resource}/purge` now reach them, and no file in
  `CyberCloud.Gateway.Host` changed to make that true. `ProviderBuilder.Build` appends two
  `ActionRegistration`s to every type that declares `SupportsSoftDelete` —
  `SoftDeletePolicy.RestoreAction` and `PurgeAction`, both long-running, under the type's *write* and
  *purge* permissions respectively — and three things follow from that one array. The gateway's stage
  6 already answers the canonical `404` to an action `TryGetAction` does not know, so the route opens
  for exactly the types with a window and for no others; `DispatchStage` already forwards a declared
  action to `IResourceManager.ActionAsync`; and ADR-012's four surfaces already emit one path per
  declared action, so the OpenAPI path, the `cyc` verb, the SDK method and the portal's action button
  all appeared without an emitter learning what soft delete is. Ten paths were added to `2026-08-01`
  and the compatibility gate reported nothing, because it refuses removals rather than additions.

  ⚠ **The fork lives in `ResourceManagerService.ActionAsync`, above its own step 1 and below the
  gateway**, and both halves of that placement are load-bearing. Above step 1 because step 1 reads
  `IResourceIndexGrain.ResolveAsync`, which refuses a parked binding — for exactly the resources these
  two verbs exist to act on, resolution answers "not found", and that refusal is the canonical `404`
  and must not be relaxed. Below the gateway because the two test suites meet at `IResourceManager`:
  a fork in `DispatchStage` would bind the route for HTTP callers only and would be provable only
  against the *substituted* manager. `ActionRoutingTests.SoftDeletesTwoVerbsAreReachableOnPost` proves
  the gateway calls `ActionAsync` with the name; `SoftDeletePathTests.TheRestoreAndPurgeActionsReach`
  `TheSoftDeletePath` proves the real manager answers that call by restoring the resource at its old
  address from the body the create wrote. Deleting the fork turns the second one red and nothing else.

  ⚠ **A provider may not declare either name** — `ProviderBuilder.Action` throws. A provider's
  `restore` would publish a permission, a request shape and a handler that nothing reads, and the two
  declarations would be indistinguishable in the generated document. The refusal covers types with no
  window too, so that adding `SupportsSoftDelete` to an existing type is never the change that breaks.
- **There is no list.** `IResourceIndexGrain.ResolveSoftDeletedAsync` answers "which resource was
  parked under this name", which is the question you can only ask if you already know the name. A
  tenant who has forgotten it has no way to enumerate the window. ⚠ **And the resource manager has no
  listing of *live* resources either** — `IResourceManager` declares no `ListAsync` and nothing in
  the tree enumerates a resource group — so this is not a hole soft delete opened. A collection
  endpoint is a platform feature that every type needs, and the soft-deleted collection is one
  filter over whatever answers it.

**Closing that is an addressing change, not a soft-delete change**, which is why it is recorded here
rather than solved in passing: it needs either a subscription-scoped path shape that
`ResourceId.ParsePath` can parse, or a collection endpoint that is not a `ResourceId` at all. Until
then the recovery window is real, tested and reachable by an operator with the resource's own path —
and invisible to the tenant it exists for.

⚠ **AND THE ENUMERATION SOURCE A `ListAsync` WOULD READ DOES NOT EXIST EITHER, WHICH IS THE FINDING
THE NEXT ATTEMPT NEEDS BEFORE IT STARTS.** The obvious source is `IResourceGroupGrain`, which owns
membership and declares `ListAsync`, `BeginCreateAsync`, `CompleteCreateAsync`, `BeginDeleteAsync`,
`CompleteDeleteAsync` and `ListOrphansAsync` — the resource-group half of § Two-phase create in
[06](06-tenancy-and-resource-model.md), fully implemented and covered by `TwoPhaseCreateTests` and
`DeleteOrderingTests`. **Nothing in production calls any of them.** `CyberCloud.ResourceManager`
touches that grain in exactly one place, `Defaults.cs`, and only for `GetAsync` to read the group's
lock; the write path claims the index, links the ReBAC parent edge and submits desired state without
ever recording the resource as a member. So a `ListAsync` built over the group's inventory today
would answer with an empty collection for every group in the platform, and it would answer *correctly*
— which is the worst shape a listing can have, because an empty list is indistinguishable from a
group with nothing in it. The reaper reminder § Two-phase create describes is in the same position:
`ListOrphansAsync` is what it reads and nothing reads it.

**So `ListAsync` is two changes and only one of them is a listing.** The first is wiring the
membership choreography into the write path and the operation grain — `BeginCreateAsync` alongside the
index claim, `CompleteCreateAsync` at the terminal state, `BeginDeleteAsync` at the delete's accept,
`CompleteDeleteAsync` where the hard delete and the purge already clear the resource grain. That is a
new failure mode rather than a new method: a write to a resource group that was never created would
start being refused, which is Azure's behaviour and is not today's. The second is the collection
endpoint itself, and it needs an authorization filter built from `Check` per member — ReBAC's
`ListObjects` is M2, [24](24-roadmap.md) § Phase 3 — or the listing becomes a way to read another
tenant's resource names. **A third cost is worth stating because it is invisible from the manager:**
a collection path reaches ADR-012's surfaces and none of them can carry it. `DocumentReader.TypesOf`
keys a resource type on `x-cybercloud-resource-type` with no `x-cybercloud-action`, so a collection
path item carrying that extension reads as a *second* type with the same name — `CliEmitter` throws on
the duplicate command, `SdkEmitter` throws on the duplicate model. The reserved-action route above
needed no emitter change precisely because it is an action; a list is not.

**Decided: the name is held for the whole window.** Azure holds it — *"You can't reuse the name of a
key vault that was soft-deleted, until the retention period expires"*, DNS record included. Releasing
it is the cheaper-sounding option and it breaks restore: a name taken by somebody else leaves a
restore with nowhere to go, so it would have to fail or overwrite, and both are worse than making the
tenant wait. `IResourceIndexGrain` is where this lands and it needs one new `IndexEntryState`, not a
new mechanism: `ResolveAsync` must refuse it — so the resource is not addressable, the `404` above is
free, and § Deleting a parent resource that has children reads it correctly with no change — while
`TryClaimAsync` must refuse it too, because the name is taken.

**Decided: a soft delete tears the data plane down. What the window preserves is everything a
teardown does not remove.**

⚠ **ADDED 2026-08-18, AFTER TWO PROVIDERS DECLARED A WINDOW, MEASURED WHAT A TENANT GOT, AND
WITHDREW.** This section had no paragraph about the data plane at all, and the sentence below about
quota was read as one: *"a CyberCloud resource in its recovery window consumes plenty, because handing
the data back is the entire feature: the volumes, the PVCs and the memory are all still allocated."*
That is a true sentence about capacity and it was implemented as a rule about objects.
`OperationGrain.DriveAsync` returned before running any reconcile pass for a soft delete, so a
soft-deleted resource kept every object it had applied: the tenant's pods kept running, the meters
kept ticking, and the address answered `404` so the tenant could not see the resource in order to
delete it again. `CyberCloud.ContainerRegistry/registries` found it with fifteen Harbor objects and
`CyberCloud.Monitor/workspaces` found it with a `VMUser` that vmauth resolves the moment it is applied
— an authenticated, billed, open write path into a store the tenant believed was gone. Both withdrew,
and both were right to: **a delete that does not delete is worse than no recovery window.**

**The two claims are separable and only one of them was true.** The registry reported that the
resource *reconciled its whole data plane back* — an active re-apply — and the workspace could not
reproduce that on its own row and recorded the discrepancy rather than inheriting the answer. The
workspace's reading held. The registry's evidence was a conformance assertion that reports an **end
state**, and an end state cannot distinguish *never torn down* from *torn down and re-applied*; the
two are different bugs in different code, and the fix went to the first. ⚠ **The lesson is about
evidence rather than about soft delete: an assertion that observes a result cannot attribute a
mechanism, and a write-up that names one anyway sends the next reader to the wrong file.**

**So the teardown runs, with `tearingDown` true, exactly as a hard delete's does — and everything that
makes the delete soft happens after it converges.** The name is held rather than released, the
committed quota is kept rather than returned, the resource grain keeps its desired state rather than
being cleared, and the ReBAC parent edge moves to the subscription. Those four are the recovery
window. The teardown is not one of them.

**What a restore restores from is the half a teardown never touches**, and it is enough:

- **The disks.** Deleting a `StatefulSet` does not delete the `PersistentVolumeClaim`s its
  `volumeClaimTemplate` created, which is Kubernetes' own behaviour rather than any provider's. A
  registry's images, its metadata database and its job queue are all still there.
- **The desired state.** `CompleteDeleteAsync` is what clears a resource grain and a soft delete does
  not call it, so the body the create wrote is still exactly what a restore applies — byte for byte,
  with no caller supplying anything.
- **The committed quota**, which is what makes the restore total: it cannot fail against an allowance
  the tenant has spent since.
- **The credentials**, because `ISecretWriter` mints once and has no delete.

⚠ **A restore is therefore a long-running operation and `RestoreAsync` answers `202`.** It used to
answer synchronously and correctly, because it did nothing — a design that tears the data plane down
and cannot put it back has not implemented soft delete, it has implemented a slower delete. It starts
an `OperationKind.Restore` over the stored body, reserving nothing.

⚠ **Two things followed that were owed rather than done, and the first is now built.** A purge left
the volumes, because ending a window has to remove exactly what a teardown keeps and
`IResourceReconciler` had no member that asks for that — so a purged resource returned its quota and
left its disks.

⚠ **CLOSED 2026-09-02, AND THE SHAPE OF THE FIX IS THE PART WORTH KEEPING.**
`IResourceReconciler.RetainedVolumesAsync` is that member. The provider **names** the claims and the
labels that prove them; the manager **destroys** them, through one `VolumeReclaimer` rather than once
per provider — because this is the only path on which the platform is supposed to destroy a tenant's
data, and a volume named by pattern rather than by ownership is how the wrong one goes. The reclaimer
reads every claim back from the API server before it deletes anything, checks each declared label
against the object the server is holding, and refuses the **whole** reclaim, non-retryably, naming the
claim and the label that disagreed. `RetainedVolumeTests` makes every one of those refusals fire and
asserts the claim is still there afterwards.

- **Where it runs.** The branch of `OperationGrain.ConvergedAsync` that a hard delete and a purge
  share, which a soft delete does not reach — it returns one branch above at `ParkAsync`. So the same
  line closes a second leak the owed item did not name: **a hard delete of a type with no window left
  its claims too**, because deleting a `StatefulSet` leaves them whether a window is involved or not.
  `CyberCloud.Messaging/natsClusters` is the first type to be fixed by that half.
- **Where in the order, and both halves are forced rather than chosen.** *Before*
  `IResourceGrain.CompleteDeleteAsync`, because the claim names are derived from the desired body and
  that call is what throws the body away. *Before* `ReturnCommittedQuotaAsync`, because this step can
  fail: returning the allowance first and failing second would let a tenant spend a budget their own
  disks are still occupying. A purge that half-succeeds is left `Deleting`, with the reason on the
  resource, its name already released and its quota still held — and the retry re-reads every claim,
  so an interrupted reclaim costs a pass rather than correctness.
- **The evidence is the set's `spec.selector.matchLabels`, not ADR-013's seven, and that is a
  workaround with a stated end.** `KubeCommandBuilder.Inject` writes the seven into a document's
  top-level `metadata.labels` and does not descend into a nested `volumeClaimTemplate`, so a claim the
  `StatefulSet` controller creates carries none of them — and `IKubeClusterConnection` has no list
  member either, so a label selector could not find one today even if it were labelled. What a claim
  *does* carry is the set's selector, which Kubernetes copies onto every claim the template produces.
  The failure direction is the safe one: a claim that came back without those labels is **refused**
  and its disk survives. When the seven reach the template, a provider moves the declaration to
  `cybercloud.io/resource-id` and nothing else changes.
- **⚠ What this unblocks is `NamespaceReclaim`, and that is a bigger consequence than the disks.**
  `NamespaceReclaim.Decide` refuses unless the namespace holds *nothing at all*, so a resource group
  that had ever run a stateful type reported `OperatorReclaimable` for ever — the claims were the
  permanent occupant. Removing them at the final teardown is what makes `Deletable` reachable.

⚠ **What remains owed here is narrower than what it replaced, and it is two things.** **A type whose
claims belong to an operator cannot name them.** Of the five types declaring a window, only
`CyberCloud.ContainerRegistry/registries` renders its own `StatefulSet`s;
`CyberCloud.DBforPostgreSQL/servers`, `CyberCloud.DBforMySQL/servers` and `CyberCloud.Storage/accounts`
apply a CloudNativePG `Cluster`, a `MariaDB` and a `Seaweed` respectively, and nothing in this
repository records how those operators name the claims they create — so their purges still leave their
disks, and each closes its own by writing the naming rule down and declaring it.
⚠ **`CyberCloud.Monitor/workspaces` is not in that list and that is a finding rather than an
omission:** it renders a `ConfigMap`, a `Secret` and a `VMUser` and creates no claim at all, so this
gap never applied to it. And **a set scaled down before it was deleted leaves the claims of the
ordinals it shed**, which the desired body cannot name — probing past the replica count would mean
deleting objects nothing in the desired state accounts for, with the guard as the only thing between
that and a tenant's data.

And **nothing sweeps an expired window**: an entry past `RecoverableUntil` refuses a
restore and holds its name and its committed quota until somebody purges it by hand. It no longer
holds a running data plane, which is what made it urgent. ⚠ **What a sweeper needs before it can be
built is a decision this section cannot take on its own: an expiry is not a request, so there is
nobody to authorize it, and `PurgeAsync` checks `PurgePermission` against a caller.** Either the
platform gains a system principal, or the purge splits into an authorized front and a mechanism the
clock may drive. Both are decisions about who the platform is when it acts for itself, which is
[07](07-rebac-authorization.md)'s question rather than this one's.

**Decided: committed quota is NOT returned on delete for a soft-deletable type. It is returned on
purge.** ⚠ **This is the decision most easily got wrong from Azure by analogy, because Azure does
three different things and the pattern is not the one it looks like.** A soft-deleted Key Vault bills
nothing during retention and consumes no vault quota — but only because there is no vault-count quota
in the first place and a vault reserves no capacity. Where the deleted thing *does* hold capacity,
Azure holds both: Managed HSM says *"These resources remain allocated even when the HSM is in a
deleted state"* and bills *"at their full hourly rate until they're purged"*, and soft-deleted blob
data is billed *"at the same rate as active data"*. **The rule is that soft delete is free exactly
when the deleted thing consumes no reserved capacity** — and a CyberCloud resource in its recovery
window consumes plenty: its volumes are still allocated and its name is still held. ⚠ **This sentence
used to end "the volumes, the PVCs and the memory are all still allocated", and the memory was the
half that was wrong.** A parked resource runs nothing — the section above tears its data plane down —
so no compute is reserved and a `vcpu` or `memoryGb` amount held through a window is an amount held
for capacity nobody has. It is held anyway, and the reason is the paragraph below rather than this
one: a per-meter split reintroduces the partial restore.

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
delete.

⚠ **REWRITTEN 2026-08-18. THE CONCLUSION SURVIVED ITS OWN IMPLEMENTATION AND THE ARGUMENT FOR IT DID
NOT.** This paragraph read: *"The first decision above already settles what happens to it … a
soft-deleted resource leaves its resource group. So the tuple does not survive unchanged — while the
resource is deleted, a tuple naming the resource group as its parent asserts a containment that is no
longer true."* **The resource does not leave its resource group.** The decision that said it would
could not be built — see above — and what shipped keeps the `ResourceId` exactly as it was, resource
group included. So the tuple *would* have survived unchanged, and it is re-parented anyway, for two
reasons that never depended on the address:

- **An unaddressable resource's resource-group edge grants nothing.** That edge exists so a role
  holder on the group can reach the resource, and while the entry is `SoftDeleted` nothing resolves
  the name, so there is nothing to reach. Leaving the edge in place would preserve a grant with no
  object behind it — not wrong, but not load-bearing either, and a tuple that grants nothing is one
  nobody can reason about later.
- **Subscription-scoped visibility is the visibility a restore actually needs.** A restore is a
  subscription-scoped operation: the caller is someone who can see across resource groups, because
  the group's own members can no longer see the thing at all. Moving the edge to
  `#parent@subscription:{sub}` makes the set of people who can see a deleted resource the set who
  hold subscription-scoped rights, which is exactly who Azure gives `deletedVaults/read` and
  `purge/action` to.

So the edge moves to `#parent@subscription:{sub}` and moves back on restore —
`SoftDeletePathTests.TheParentEdgeMovesToTheSubscriptionWhileDeletedAndBackOnRestore` pins it.
⚠ **The lesson is about design records rather than about tuples: this argument leaned on a neighbouring
decision instead of on its own reasons, so when that decision turned out to be unbuildable the
conclusion was left standing on nothing.** It was still the right conclusion. Nobody reading it could
have told.

One more thing falls out, and it is why moving the edge beats dropping it. **The resource is never
parentless**, so the failure that made the parent tuple necessary in the first place — a resource
nobody can see, and a silo lost in that window leaving it that way — cannot happen during the
recovery window either.

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

### Reclaiming a resource group's namespace, and why the recovery window forbids the obvious version

Every non-teardown reconcile pass creates `{subscriptionId:N}-{resourceGroup}` on the cluster it is
about to write to (`NamespaceEnsurer`), and nothing removes it, so a tenant who deletes every resource
in a group leaves a labelled empty namespace per cluster the group ever touched, indefinitely. Small
per group; unbounded over time. `src/Providers/README.md` § Namespaces carries the mechanism. What
belongs here is the interaction with the section above, because **it is the reason the cheap version
of this cleanup cannot ship**.

**Decided: a namespace is deletable only when it holds nothing at all, and never when it holds only
objects the platform did not write.** The tempting rule is the second one — delete when nothing carries
`cybercloud.io/managed-by` — and it destroys a tenant three ways. It deletes the objects of a resource
whose membership was never recorded, which is every resource today. It deletes the objects of a
resource that is live and simply not being deleted. And it deletes the volumes of every resource
inside its recovery window, which is the one this section owns: **a soft-deleted resource's data plane
is torn down and its `PersistentVolumeClaim`s are what a restore restores from**, so a namespace
delete during a window turns every restore in that group into a lie — and the tenant is *told* it came
back, which § Deleting a parent resource that has children already names as worse than not restoring.

⚠ **The volume claims carried none of ADR-013's seven labels, and the half of that which is now closed
does not move the design.** `KubeCommandBuilder` injected the labels into an object's own
`metadata.labels` and did not descend into a `volumeClaimTemplate`, so the claims the StatefulSet
controller created from it were invisible to any managed-only listing — including the drift inventory
— and read as *foreign* to any rule that tests for `managed-by`. `IKubeCommandBuilder.WithTemplateLabels`
now stamps the template with the six labels that cannot change for the life of a resource;
`src/Providers/README.md` § Labelling a nested claim template carries the mechanism, why the seventh is
excluded, and what stays owed — chiefly that **every claim that already exists is unlabelled and stays
that way**, because the StatefulSet controller labels a claim once, at creation. The conclusion here is
unchanged and does not depend on the labels: **the namespace of a group that ever ran a stateful type
never becomes empty**, because the paragraph above records that a purge still leaves the volumes and
`IResourceReconciler` has no member that asks for them. So for exactly those groups the answer is not a
delete at all — it is to record the namespace as reclaimable and let an operator decide, which is what
`NamespaceReclaim.OperatorReclaimable` reports. ⚠ **That flag's predicate now needs the same owed
work**: it requires every occupant to be unmanaged, which was true only while the claims were
unlabelled. **Making the purge remove the disks it kept is what would turn that back into a delete**,
and it is the same owed item, reached from the other end.

**What exists: the rule, the seam and the gate. What does not: a caller.** `NamespaceReclaim.Decide`
weighs the group's members against a listing of everything in the namespace;
`NamespaceEnsurer.DeleteAsync` refuses without a verdict that says both are empty and that names this
cluster and this namespace. Three things keep it uncalled, and each is a decision somebody else's task
has to take:

- **There is no group delete.** `IResourceGroupGrain` has `BeginDeleteAsync`/`CompleteDeleteAsync` for
  its *members* and no method that deletes the group itself. Whether a group refuses while it holds
  members or cascades is open; the ordering is not, and it is § Two-phase create in reverse — seal the
  group so it stops accepting members, then the members, then the namespace last. Sealing first is
  also the only thing that closes the race where a resource is created between the listing and the
  delete.
- **Membership is not recorded**, so `IResourceGroupGrain.ListAsync` answers empty for every group and
  half the evidence is vacuous. A delete wired to it today would fire on every populated group in the
  platform.
- **Nothing can list a namespace.** `IClusterObjectInventory` selects on `managed-by`, which excludes
  the objects the decision has to find; `INamespaceInventory` is the right question and its only
  implementation refuses, because an empty listing is a licence to delete. A real one needs a
  discovery of every namespaced `APIResource` the cluster serves and a list per kind, which
  [09](09-kubernetes-fabric.md) § Observing's label-selected informer does not provide.

⚠ **And the delete is not a local act even once all three land.** `NamespaceEnsurer` memoises "this
namespace exists" per silo for an hour, so a namespace deleted on one silo stays believed-in on the
others and a group whose namespace is reused immediately fails its reconciles elsewhere until the memo
expires. Whatever calls the delete needs an invalidation the memo has no channel for.

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
| Hold secrets | `ISecretResolver` (read) and `ISecretWriter` (mint-once) → OpenBao | [05](05-state-and-storage.md) |
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
