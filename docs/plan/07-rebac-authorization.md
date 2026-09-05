# 07 — ReBAC Authorization

Per ADR-007: a relationship-based authorization engine, written here, in C#, over grains. This
document is what that means concretely, including the three parts that are harder than the papers make
them look.

## The model

Three concepts and nothing else.

**An object** is `type:id` — `resourceGroup:9f2c…`, `user:1a4b…`, `subscription:77de…`,
`cluster:c001…`. Types come from the schema; ids are GUIDs.

**A relation tuple** is `object#relation@subject`, where the subject is either an object or a
*userset* (`group:eng#member` — "the members of eng"):

```
resourceGroup:prod#owner@user:alice
resourceGroup:prod#reader@group:eng#member
subscription:main#contains@resourceGroup:prod
group:eng#member@group:platform#member
```

**A schema** defines, per object type, which relations exist and how each is *computed* — directly
from tuples, or rewritten from others.

```csharp
Schema.DefineType("resourceGroup")
    .Relation("parent")                                    // direct only
    .Relation("owner",       This | From("parent", "owner"))
    .Relation("contributor", This | From("parent", "contributor") | Rel("owner"))
    .Relation("reader",      This | From("parent", "reader")      | Rel("contributor"))
    .Permission("delete",    Rel("owner"))
    .Permission("write",     Rel("contributor"))
    .Permission("read",      Rel("reader"))
    .Permission("assignRole", Rel("owner") & !Rel("suspended"));
```

`From(x, y)` is Zanzibar's *tupleset-to-userset*: "whoever has `y` on the object I point to via `x`".
It is the whole of hierarchical inheritance and it is why a role assignment at a subscription grants
on every resource group in it without any *role* tuple being written per resource.

**But `parent` itself is a tuple, and something has to write it.** "No tuple per resource" is a claim
about role tuples and it stays true; it is not a claim about the pointer the rewrite follows. A
resource with no `parent` tuple is a resource the walk cannot leave, so it inherits nothing, so its
own creator cannot read it.

**The resource manager writes that edge, at step 8 of [08](08-resource-manager.md) § The write path,
end to end, and nothing else in the platform does.** It is `resource:{id}#parent@resourceGroup:{sub}-{rg}`
— the parent is the **resource group**, not the subscription, because this schema's chain is
resource → resourceGroup → subscription → tenant and an edge that skipped the group would make every
`resourceGroup:R#contributor` assignment in the table below grant nothing on the resources inside it.
The manager is the right owner for two reasons: it is already the one place a tenant's intent enters
the platform, and it is the only component that knows the resource's GUID before the resource exists,
which is what lets the edge be written *before* the durable state rather than after it. Providers do
not write tuples, for the same reason § The enforcement seam says they do not read them. The manager
removes the edge when the resource is gone; where and why is [08](08-resource-manager.md)'s to state,
because it is a property of the delete choreography rather than of the model.

The schema is **C# and compiled**, not a DSL file. It gets type checking, IDE navigation, and — the
real reason — the analyzer can then verify that every `[Authorize]` on a provider's application service
names a permission that exists. A typo'd permission name in a text DSL is a silent allow-nothing or,
worse in the wrong evaluator, a silent allow-everything.

## Azure RBAC, expressed in it

The catalogue promise from [01](01-azure-parity-catalogue.md) is that Azure-shaped role assignments
are a *view* over this. They are:

| Azure | Tuple |
|---|---|
| `Owner` on subscription `S` for user `U` | `subscription:S#owner@user:U` |
| `Contributor` on resource group `R` for group `G` | `resourceGroup:R#contributor@group:G#member` |
| Inheritance sub → rg → resource | The `From("parent", …)` rewrites; no *role* tuples written per resource — one `parent` edge is, by the resource manager, at step 8 of [08](08-resource-manager.md) |
| Deny assignment | `#suspended`, and the `& !Rel("suspended")` in the permission |

