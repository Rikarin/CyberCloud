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
| Inheritance management group → sub (#39) | The same rewrites on a `managementGroup` type whose `parent` is a tenant or another group. ⚠ A subscription assigned to a group has the group as its *one* `parent` — the edge is relinked, never doubled, so the chain stays a chain for `CheckGrain.WalkAncestorsAsync` and the scoped `ListObjects` walk; [06 § The hierarchy](06-tenancy-and-resource-model.md) |
| Deny assignment | `#suspended`, and the `& !Rel("suspended")` in the permission |

The API can present `GET /roleAssignments` by listing tuples whose relation is a named role, which is
how the portal shows a familiar screen over an unfamiliar engine. The reverse direction — expressing
`resourceGroup:prod#reader@group:eng#member` in Azure RBAC — is not possible, which is the argument
for building this rather than a role table.

**The write half of that view is built (issue #70), and its address is the tuple.**
`PUT`/`GET`/`DELETE {scope}/providers/CyberCloud.Authorization/roleAssignments/{name}`, where the
scope is a tenant, a subscription, a resource group or a resource — Azure's shape — is served by
`IRoleAssignmentManager` over `ITupleStoreGrain`, and the name is
**`{role}-{principalType}-{principalId}`**: `reader-user-7f3c…`, `contributor-group-2b4a…` (the id is
the principal's `N`-form GUID — a group has one as a user does, and `contributor-group-eng` would
name a principal the directory check below refuses). That is
the one place the address departs from Azure's, and it is a decision rather than a shortcut. Azure's
`{name}` is a client-minted GUID kept in a record beside the grant so that it can be looked up
again; a record that maps a GUID to a tuple is the role table the paragraph above argues against,
however small. The tuple already *is* the assignment — one object, one relation, one subject, and an
object's tuple set cannot hold it twice — so the name says which tuple, a repeated `PUT` is the same
tuple, and there is no second durable thing to keep in step with the first. What it costs is the
client that mints its own GUIDs; the Bicep `guid(scope, principal, role)` idiom exists because a
deterministic name is what people want from this address anyway. The body's three properties
(`principalId`, `principalType`, `roleDefinitionId` — a role *name*, since there are no role
definitions to address) are optional and must agree with the address when present. Only `assignRole`
holders may `PUT` or `DELETE` — `Rel("owner") & !Rel("suspended")`, which is Azure's
`roleAssignments/write` sitting in Owner and in no built-in role beneath it — and a `GET` needs
`read`. A `group` principal is written as the userset `group:{id}#member`, which is row two of the
table above.

**The read half is on the wire too (issue #86):** `GET {scope}/providers/CyberCloud.Authorization/roleAssignments`
lists what is assigned at a scope — the tuples written on it and the ones inherited from its
ancestors, which is `ICheckGrain.ListRoleAssignmentsAsync`'s view exactly, so a row appears only
where the type's own rewrite actually inherits the role. It needs `read` on the scope and nothing
per row: an assignment is a tuple *on* the scope rather than an object with tuples of its own, which
is why Azure's `roleAssignments/read` sits in Reader, and why this listing — unlike
[10](10-gateway-and-api.md) § Shape's resource collection — is never short for a reason the caller is
not told. It is paged the way every collection of this API is (`$top`, `$skipToken`, `nextLink`),
ordered and resumed by each assignment's address. ⚠ **An inherited row carries the ancestor's
address, marked `inherited: true`.** `subscription:S#owner@user:U` is one assignment however many
groups and resources it reaches, and its `id` is the one address a `GET` or a `DELETE` answers for
it; rendering it at every scope that inherits it would mint addresses nothing serves.

**The principal is checked against the directory before the tuple is written (issue #86).** Until
then `IRoleAssignmentManager` wrote the tuple for any well-formed subject, so a typo granted to
nobody and nothing could ever see it. The check goes through `IPrincipalDirectory`, a seam in
`CyberCloud.ResourceManager.Contracts` shaped like `CallerContext` — a type and an id, never a
`SubjectRef` — because `module-layering.txt` gives the resource manager no edge to identity and
identity none back, and a directory lookup is the case that file says goes through a seam. The
implementation that reads `IUserGrain`, `IServicePrincipalGrain`, `IManagedIdentityGrain` and
`IGroupGrain` is the gateway's `GrainPrincipalDirectory`, in the one shipping assembly that references
both sides; the resource manager's own default refuses, so a host that composes the manager and
forgets the directory grants nothing rather than granting to anybody. Three consequences worth
stating: the lookup is asked *after* `assignRole`, so a caller with no grant cannot learn which
principal ids exist from the difference between two refusals; it is asked of the assignment's
tenant through `ForTenant`, so a principal of another tenant is "does not exist" by construction
([11](11-identity.md) § Sign-up and tenant creation — one user, one tenant); and a revoke does not
ask, because a grant to a principal since deprovisioned must remain removable. The id must be the
`N`-form GUID a token carries as `sub` — any other spelling names a subject no token presents and is
refused rather than folded. `RoleAssignmentTests` drives all of it through the real grains.

**A resource is a fifth principal type, and it is not a subject type (issue #90).** `SubjectTypes`
stays closed at `user`, `servicePrincipal` and `managedIdentity` — what a token can carry, what can
sign in. `principalType: "resource"` with the resource's own `N`-form GUID is what a tenant grants
when one resource has to read another: a backup vault the `reader` role on the resource group whose
shares it protects, the way Azure grants a vault's system-assigned identity a role on what it backs
up — minus the identity, because the resource's GUID is already a stable, tenant-scoped object id.
The subject a check then sees is `resource:{id}`, which the schema already knows as an object and
Zanzibar makes a valid subject; the reconcile driver puts exactly that subject into every
cross-resource read ([08 § What the resource manager deliberately does not do](08-resource-manager.md)),
so the grant and the check meet on one spelling. Its existence is answered by `IResourceGrain` in the
assignment's tenant rather than by the directory — the directory is identity's and a resource is the
manager's — with the same three consequences as above: asked after `assignRole`, asked `ForTenant`
so another tenant's resource does not exist here, and never asked on a revoke. A resource never acts
outside its own reconcile pass, and it acts as itself: `CallerContext.ImpersonatedBy` is empty by
construction, so an audit line reads "the vault read the share", not "the vault read the share as
Alice".

⚠ **The derived name, re-taken with the cost stated, and it stands (issue #86).** `PUT
…/roleAssignments/{guid}` is what every ARM client emits — the SDKs, `az role assignment create`,
and Terraform's `azurerm_role_assignment`, which mints a random UUID when `name` is unset. A
Terraform provider for this platform ([01](01-azure-parity-catalogue.md) § K — Developer surfaces, [21](21-cli-and-sdks.md) § Other SDKs, issue #59)
therefore cannot be Azure's provider with the host swapped: it computes the segment from
`(roleDefinitionId, principalType, principalId)` before the `PUT`, as a Bicep template computes
`guid(...)`, and reads state back through the `id` the response returned. That is a documented
mapping in one generated file rather than a per-assignment record in the platform, which is the
trade. **A client GUID cannot be accepted on `GET` or `DELETE` as an alias, and the reason is
arithmetic**: a mapping from a client-chosen value to a tuple is either computed or stored; it cannot
be computed, because the client chose the GUID at random and the tuple carries no trace of it; and
stored, it is the role table this section argues against in every respect that matters — a durable
grain per assignment, a grain-key shape, a write that lands beside the tuple write and has to be
reasoned about when only one of the two does. Accepting a GUID on `PUT` alone, and answering with the
derived `id`, would work for a client that reads back through the response, and is not done either:
an address that accepts a name it will never serve a `GET` on teaches a client that the name is
real. `RoleAssignmentName`'s remarks carry the same argument beside the code.

⚠ **The address reaches no generated surface, and that is #63's question asked a third time rather
than a new one.** `CyberCloud.Authorization` is a reserved namespace — `ProviderRegistry.Build`
refuses a provider that claims it, because on a resource group the assignment address is a
well-formed resource path and the gateway routes it first — so ADR-012's emitters, which read the
registry and the scope extension #63 added, know nothing of it. `openapi/`, `cyc` and the SDK are
silent about it exactly as they were about scopes before #63, and the fix is the same shape: a third
non-registry source for the emitters. [10](10-gateway-and-api.md) § Shape records the gap. The portal
reaches the address by hand in the meantime — its access page (#22) grants, checks and revokes over a
hand-written `RoleAssignmentsApi` that derives the name exactly as `RoleAssignmentName` does, and it
cannot list, because the collection `GET` is #86's.

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
of its own — the role-assignment path exists now (issue #70), and the three roles it grants are the
schema's three. What the definition above does deliver is worth stating exactly: a deny assignment
removes `purge` while leaving `delete`, which is `notActions` with one row in it; and — the
separation that actually bites — a parked resource has been re-parented to its **subscription** and
had its direct role assignments dropped, so `owner` resolves through `From("parent", "owner")` to a
*subscription* owner and **not** to the resource-group owner whose `DELETE` parked it. Whether
`purge` deserves a grantable relation of its own is answered two paragraphs down, under
**Decided: `delete` stays `Rel("owner")`** — a fourth role is the same change as widening `delete`,
and neither is taken.

⚠ **The owed item above was blocked by something narrower and more concrete than "a
role-assignment story": until issue #70 there was no way to write a role tuple at all.** The tuple
*store* existed — `ITupleStoreGrain.WriteAsync` — and `IObjectRelationsGrain`'s own remarks forbid
reaching past it. What sat above it was one grant: `IScopeRelationWriter.GrantOwnerAsync`, called
when a tenant is created. There was no `PUT /roleAssignments` and nothing anywhere that wrote
`contributor` or `reader`, so the reads described above were real and the write half of the same
feature was absent — which is what made the M1 exit story's *"grant them Reader on one resource
group"* unbuildable. § Azure RBAC, expressed in it's `IRoleAssignmentManager` is that write half.
**A `purger` relation would still not fix the separation**, for the second reason below.

⚠ **The second finding is about `delete` rather than about `purge`, and it is why the separation
cannot be expressed even in principle.** Azure's own version of "may delete, may not destroy" is
*Contributor* holding `delete` while `deletedVaults/purge/action` sits in its `notActions` — the
separation lives between two roles that both exist. Here `delete` is `Rel("owner")` and `write` is
`Rel("contributor")`, so **a Contributor cannot delete at all**, which is stricter than the Azure
role this schema says it is a view of. Every principal holding `delete` is an owner, every owner
holds `purge`, and no grant can come between them. So the real question underneath is *"should
`delete` be `Rel("contributor")`"*, a widening of the platform's most destructive verb rather than
an addition.

**Decided: `delete` stays `Rel("owner")`, and a Contributor cannot delete, deliberately.** Issue #70
put the widening on the table once the write path existed to make it matter, and the answer is no,
for one reason: `purge` is defined in terms of `owner`, and owner-only `delete` is what keeps `purge`
— the end of a recovery window, the one irreversible verb this platform has — **unreachable to a
contributor**. Widening `delete` alone would let a contributor park a resource that only an owner
could restore or destroy, which is a role that can start a process it cannot finish; widening both
would hand the irreversible verb to the role Azure gives it to only through a `notActions` row this
schema cannot express — § Caching across requests' negation rule allows `!Rel` over a direct
relation on the same object and nothing else, so *"Contributor minus purge"* is not a permission
here. The price is stated plainly: a Contributor in this platform is narrower than Azure's, and a
tenant that wants "may remove, may not destroy" today gets it as a deny row on the resource
(`#suspended` removes `purge` and leaves `delete`), not as a grantable role. Re-taking this needs a
role beneath owner that holds `delete` and not `purge`, which is a fourth relation and a
`SchemaVersion` bump, and it needs `purge` to stop being defined through `owner`; both are the same
change and neither is a change to make in passing.
`RoleAssignmentTests.AContributorCanWriteButNotDeleteByDecision` drives a real Contributor grant
through the real `PUT` and asks the three verbs of a resource beneath it; its failure message names
this paragraph as the one to change first.

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
| `IMembershipIndexGrain` | `rel/idx/{type}/{id}` | Flattened, transitively-closed membership, **both directions** — every userset this subject is in, and the members of every userset formed on it | One per subject object |

The first two are written together on every tuple write — the write is to two grains and is *not*
transactional, so it is ordered (object first, then subject) and reconciled by a sweeper. A subject
index missing an entry costs a `ListObjects` a miss, not a `Check` an incorrect answer, because
`Check` walks forward from the object. **That asymmetry is deliberate: the direction that can be
stale is the one where staleness is a performance bug, not a security bug.**

The third is the Leopard index and it is discussed below. ⚠ **Its row used to read
`rel/idx/{usersetType}/{usersetId}`, one per userset, and what shipped (issue #37) is one per subject
object holding both directions** — because a write against a userset has to find the subjects it
reaches through the members set, and a listing has to find the usersets a subject is in, and the two
directions of one object belong in one activation for the reason the reverse index keeps `group:eng`
and `group:eng#member` in one grain. It is written by the same store, under the same journal, as a
step of the same write — and, unlike the reverse index, `Check` reads it, so the store orders it so
that no crash can leave it more permissive than the tuples. § The Leopard index says how.

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

⚠ **The same class runs the other way, and a deployment is where it was measured (#39).** A write's
step 3 checks `MinimizeLatency`, and `CheckGrain` answers that mode from any cached entry with no TTL —
so a *deny* is cached exactly as an allow is. A deployment child refused because its creator held
nothing on its group, and retried after an owner granted the missing role, met the cached refusal again
in `test/CyberCloud.Isolation`'s `DeploymentAuthorizationTests`; an ordinary `PUT` retried after a
grant does the same. The portal's `AtLeastAsFresh` token is the fix on its own path; a write path that
carried the caller's latest token, or a grant that dropped cached denies, is owed — recorded in
[08 § Long-running operations](08-resource-manager.md) beside the deployment that found it.

⚠ **And in the revoke direction, where it is the incident this section is about.** The same cache let
a deployment keep writing children as a creator revoked after the first one: the deployment's own `PUT`
had cached their allow at the group, and every later child at `MinimizeLatency` hit it. A deployment's
child is written as a recorded caller, from a reminder, with no token to be fresh against, so its step 3
is `FullyConsistent` — which also closes the grant direction above *for children*
(`DeploymentAuthorizationTests.ARightRevokedBetweenTwoChildrenIsHonouredAtTheSecond`).

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

⚠ **BUILT (issue #37), and four of the paragraphs above describe a different index from the one
that shipped. Each difference is a decision, and the reasons are here.**

- **Keyed by the subject object, holding both directions.** `IMembershipIndexGrain`, key
  `rel/idx/{type}/{id}` — the twenty-third `GrainKeyKind` — holds for one subject object every
  userset it is transitively in (`Usersets`, per subject relation, what `ListObjects` starts from)
  and the transitively closed members of every userset formed on it (`Members`, per relation, what
  `Check` tests against). The first attempt at this section found the walk wants the direction the
  section did not describe, and that maintaining either direction incrementally needs the other: a
  write against a userset finds the subjects it reaches through the members set, and finds the
  usersets it reaches them *for* through the usersets set.
- **Maintained by the tuple store in the write path, not by a stream consumer.** There is no
  `cc.{tenant}.rebac.userset.{id}` and no rebuilder process. `TupleStoreGrain` runs
  `MembershipIndexMaintainer` as a step of every write and every delete, under the same journal that
  makes the reverse index reconcilable, **before** the tenant relation version moves — so a token
  covers the index the way it covers the reverse half, and the version comparison the § Staleness
  paragraph requires has nothing to compare. The index cannot be behind a token. It can be behind a
  write that crashed, by exactly the sweep window the reverse index is, and the sweeper replays it.
  What the store does that the stream design did not have to decide is **order**: a write lands the
  index last, a delete lands it first, so at every point a crash can leave a tenant the index is no
  more permissive than the forward half. That is the whole argument for letting `Check` take a
  `false` from it without walking, and `MembershipIndexGrainTests` drives a write and a delete
  through a real interruption on each side of the index to hold it.
- **The closure is over direct-only relations, and "verifiable, never an authority" is met by
  completeness rather than by version.** The graph's edges are tuples on relations computed from
  `This` and nothing else — `group#member` here — followed onward only through usersets on such
  relations. A userset on `This | From("parent", "owner")` is recorded as a member and never
  expanded, because what it contains is a `From` away from anything a closure over tuples can say;
  a closure that holds one is *incomplete*, and `MembershipIndexReader` answers "walk it" for it
  rather than `false`. `CheckPropertyTests.CheckAgreesWithTheReferenceEvaluatorThroughTheLeopardIndex`
  holds the indexed evaluator to the reference one on the same twenty thousand graphs the walk is
  held to, and `MembershipIndexPropertyTests` holds the index itself to a brute-force closure after
  every one of a random sequence of writes, deletes and replays on two thousand more.
- **A write is two unions; a delete recomputes, bounded exactly as the previous status paragraph
  predicted.** Adding `U → S` puts `{S} ∪ Members(S)` into the members of `U` and everything above
  it, and `{U} ∪ Usersets(U)` into the usersets of `S` and everything below it — one slice write per
  userset above plus one per member below, which is the fan-out the threshold paragraph exists to
  cap and `AuthorizationMetrics.IndexWrites` now measures. Removing `U → S` recomputes the members of
  `U` and everything above it from the tuples, and subtracts from the usersets of `S` and everything
  below it whatever those recomputations no longer reach. Both are idempotent, which is what lets the
  sweeper replay a half-applied change.
- **`Check` counts an index-answered userset against no cap, and the walk mirrors the cap where it
  crosses the node `Check` caps.** § Check step 5's "breadth 1 000 per level" counts expansions, and
  an index read is a set test; so a subject granted through the 1 001st indexed group on one object
  is allowed. The reverse walk, reaching an object through a userset, asks the same index the same
  question `Check` asks at that node — and only when the index declines does it read the object's
  tuples and count, in `Check`'s order, the unanswered usersets before this one. A derivation
  `Check` would cut is not reached, the answer stays exact, and `ListObjectsEvaluation.BreadthCapHit`
  says a pair was left out — the reading `DepthCapHit` already has. ⚠ The first cut at this (the
  branch as reviewed) capped the transpose — the objects one userset is granted on — so a group
  granted on more than 1 000 objects anywhere in the tenant made every scoped listing by its members
  fall back to the per-member check; that outcome value is gone from `ListObjectsOutcome`, which is
  back to three. On `CyberCloudSchema` every userset the platform writes is indexed, so a listing
  over a written index pays nothing for the mirror.
- **An unwritten slice is not an empty closure, and the backfill is lazy.** A slice's
  `SchemaVersion` is `0` until a write touches it, and the tuples it should close over may predate
  the index — every tuple in a tenant upgraded to it, or restored without its index rows. The
  review of issue #37 found the reader taking such a slice as complete and answering an
  authoritative `false` for every pre-existing group membership, and the maintainer closing a new
  nesting edge over it into a slice, stamped current, that omitted the group's existing members for
  good. Now `MembershipIndexReader` refuses an unwritten slice the way it refuses a stale one —
  "walk it", and nothing on the listing side, whose walk still hops the groups — and every path
  that would derive from or add to such a slice rebuilds it from the two indexes first:
  `MembershipIndexMaintainer` for the two ends of an edge, `MembershipIndexGrain.ApplyAsync` for
  every other slice a change lands on. So the first write that touches an object backfills its
  slice, and until then the index says nothing about it.
- **`FullyConsistent` never reads the index.** Its contract is the durable rows themselves, and a
  closure derived from them by a write that may not have seen a restore or a repair is what that mode
  exists to bypass; it walks with `NoMembershipIndex`, which is now that mode's and the in-memory
  tests' rather than the silo's.

⚠ **What is owed, precisely.** The **threshold** — "materialized only once it exceeds 64 members or
2 levels" — is not built: every direct-only userset is closed, and the cost of a group-to-group edge
is one slice write per subject below it, in the write path, with `IndexWrites` as the number to
watch before deciding where the threshold goes. The **roaring bitmap** and the per-tenant subject
dictionary are not built: a slice is a JSON list of `SubjectRef`s, so a ten-thousand-member group is
a ten-thousand-entry row. A **tenant-wide rebuild** is not built: `IMembershipIndexGrain.RebuildAsync`
recomputes one slice from the two other indexes, the maintainer and the grain rebuild a slice they
find unwritten or stamped with another `SchemaVersion` before deriving from it or adding to it, and
readers refuse such a slice — but nothing enumerates a tenant's subject objects, so both an upgrade
that brings the index to a tenant with tuples and a schema bump that changes which relations are
direct-only leave untouched slices unindexed, and walked, until a write reaches them. A **rolling
upgrade that bumps the schema version** has a window the check cache does not: `MembershipIndexGrain`
refuses a change computed under another version, so a tuple write whose store runs version N and
whose index grain runs N+1 fails at step 6, after both halves landed; the journal keeps the entry,
the index is behind the forward half for that tuple — the deny direction — and the sweeper applies
it once the fleet converges. The check cache keys on the schema version and rides the window; the
index has no per-version copy to key on. And the **resource-graph access column** § ListObjects says
the walk maintains is maintained since #54 — by the projection's consumer, on every resource change,
from the role-assignment view rather than from the walk, and not yet on a relation change; see
[08 § The resource-graph projection](08-resource-manager.md) for what it holds and what it owes.

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

⚠ **BUILT (issue #37), and the signature above is not the one that shipped, for the reason `Check`'s
was not.** `IListObjectsGrain`, key `rel/list/{type}/{id}` — the twenty-second `GrainKeyKind`, keyed by
the **subject**, because that is where the walk starts and because a listing is a question about one
caller. The subject is the key rather than a parameter, the rest travels in a `ListObjectsRequest`,
and the answer is a `ListObjectsPage` with the objects ordered by id, a continuation naming the last
one returned, and the tenant token the walk ran at. `ListObjectsEvaluator` is the algorithm above as
three rules over reached `(object, name)` pairs — a tuple naming the subject reaches its object, a
userset the subject is in reaches everything written against it, `Rel(x)` and `From(ts, c)` carry a
reached pair to the names computed from it — and it is exact for union rewrites. **"Each page is
`Check`-verified" is paid only when it is owed**: the walk records whether any rewrite it crossed was
an intersection or an exclusion, and re-runs every candidate through `CheckEvaluator` only then. On
`CyberCloudSchema` that is `assignRole` and `purge`; the `read` path is never verified.
`ListObjectsPropertyTests` holds the walk to a brute force over the reference evaluator on 2 000
generated graphs, every type and name, and demands that at least a twentieth of them verify.

⚠ **The bound the section above does not name is the one that made the resource list affordable.**
The warning about serving a list page from an unscoped walk is exactly right, and `ListObjectsRequest.Within`
is what turns the walk into something a list page *can* be served from: it restricts the answer to
objects at or below one object in the `parent` hierarchy — and, more to the point, restricts the
*walk*. Descending from an ancestor of the scope follows only the next object on the chain toward it;
descending below the scope stops at `WithinDepth`; an object at the requested depth is not expanded at
all. So `ReBacResourceAuthorizer.ListReadableAsync` asks for the resources under the group at depth 1
and the walk reads the caller's own index, the groups they are in and the chain down to the group —
and never a member's grain. #10's honest cost, *"one `ICheckGrain` call per member examined, to a
distinct activation keyed on that resource's GUID"*, is the fallback now, taken when the walk passes
`AuthorizationLimits.MaxListObjects` (10 000 objects, past which the page is empty and says so) or
fails, because "nothing readable" is the one answer a listing may never fake. Scoping makes two
assumptions, both recorded on the request type, both failing in the direction that hides rather than
shows: the chain is a chain, and no userset is formed on an object the pruned walk never expands. The
platform writes one `parent` per resource and forms usersets on groups only, so both hold here.

⚠ **The Leopard side of this section is built, and the seam took two methods rather than the one
this paragraph used to promise.** The walk still reads `ISubjectRelationsGrain` through
`IReverseRelationReader.ReadAsync`, one grain per subject object it visits, because a group's grants
live in its reverse index and nowhere else. What it no longer pays is the hop per level that *found*
the groups: `IReverseRelationReader.ReadUsersetsAsync` answers the subject's transitively closed
userset membership from one `IMembershipIndexGrain` read, and the walk reaches every group in it at
depth 0 — the index is not a hop, so a chain of twenty nested groups is listed where thirteen hops
used to be cut. "Would satisfy that interface unchanged" was wrong: a reverse entry carries no
subject, so a closed entry `c#parent@group:platform#member` handed back for `group:eng` would have
read as `eng` being `c`'s parent, and the tupleset rule needs the record verbatim. The
resource-graph access column this section says `ListObjects` maintains is maintained since #54 by
the projector in `CyberCloud.ResourceGraph`, and by `ICheckGrain.ListRoleAssignmentsAsync` rather
than by this walk — the column needs the object-to-subjects direction, and this walk answers the
other one. The membership index's subject-to-usersets read happens where the paragraph above puts
it, on the list query, once per caller. See § The Leopard index below and
[08 § The resource-graph projection](08-resource-manager.md).

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
- **Index equivalence**: for every generated graph, the Leopard index's answer equals the walk's —
  built as `CheckPropertyTests.CheckAgreesWithTheReferenceEvaluatorThroughTheLeopardIndex` on the
  check side and `MembershipIndexPropertyTests` on the index itself, the latter after every mutation
  of a random write-and-delete sequence rather than once per graph.
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
