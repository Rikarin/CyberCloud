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