The API can present `GET /roleAssignments` by listing tuples whose relation is a named role, which is
how the portal shows a familiar screen over an unfamiliar engine. The reverse direction — expressing
`resourceGroup:prod#reader@group:eng#member` in Azure RBAC — is not possible, which is the argument
for building this rather than a role table.

⚠ **`purge` is the sixth permission, added at `SchemaVersion` 2, and how it came to be missing is the
more useful half.** [08](08-resource-manager.md) § Soft delete gives a purge its own permission —
`SoftDeletePolicy.DefaultPurgePermission` is `"purge"` — and `ResourceManagerService.PurgeAsync`
checks it through the real authorizer. The schema declared `read`, `write`, `delete` and `assignRole`
on `resource` and nothing else. **A permission this schema does not declare can only evaluate false,
and § The enforcement seam turns a false into the canonical `404`** — so on a real silo every purge
answered *"does not exist"*, to every caller, permanently: the name stayed held, the committed quota
was never returned, and a recovery window had no way to end. The two constants live in assemblies
that do not reference each other (`CyberCloud.ResourceManager.Contracts` does not reference
`CyberCloud.Authorization.Contracts`), so nothing in the compiler could say they had drifted, and
**every purge test in the repository ran against a doubled authorizer** — which answers whatever its
author believed about a permission name. `test/CyberCloud.Isolation` is what drove one through this
schema and found it, which is the second defect that project has caught in the same way.

**Decided: `resource.purge` is `Rel("owner") & !Rel("suspended")`, and that is deliberately less
separation than [08](08-resource-manager.md) § Soft delete describes.** That section wants *"a role
can hold the first without the second"*, copying `deletedVaults/purge/action` sitting in Key Vault
Contributor's `notActions`. Here `delete` is already `Rel("owner")`, so **any** purge defined in terms
of `owner` is held by everyone who can delete, and a strictly separable purge needs a grantable role
of its own — which needs a role-assignment story this document does not yet have. What the definition
above does deliver is worth stating exactly: a deny assignment removes `purge` while leaving `delete`,
which is `notActions` with one row in it; and — the separation that actually bites — a parked resource
has been re-parented to its **subscription** and had its direct role assignments dropped, so `owner`
resolves through `From("parent", "owner")` to a *subscription* owner and **not** to the resource-group
owner whose `DELETE` parked it. ⚠ **Owed: whether `purge` deserves a grantable relation of its own**,
which is the only thing that makes "may delete, may not destroy" expressible for a role rather than
for a deny assignment.

⚠ **And the owed item above is blocked by something narrower and more concrete than "a
role-assignment story": there is no way to write a role tuple at all.** The tuple *store* exists —
`ITupleStoreGrain.WriteAsync` — and `IObjectRelationsGrain`'s own remarks forbid reaching past it.
What sits above it is one grant: `IScopeRelationWriter.GrantOwnerAsync`, called when a scope is
created. There is no `PUT /roleAssignments`, no `IScopeManager` member that grants, and nothing
anywhere that writes `contributor` or `reader` either. So the reads described above are real —
`GET /roleAssignments` lists tuples, `ICheckGrain.ListRoleAssignmentsAsync` walks ancestors — and the
write half of the same feature is absent. **Adding a `purger` relation today would add a relation
nothing can write**, which is worse than the gap it closes: a schema version bump, a permission that
looks grantable in the document, and no request that grants it.

⚠ **The second finding is about `delete` rather than about `purge`, and it is why the separation
cannot be expressed even in principle.** Azure's own version of "may delete, may not destroy" is
*Contributor* holding `delete` while `deletedVaults/purge/action` sits in its `notActions` — the
separation lives between two roles that both exist. Here `delete` is `Rel("owner")` and `write` is
`Rel("contributor")`, so **a Contributor cannot delete at all**, which is stricter than the Azure
role this schema says it is a view of. Every principal holding `delete` is an owner, every owner
holds `purge`, and no grant can come between them. So the real question the owed item asks is
*"should `delete` be `Rel("contributor")`"*, and that is a widening of the platform's most
destructive verb rather than an addition — it is not a change to make in passing, and it is recorded
here so that the next reader starts from it instead of from the `purger` relation, which does not fix
anything on its own.

**Decided: an expired recovery window is ended by a mechanism, and the platform gains no system
principal.** [08](08-resource-manager.md) § Soft delete deferred this here and stated the fork
exactly — *"an expiry is not a request, so there is nobody to authorize it, and `PurgeAsync` checks
`PurgePermission` against a caller. Either the platform gains a system principal, or the purge splits
into an authorized front and a mechanism the clock may drive."* The split is built:
`IResourceManager.PurgeExpiredAsync` takes an `ExpiredPurgeRequest`, which has no `CallerContext`,
and both fronts run the same `PurgeCoreAsync`. Three reasons, in the order they mattered:

- **A system principal is a subject that passes every check, and this document has no way to bound
  one.** It would be checked through the same seam as everybody else, so bounding it means a relation
  it holds and others do not — and the paragraphs above are the finding that no relation can be
  granted to anybody. The bound would be a comment.
- **The precondition is stronger than the permission, in the sense that matters.** A right is
  granted, denied, inherited and impersonated; a deadline that has passed can only be waited for.
  What stands where the `Check` stands is `IResourceIndexGrain.ResolveExpiredAsync`, and it is a
  member on that grain rather than a comparison at the caller for the reason `SoftDeleteAsync` takes
  a *duration*: one activation stamps the window and reads it, so *"may this still be restored"* and
  *"is this window over"* are two readings of one clock. A caller-side comparison would put a skew
  back on the one path where being early destroys something still restorable.
- **The two fronts share the body, and sharing it is the decision.** A purge the clock drove through
  a second implementation would drift in the direction nobody is watching — the one nobody types.

⚠ **One thing the mechanism does not inherit and one it does, and the asymmetry is the whole of the
design.** It does **not** inherit purge protection, because the flag's own two messages already
promise that the window ends it. It **does** inherit the lock check: a `CanNotDelete` lock is a
tenant's standing, visible refusal of destruction, and a clock that overruled it would make the lock
mean *"until the platform disagrees"*. A locked resource whose window has ended therefore stays
parked — held past its window, which is the thing being fixed, by a decision its owner made and can
see, which is the difference.

⚠ **And the purge-protection reading above was a live defect rather than a subtlety.** The condition
was the flag alone, while the refusal it produced said the resource *"cannot be purged **before** its
recovery window ends"* and the write path's said *"wait for the recovery window to end"*. So a
purge-protected resource became permanently undestroyable the moment its window closed:
unrestorable, unpurgeable by anybody, holding its name and its committed quota, with — as its own
message said — no request that changes the answer. The condition is now the flag **and** a window
that has not ended, asked of the grain that owns the deadline.

⚠ **The caller of the mechanism is built, and it is a scan rather than the reminder-per-resource this
paragraph used to record.** `IExpirySweeperGrain` — key `sweep/{subscriptionId:N}/rg/{name}`, one
activation per resource group, holding a reminder while that group has anything parked and cancelling
it when it does not. Every tick reads
[08](08-resource-manager.md) § Soft delete's parked-resource registry, asks the index whether each
entry is still true, and hands the ones that are to `PurgeExpiredAsync`. **It takes no decision the
two fronts do not already take**: it never reads `RecoverableUntil`, so it can be late and cannot be
early.

⚠ **The shape recorded here was "a durable reminder registered when the resource is parked and firing
at its deadline, rather than a scan", and the reason given was that "a sweeper that *searched* would
need an index that does not exist". That index landed first.**
[08](08-resource-manager.md) § Soft delete's `IParkedResourceRegistryGrain` is exactly an enumeration
of the resources a window is running on, so the premise is gone and the conclusion is re-taken. Three
reasons beyond the premise, in the order they mattered:

- **A reminder that fires at a deadline is a second durable copy of the deadline.** Its due time *is*
  `RecoverableUntil`, written into the reminder table by a different writer and read back by the
  reminder service's clock — which is the same objection this section already makes to a caller-side
  comparison. A reminder armed off *"this group has something parked"* carries no deadline at all: it
  says **look**, and `ResolveExpiredAsync` says **whether**.
- **A per-resource reminder has no repair path.** Lose the registration — a silo with no reminder
  service at the moment of the park, a crash between two writes, a reminder table restored from a
  backup — and nothing anywhere records that a window needs driving. A scan re-derives its candidates
  from a registry that has a stated invariant and a repair.
  ⚠ **This reason was stated too widely and the narrowing is owed to it (2026-09-05, #12 review).**
  A lost *group-level* row is not repaired by re-deriving the candidate set, because the re-derivation
  only happens on the tick that the lost row would have produced — and the asymmetry runs the wrong
  way for this design, since a group-level row costs a whole resource group's windows rather than one
  resource's. What makes the reason hold as narrowed is that the row is re-derivable from a durable
  record this design has and the recorded one did not: the registry says which groups have something
  parked, so the next park in the group, a hand `SweepAsync` (which arms as well as sweeps) and
  `ExpirySweeperBackfill` — a walk of every resource group at silo start — each put it back. A
  per-resource reminder has no equivalent, because nothing durable anywhere records *which deadline*
  was lost.
- **The scan reconciles what a reminder could not.** Asking the index per entry is what lets a sweep
  *remove* an entry the index no longer agrees with, which is the first thing in the tree that can
  correct a parked-resource registry that has gone long. `RepairParkedRegistryAsync`'s known race
  stops being permanent damage and becomes damage with a one-period lifetime.

⚠ **And it is a grain of its own rather than a reminder on the registry, which is a cycle rather than
a preference.** A sweep calls `PurgeExpiredAsync`, and the purge calls the registry's `UnparkAsync` —
so a reminder firing on the registry grain would be an activation awaiting a call back into itself.
Every grain a purge touches is closed to the driver for the same reason. It is the twenty-first grain
key shape and it holds no state of its own.

⚠ **What the sweeper makes routine is worth stating where the decision is.** Purges of the five types
that declare a window stop being something a person types. [08](08-resource-manager.md) § Soft delete
records that the operator-owned claims of `DBforMySQL/servers` and `Storage/accounts` are not named by
anything in this repository, so *"their purges still leave their disks"* — now on a timetable. And
issue #69 finds `DBforPostgreSQL/servers`' window hollow: the sweeper destroys nothing #69 has not
already destroyed, but it releases that name and returns that quota on schedule with nobody in the
loop. Neither is an argument for holding every window open for ever, which trades a disk for a name
and a committed quota held permanently.

⚠ **And it is the second permission carrying `& !Rel("suspended")`.** § The model's sketch above
shows the negation on `assignRole` alone, and `CyberCloudSchema`'s own remarks used to call that
permission the only one to carry it; both now describe a pair. Adding a negation is a schema change
and a version bump rather than an edit, and this one paid both.

## Storage

Tuples live in the **durable tier**, sharded by tenant, in grains. Three grain kinds, and the third one
is the one that makes it fast.

| Grain | Key | Holds | Cardinality |
|---|---|---|---|
| `IObjectRelationsGrain` | `rel/obj/{type}/{id}` | Every tuple **whose object is this** | One per object |
| `ISubjectRelationsGrain` | `rel/sub/{type}/{id}` | Every tuple **whose subject is this** (reverse index) | One per subject |
| `IMembershipIndexGrain` | `rel/idx/{usersetType}/{usersetId}` | Flattened, transitively-closed membership | One per userset |

The first two are written together on every tuple write — the write is to two grains and is *not*
transactional, so it is ordered (object first, then subject) and reconciled by a sweeper. A subject
index missing an entry costs a `ListObjects` a miss, not a `Check` an incorrect answer, because
`Check` walks forward from the object. **That asymmetry is deliberate: the direction that can be
stale is the one where staleness is a performance bug, not a security bug.**

The third is the Leopard index and it is discussed below.

## Check

```csharp
Task<Result<CheckResult>> ICheckGrain.CheckAsync(
    ObjectRef @object, string permission, SubjectRef subject, ConsistencyToken? token);
```

Evaluation is a bounded, memoized search over the rewrite tree:

1. Expand the permission into its rewrite expression.
2. For each `This` node, read `IObjectRelationsGrain` and test direct tuples.
3. For each userset subject, test membership — via `IMembershipIndexGrain` if the userset is indexed,
   otherwise recurse.
4. For each `From(tupleset, computed)` node, read the tupleset, then recurse on each target.
5. Short-circuit on the first `true`. Depth cap **12**; breadth cap **1 000** per level.

**Every visited `(object, relation, subject)` triple is memoized for the request**, which is what
stops a diamond-shaped org chart from being exponential. Cycles are broken by the memo, not by cycle
detection — a revisit is a cache hit that returns "in progress → false for this path", which is the
correct semantics for a union and is checked by a property test over randomly-generated cyclic graphs.

**Caching across requests.** A `Check` result is cached in the hot tier keyed by
`(tenant, object, permission, subject, schemaVersion, tenantRelationVersion)`. The tenant relation
version is bumped on every tuple write, so a write invalidates the tenant's whole check cache. That is
crude and it is right: tuple writes are rare (role assignments), checks are constant, and a
fine-grained invalidation graph is a second consistency problem to get wrong.

⚠ **Negative relations break monotonic caching and this is the subtlest thing in the document.** A
permission of the form `A & !B` is not monotone: adding a tuple can *remove* access. Any cache that
assumes "more tuples can only grant more" is wrong in the presence of `!`. The rule, enforced by the
schema builder: **negation may only appear at the top level of a permission, over a relation that is
computed from direct tuples on the same object** (`!Rel("suspended")`, never `!From(…)`). That
restriction keeps invalidation to "the same object changed", which the version stamp already covers.
It costs expressiveness we do not need and buys a cache that is not subtly wrong.

## Consistency

Zanzibar's zookie, adapted. A `ConsistencyToken` is a per-tenant monotonic version returned by every
tuple write and accepted by every check.

| Mode | Behaviour | Used by |
|---|---|---|
| `MinimizeLatency` (default) | Any cached result | List views, portal navigation |
| `AtLeastAsFresh(token)` | Bypass cache entries older than the token | Immediately after a role assignment — the portal passes the token it just got back |
| `FullyConsistent` | Bypass all caches, read durable | Deletion, key export, billing changes, anything where a stale allow is a real incident |

This exists because of one specific bug class: an admin revokes a user's access, the UI says done, and
the user's next request is served from a cache and succeeds. Without a token, the only fixes are "never
cache" or "hope". With one, the revoke returns a token, the portal shows the new state as of that
token, and the *enforcement* path for anything destructive is `FullyConsistent` regardless.

## The Leopard index — and why it is not optional

The naive `Check` walks group membership at request time. For `group:eng#member@group:platform#member`
nested five deep with 10 000 members, that is thousands of grain calls. The 00-doc target is p99 < 10 ms.

`IMembershipIndexGrain` holds, per userset, the **transitively closed** set of concrete subjects, as a
compressed roaring bitmap over a per-tenant subject dictionary. Membership becomes a bitmap test:
O(1) after one grain read, and an intersection of two usersets is O(min(|A|,|B|)) rather than a walk.

**Maintenance.** A tuple write that touches a userset publishes to `cc.{tenant}.rebac.userset.{id}`.
An index rebuilder consumes it, recomputes the affected closures (bounded by walking *up* the userset
graph from the changed edge, not from scratch), and writes back. Rebuilds are idempotent and versioned.

**Staleness.** The index is *eventually* consistent and lags a tuple write by tens of milliseconds. A
check that requires `AtLeastAsFresh` or `FullyConsistent` compares the index's version to the token
and falls back to the walk if the index is behind. So the index is a fast path that is always
verifiable, never an authority.

⚠ **Indexing is opt-in per userset type, and there is a threshold.** Indexing every two-member group
costs more than it saves. `group#member` and `role#assignee` are indexed; a userset is materialized
only once it exceeds 64 members or 2 levels of nesting, and it is dropped back when it shrinks. That
threshold is a tuned constant with a metric on it, not a guess frozen in code.

## ListObjects — the expensive one

"Which resource groups can Alice read?" is not `Check` run repeatedly; it is the reverse direction, and
it is where naive ReBAC implementations fall over.

```csharp
Task<Result<Page<ObjectRef>>> ListObjectsAsync(
    string objectType, string permission, SubjectRef subject, ContinuationToken? ct);
```

Algorithm: start from `ISubjectRelationsGrain` for the subject and every userset it belongs to (from
the membership index, reversed), collect the objects they touch, then walk the rewrite tree
*backwards* — for a `From("parent", "owner")` rule, an object is reachable if its parent is. Results
are paged and each page is `Check`-verified before being returned, because the backward walk over
approximates when negation is present.

**It is paged, capped, and it is not a search API.** A tenant with 200 000 resources and a user with
access to all of them gets pages, not a list. The portal's resource list is served from the
**resource-graph projection** in ClickHouse ([08](08-resource-manager.md)), which is maintained by the
resource-changed stream and carries a denormalized access column recomputed from `ListObjects` on
relation changes. That is: **the fast list is a projection; `ListObjects` is what maintains it.**
Getting this the wrong way round — serving the portal's list page directly from `ListObjects` — is the
single most likely performance mistake in this subsystem and it is named here for that reason.

## The enforcement seam

Exactly one place in the request path calls the engine:

```csharp
// CyberCloud.ResourceManager — before any provider is invoked
var check = await authz.CheckAsync(
    ObjectRef.Resource(resourceId), permission, SubjectRef.From(caller), consistency);

if (!check.Allowed)
    return Result.NotFound();          // ← 404, never 403
```

**404, never 403**, on a resource the caller cannot read. A 403 confirms the resource exists, which is
an enumeration oracle: a competitor can discover a customer's resource names by probing. 403 is
returned only when the caller can *read* the object but not perform the *action* — which is a real and
useful distinction, and it means the response code itself is authorization output.

Providers never call the engine. A provider that does is failing a review, because authorization
scattered across twenty providers is twenty places to get it wrong and one place to miss.

## Testing

- **Property tests** over generated schemas and tuple sets: `Check` agrees with a slow, obviously-correct
  reference evaluator on 100 000 random graphs including cycles, deep nesting, and negation.
- **Index equivalence**: for every generated graph, the Leopard index's answer equals the walk's.
- **Consistency**: write a tuple, immediately check with the returned token, assert the new state —
  run against a cluster with an artificially lagging index.
- **The isolation suite** ([03](03-repository-layout.md)) drives the public API with tenant B's ids as
  tenant A across every provider and asserts 404 on all of them.
- **A regression corpus**: every authorization bug ever found becomes a named test with its tuple set
  checked in. This corpus is the real asset; the code is replaceable.

## Effort and sequencing

| Piece | EM | Milestone |
|---|---|---|
| Schema builder, tuple grains, `Check` with memo + depth cap | 1.2 | M1 |
| Check cache + consistency tokens | 0.5 | M1 |
| Azure-shaped role-assignment API over tuples | 0.4 | M1 |
| `ListObjects` + the resource-graph access column | 1.0 | M2 |
| Leopard membership index + rebuilder | 1.2 | M2 |
| Time-bounded relations (JIT roles), delegation | 0.4 | M3 |
| **Total** | **4.7** | |

M1 ships without the index and without `ListObjects`, and that is viable because M1 tenants are small:
a walk at depth ≤ 4 over ≤ 100 members is single-digit milliseconds. The index is scheduled for M2
because that is when tenant size starts to vary, and the threshold logic means it can be turned on for
one tenant before it is turned on for all of them.
