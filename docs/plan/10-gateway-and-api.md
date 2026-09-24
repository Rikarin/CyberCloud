# 10 — The Gateway: REST and SignalR

One public entry point. The portal, the CLI, the SDK and a tenant's own automation all arrive here,
authenticate the same way, and are authorized by the same engine.

## Shape

`CyberCloud.Gateway.Host` is an ASP.NET Core app and an **Orleans client** (`CreateClient`, per
[03](03-repository-layout.md)), not a silo. It is stateless, scales on request rate, and a deploy of it
does not move grains.

```
Internet
  └─ Envoy Gateway (TLS, HTTP/2+3, per-IP shed)
      └─ CyberCloud.Gateway.Host  (N pods)
          ├─ /            REST  — the resource API, and the scope API below it
          ├─ /hubs/*      SignalR — portal live updates, terminal, operation progress
          ├─ /.well-known OIDC discovery (proxied from Identity — not built), and
          │               security.txt (RFC 9116, served from an embedded file — built)
          ├─ /openapi     the generated document, per api-version
          └─ /agent/v1/tunnel  WebSocket — a connected cluster's agent dialling in (#36);
                          no JWT and no api-version, a per-cluster credential the tunnel
                          grain checks — 09 § Cluster connections, the AgentInitiated row
```

`/agent/v1/tunnel` is the third route that reaches stage 5 without a tenant, beside `/openapi` and
`/.well-known`: it is anonymous *to stage 2 only*, and `AgentTunnelEndpoint` refuses the upgrade
itself when the bearer value's hash does not admit an agent to exactly the cluster id in the request
header. It is also the one route on this host that is not REST and not SignalR — a frame protocol
over a WebSocket the gateway relays without reading, which is why it carries no `api-version` and
appears in no OpenAPI document.

`/.well-known/security.txt` is the one path on this host that answers `text/plain`, and it is served
through the same nine stages and the same writer as everything else: no token required, no
`api-version` (the file's shape is RFC 9116's, not this platform's), the per-IP unauthenticated
bucket at stage 5, and both correlation ids on the response. `GatewayOutcome` grew a third body
kind for it rather than a content-type member — [18](18-security-vault-and-malware-scan.md)
§ Disclosure records why that distinction is the whole point.

**The scope API is the first four and six segments of the resource path, and it is a different
component behind the same door.** `GET` and `PUT` on `/tenants/{t}/subscriptions/{s}` and on that plus
`/resourceGroups/{rg}` reach `IScopeManager`; everything longer reaches `IResourceManager`. The two
grammars are disjoint — a resource address is at least ten segments and must contain `/providers/` —
so the router tries both without a precedence rule. ⚠ Since #39 the scope API also has one shape that
is *not* a prefix of the resource path: `/tenants/{t}/managementGroups/{name}`, four segments like a
subscription and told apart by the literal ([06 § The hierarchy](06-tenancy-and-resource-model.md)).
It added no segment count, so the disjointness argument is unchanged. A scope answers `201` on a create and `200` on a
repeat rather than the resource path's `202`: a subscription and a resource group are one grain
activation each and converge before the call returns, so there is nothing to poll and an
`Azure-AsyncOperation` header would name a URL that answers `404`. `DELETE` on a scope answers `405`,
because deleting a resource group is the reverse of [06](06-tenancy-and-resource-model.md) § Two-phase
create — everything in it, in dependency order, as one long-running operation — and that is not built.

⚠ **`/tenants/{t}` routes and a tenant is still not creatable over HTTP, and the two facts are the same
fact.** A caller may `GET` the tenant they hold a token for. They cannot `PUT` one, because stage 3
below resolves the request's tenant from the token and refuses any path naming a different one — so the
only tenant a request can address is one that already exists. Tenant creation is
`IScopeManager.CreateTenantAsync`, off this pipeline entirely;
[08](08-resource-manager.md) § The write path, end to end carries the argument and what was rejected.

⚠ **The scope API is in the generated OpenAPI document, and the generator therefore has a second,
non-registry source.** § API versioning's document is generated from the provider registry and a scope
has no provider, so until this was closed `cyc`, the SDK and the portal forms knew nothing about these
addresses and a tenant could create a subscription only by hand. The alternative — documenting the
scope API separately and excluding it from generation — was rejected because the compatibility gate
diffs the *published document* and every derived surface reads it ([21](21-cli-and-sdks.md)
§ Generation's one hop): a page outside that document would have left all four surfaces exactly as
unable to create a subscription, and would have put the two addresses where no gate could see them
break. `OpenApiEmitter.ScopePathItems` emits them, discriminated by `x-cybercloud-scope`; the
precedent is `/operations/{operationId}`, which has come from no provider since the emitter was
written. ⚠ The tenant path carries a `GET` and no `PUT`, and the absence is emitted as a decision:
stage 3 below resolves the request's tenant from the token, so a create route could not authenticate,
and documenting one would have generated a `cyc` verb that fails every time it is used.

**The scope collections are the third scope grammar, and #63's answer applied a second time.**
`GET /tenants/{t}/subscriptions`, `GET /tenants/{t}/managementGroups` (#39 — flat, every group in
the tenant, each carrying its parent) and `GET /tenants/{t}/subscriptions/{s}/resourceGroups` — the
item address one segment short, three or five segments, no `/providers/` — route as
`RouteKind.ScopeCollection`, tried after the item grammar and only on a `GET`, and reach
`IScopeManager.ListAsync`, which is the resource collection's shape one level up: the parent grain's
own index (`ITenantGrain.ListSubscriptionsAsync`, `ITenantGrain.ListManagementGroupsAsync`,
`ISubscriptionGrain.ListResourceGroupsAsync`; ⚠ a tenant has two collections since #39, so
`ScopeCollectionId.MemberKind` travels with the parent rather than being derived from it)
ordered ordinally and cut by `$top`/`$skipToken`, one `ListObjects` for the page filtered by `read`
([07](07-rebac-authorization.md) § ListObjects — Azure's `GET /subscriptions` semantics, what the
caller holds any role on), a `Check` per member when the engine declines, and each survivor rendered
by the by-id read so an element of `{ "value": [ … ], "nextLink": … }` is byte-for-byte what a `GET`
of that scope returns. The two collections differ in one check: the subscription collection asks
nothing about the tenant — a caller holding `reader` on one subscription sees that one — and the
resource-group collection answers the canonical `404` for a subscription the caller cannot read,
because an empty page under it would confirm the subscription exists. A `PUT`, `PATCH`, `POST` or
`DELETE` on either path is a `400` that names the item address a scope is created at; there is no
`/tenants` collection, because the only tenant a request can address is its own. Both are emitted
through the same scope source as the items — `x-cybercloud-scope` plus
`x-cybercloud-scope-collection` — so `cyc scope subscription list`, `cyc scope resource-group list`,
the three SDKs' list methods and the portal's generated client all learn them from the document. A
collection routed by hand and left out of it would recreate the state #63 closed: an address the
gateway serves where the compatibility gate cannot see it break.

**The role assignment API is an extension address on every scope and on every resource, and it is a
third component behind the same door.** `GET`, `PUT` and `DELETE` on
`{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}` reach `IRoleAssignmentManager`
([07](07-rebac-authorization.md) § Azure RBAC, expressed in it), where the scope is any of the four
addresses above or a resource path. ⚠ **This one the router tries first, and the order is not free.**
On a resource group the address is a well-formed ten-segment resource path — a resource of type
`CyberCloud.Authorization/roleAssignments` — so tried second it would reach `IResourceManager` and be
refused as a type no provider serves. What keeps that from being a precedence rule nobody wrote down
is that `CyberCloud.Authorization` is a reserved namespace: `ProviderRegistry.Build` refuses a
provider that claims it, and under it only the two assignment grammars answer — the item's and,
since #86, the collection's below — so a malformed name is a `400` that names the grammar, never a
fall-through into the resource or resource-collection grammars and their `404`. `PUT` answers `201` on a grant and `200` on a repeat, `DELETE` answers `204`, and there
is no `202`: an assignment is one tuple write and converges before the call returns.

**The collection is served too (issue #86):** `GET {scope}/providers/CyberCloud.Authorization/roleAssignments`
— no name, no trailing `/` — is the second grammar under the reserved namespace, asked after the
assignment's and only on a `GET`, exactly as the resource collection is asked after the resource. It
answers `{ "value": [ … ], "nextLink": … }` with `$top` and `$skipToken` read and echoed the way the
resource collection reads and echoes them (#76), each element the object a by-name `GET` renders plus
`properties.inherited`, and an inherited row under the *ancestor's* address. A `PUT`, `PATCH`,
`POST` or `DELETE` on the collection path is a `400` that says the grant is one assignment with a
derived name — the sentence an ARM client that emitted `PUT …/roleAssignments/{guid}` and lost the
segment most needs. The listing is one `read` check on the scope and no per-row filter;
[07](07-rebac-authorization.md) § Azure RBAC, expressed in it says why that differs from the resource
collection and is still right. And a grant now checks the principal against the directory — the same
section — so a `PUT` naming a user this tenant does not have is a `400`, not a tuple.

⚠ **The role assignment API is not in the generated document, and that is #63's question asked a
third time.** The reserved namespace is exactly what keeps it out of the registry the emitters read,
and the scope extension #63 added carries a scope, not an address *on* one. So `cyc` and the SDK are
silent about it, as they were about scopes before #63, and a tenant grants a role from `cyc` today by
hand. The portal is the exception, and it is a hand-written one: `RoleAssignmentsApi`
(`portal/apps/portal/src/app/api/role-assignments.ts`) builds the address and the derived name and
sends them through the same transport as the generated client, so the access page (#22) grants,
checks and revokes without waiting on the emitter — and is the one page a regeneration cannot move.
#86 added the collection and changed nothing here. The fix has the same shape as #63's — a third non-registry source, emitted for every scope path and
every resource path as a sub-path — and it is owed rather than done because it touches all five
surfaces at once; when it lands, the portal's three methods become delegations.

**The resource graph is a fourth component behind the same door, at one address under the second
reserved namespace (#54):** `POST /tenants/{t}/providers/CyberCloud.ResourceGraph/resources` with
`{ "query": "resources | …", "$top": n, "$skipToken": "…" }` reaches `IResourceGraphQuery`
([08 § The resource-graph projection](08-resource-manager.md)), which translates a KQL subset into
one parameterised ClickHouse statement with the caller's access ANDed in and answers `{ "columns":
[ … ], "value": [ … ], "nextLink": … }`. Routed as `RouteKind.ResourceGraphQuery`, asked before
the scope grammars and before the `POST` branch: the address is five segments with `providers`
third, which parses as nothing else, but `ResolveAction` would otherwise have read it as the action
`resources` on a malformed resource id. Under the namespace the router asks this one grammar and no
other, so `…/resources/main` or `…/queries` is a `400` naming the one address, as a malformed role
assignment path is under its namespace. It is the one `POST` in this API that is not an action —
a query is a program, and a URL is not where one goes — and a `GET` on it is a `405` with
`Allow: POST`, the third address to answer `405` after the scope and the role assignment. Stage 5
counts it as a read: a `POST` charged to the subscription-write bucket would spend the small one on
a portal's list page. The page parameters are read from the body and, for a `nextLink`, from the
query string, because the link is the whole next request; a client follows it by `POST`ing the same
body.

⚠ **The resource graph's address is not in the generated document either, and that is #63's
question asked a fourth time.** The reserved namespace keeps it out of the registry the emitters
read, exactly as `CyberCloud.Authorization`'s does, so `openapi/`, the three SDKs and the portal's
generated client are silent about it and `cyc graph query` — hand-written beside `cyc rest`,
[21 § Grammar](21-cli-and-sdks.md) — is the CLI's whole knowledge of it. The fix is the same third
non-registry source the role assignment API waits on, one path rather than a sub-path of every
scope, and it is owed with that one because it touches the same five surfaces.

**A deployment's `whatIf` is a fifth component behind the same door, and unlike the last two it is in
the generated document (#39).** `CyberCloud.Resources/deployments` is a registered type, so its `PUT`,
`GET`, `DELETE` and collection are the ordinary resource routes to `IResourceManager` and its
`whatIf` is declared like any action — but the declaration names an entry point instead of a handler
(`ActionRegistration.EntryPoint`), and stage 8 sends `POST …/deployments/{name}/whatIf` to
`IDeploymentManager` rather than to `IResourceManager.ActionAsync`. Two properties force it: a what-if
answers for a deployment that need not exist, and `ActionAsync` refuses an action on an absent
resource because `POST` never creates; and it reads every resource the template names *as the
caller*, which an action handler — handed an `ActionContext` with no caller, by design — cannot. It
answers `200` with `{ "status", "creates", "modifies", "noChanges", "changes": [ … ] }` and no
`Azure-AsyncOperation`: the three typed arrays of resource ids are the verdict the document declares
(`Deployments.WhatIfResponse`), and `changes` is Azure's array of objects beside them, admitted by an
open schema because `SchemaKind` can't declare it (the review of #39 found this sentence still giving
only the last). Routing is still
not a decision: the entry point runs step 1's ownership checks and the action's permission check
itself, behind the same seam. ⚠ The gateway never names `IResourceManager.WriteChildAsync`, the
door a deployment's children go through as their recorded caller —
`GatewayIsolationTests.NoGatewaySourceFileWritesAsARecordedCaller` reads this project's source for it.

## Request pipeline

Order matters and each step is here for a named reason.

| # | Stage | Notes |
|---|---|---|
| 1 | **Correlation** | `x-ms-correlation-request-id` in (Azure's header, because tooling already sends it), `x-cybercloud-request-id` out. Both on every log line and every span |
| 2 | **Authenticate** | JWT (users, service principals, workload identity) or a session cookie **only on the identity host**, never here |
| 3 | **Resolve tenant** | From the token's `tid`. Directory cache lookup ([05](05-state-and-storage.md)) |
| 4 | **Region routing** | Not this region → proxy to the home region's gateway, preserving the correlation id. One hop, never two |
| 5 | **Rate limit** | Redis-backed sliding window. **Never touches a grain** — a rate limiter that costs a grain call is a rate limiter that amplifies an attack |
| 6 | **Route** | Path → provider + type + api-version, from the registry |
| 7 | **Validate** | JSON Schema for that api-version, before any grain call |
| 8 | **Dispatch** | To `CyberCloud.ResourceManager` ([08](08-resource-manager.md)), which owns authz, quota, locks |
| 9 | **Shape the response** | `Result` → status + body; errors to the one error shape |

Steps 5 and 8 are the load-bearing ones. Rate limiting before dispatch means a flood costs Redis
`INCR`s, not grain activations. Authorization *inside* dispatch rather than as gateway middleware means
the gateway cannot be bypassed by a future internal caller, and there is exactly one enforcement seam
([07](07-rebac-authorization.md)).

⚠ **Step 9's resource body is the envelope around the api-version's projected body, and `location`
is served once, from the manager's own record.** The grain projects a resource to
`{ "location": …, "properties": { … } }` — the body `openapi/{version}.json` publishes for the type,
and what every provider's conformance run compares to the body it wrote — and the gateway writes
`id`, `name`, `type`, `location`, `provisioningState` and `etag` around it, then the body's own
members, then `tags`. Every published type declares `/location` as a required, immutable body
property, so the value arrives in the body and the write path copies it onto the resource at step 9
of [08](08-resource-manager.md) § The write path; that copy is the one served, and the body's stays
off the wire. Issue #72 is what happens when the writer takes the projected document for the inner
`properties` slice — `properties.properties.*` and `location` twice — and it went unseen because the
gateway suite's substitute manager hand-wrote the shape the writer expected. The substitute now
builds its snapshot through the grain's own projection, so the two cannot drift apart on one commit.
Issue #85 is the other half: the published schema described only the projected body, so the five
members the gateway writes around it were forbidden by the document's own `additionalProperties:
false`. Every type's schema now `allOf`s a shared `Resource` component and repeats its five members
as `readOnly`, the `202` declares the body it always carried, and
`ServedShapesMatchTheDocumentTests` validates what this step serves against that document —
[21](21-cli-and-sdks.md) § Generation says why that shape and not a separate read schema.

⚠ **Step 3 is a security boundary, not a routing convenience, and this was not obvious.** The
gateway is an Orleans **client**, and `Orleans.Multitenant`'s call filter skips clients entirely
([00 § The tenant-separation row, corrected](00-vision-and-principles.md) has the decompiled proof).
So the runtime will **not** stop a gateway code path from reaching another tenant's grain by naming
its key — the tenant resolved at step 3 is the only thing that does.

Concretely, and this is a review rule with teeth:

- Every grain reference the gateway obtains comes from `IGrainFactory.ForTenant(t)` where `t` is the
  tenant resolved from the **token**, never from the path, the body or a header. A path segment that
  disagrees with the token is a `404`, resolved before dispatch.
- **Raw `IGrainFactory.GetGrain(key)` is banned in gateway code.** A caller-influenced key reaching
  it is a cross-tenant read with no exception and no log line. This needs an analyzer or an
  architecture-test gate; until it has one it is upheld by review, which is weaker than every other
  isolation claim in this plan and should not be left that way for long.

## Rate limiting

| Bucket | Default | Rationale |
|---|---|---|
| Per subscription, reads | 12 000 / 5 min | Azure's ARM read limit, and it is a sane number |
| Per subscription, writes | 1 200 / 5 min | Writes cost a reconcile |
| Per tenant, total | 30 000 / 5 min | Stops one subscription's automation starving the tenant's portal |
| Per IP, unauthenticated | 60 / min | Sign-in, token, discovery |
| Per user, interactive | 600 / min | Generous; the portal is chatty |

`429` with `Retry-After` and `x-ms-ratelimit-remaining-*`, because every cloud SDK's retry policy
already understands those headers.

⚠ **Long-poll and SignalR are exempt from the request-count limits and get a concurrency limit
instead** (connections per tenant, streams per connection). Counting a 30-second long-poll as one
request against a 5-minute window is how you accidentally rate-limit your own portal.

⚠ **The per-IP row's rationale names endpoints the identity host serves, and that host counts them
now.** Sign-in and token are not on this origin ([§ Request pipeline](#request-pipeline) puts them on
`CyberCloud.Identity.Host`), so on this table the row reaches only the anonymous routes of
[§ Shape](#shape). The sliding-window counters behind every bucket here moved to
`CyberCloud.ServiceDefaults.RateLimiting` (#94) so that host could count through the same window
arithmetic with its own buckets — [11 § Credentials](11-identity.md#credentials) says which endpoints
and which numbers. One counter implementation, two hosts' worth of buckets, no second window that
resets on a boundary.

## API versioning

`?api-version=2026-08-01`, required, on every request. Missing → `400` naming the current version.

**Why a query parameter rather than a header or a path segment:** it survives being pasted into a
browser, it appears in logs without extra configuration, and it is what every Azure tool already emits.
Header versioning is cleaner and loses all three.

Versions are dates and immutable ([08](08-resource-manager.md)). The gateway resolves version →
schema → mapping, so an old client keeps getting the shape it was written against indefinitely.

## Long-running operations, over HTTP

```
PUT …/servers/main?api-version=2026-08-01
→ 202 Accepted
  Azure-AsyncOperation: https://api.cybercloud.io/operations/{opId}?api-version=2026-08-01
  Retry-After: 10

GET /operations/{opId}
→ 200 { "status": "Running", "percentComplete": 40,
        "progress": [ { "at": "…", "step": "etcd", "message": "etcd cluster ready" } ] }
→ 200 { "status": "Succeeded" }   → then GET the resource
→ 200 { "status": "Failed", "error": { "code": "…", "message": "…" } }
```

Azure's `Azure-AsyncOperation` pattern exactly, so `Operation<T>` in the SDK and `--wait` in the CLI
are the standard implementation rather than a bespoke one. The `progress` array is our addition and it
is what makes a nine-minute cluster creation ([09](09-kubernetes-fabric.md)) tolerable.

## SignalR

Four hubs, and the split is by lifecycle rather than by feature.

| Hub | Purpose | Backplane |
|---|---|---|
| `/hubs/resources` | Resource-changed events for the blades a user is looking at | Orleans streams → hub |
| `/hubs/operations` | Operation progress | Same |
| `/hubs/terminal` | The cloud shell ([19](19-cloud-terminal-and-virtual-desktop.md)) | Direct to the session grain — binary, no backplane |
| `/hubs/metrics` | Live metric tiles | Pre-aggregates from the hot tier, polled server-side |

**No SignalR backplane product.** The Redis backplane broadcasts every message to every server, which
is the wrong shape here: our fan-out is already `tenant → interested connections`, and Orleans streams
already do exactly that. A connection registers its interests with a `IConnectionGrain` (hot tier,
dies with the connection); the grain subscribes to the relevant streams; messages arrive at the one
gateway pod holding that connection. This is O(interested) rather than O(pods).

⚠ **Subscription authorization is per-subscribe, not per-connect**, and it is re-checked on relation
changes. A user who loses access to a resource group must stop receiving its events — otherwise the
live-update channel is an authorization bypass with a nice UI. The `IConnectionGrain` subscribes to
the tenant's relation-version stream and drops now-unauthorized interests.

**Reconnect** is the portal's own — a fresh ticket and a new socket, on a backoff ladder — plus a
`since` version on resubscribe, so a portal tab that slept through a deploy catches up rather than
showing stale state forever. ⚠ Not SignalR's `withAutomaticReconnect`, for any browser client of any
hub: it reopens the URL the connection was built with, and that URL holds a ticket that was spent when
the connection first opened, so the reconnect would be a `401` every time. This paragraph said
"SignalR's automatic reconnect" until the ticket landed; the terminal is the first client written the
new way (`TerminalSession`), and the three live-update hubs follow it when the portal opens them.

⚠ **A browser opens a hub with a ticket, never with the bearer token in the URL.** Stage 2 reads the
`Authorization` header and nothing else, and a browser cannot put a header on a WebSocket; the SignalR
client's own convention fills the gap with `?access_token=<bearer>`, which puts a ten-minute token in
every proxy log and browser history on the path. So the portal asks `POST /hubs/{hub}/ticket` — an
ordinary authenticated request, counted as the write it is — for a *ticket*: 32 random bytes, bound to
the claims of the request that minted it and to that one hub, good for thirty seconds or until the
token behind it expires, and redeemable once. The upgrade is `GET /hubs/{hub}?ticket=…` with no
header; stage 2 redeems the ticket and parks the same claims the header would have, so stage 3 builds
the same caller and the hub sees no difference. A ticket is read only on the exact hub path — never on
the ticket route, so a ticket cannot mint the next ticket — and only when the header is absent, so it
is never a second chance for a refused token. The store is Redis where the rate-limit counters are and
in-process otherwise, for the same reason: N pods, and the pod that minted is not the pod the socket
lands on. `HubTickets` in the gateway carries the rest; `HubTicketOverHttpTests` drives it through
Kestrel and a real upgrade. ⚠ The portal skips SignalR's negotiate for the same reason it cannot
reuse a URL to reconnect: the ticket is spent by whichever request reaches the gateway first, so the
upgrade has to be that request, and a reconnect mints again. The negotiate route itself,
`/hubs/{hub}/negotiate`, routes to its hub now; until the ticket landed it was looked up as a hub
named `resources/negotiate` and answered 404 to every client that negotiated.

## Authentication inputs

| Caller | Credential | Notes |
|---|---|---|
| Portal | Authorization Code + PKCE → access token in memory, refresh in an `HttpOnly` cookie scoped to the identity host | Access token never in `localStorage`. The cookie is `__Host-cyc-refresh`, `SameSite=Lax`, and the identity host both writes it and reads it back only when the request's `Origin` is one of the portal's registered redirect-URI origins — `Lax` lets a same-site subdomain's `POST` carry it, and a top-level cross-site form `POST` would have its `Set-Cookie` honoured whatever `SameSite` says; the `Origin` check is what refuses both ([11 § Protocol](11-identity.md#protocol)) |
| CLI | Device code, or client credentials for CI | Token cached in the OS keychain |
| SDK | `TokenCredential` — the Azure SDK shape, so the mental model transfers | |
| Service principal | Client credentials, or a certificate | |
| Workload in a tenant cluster | Its projected SA token, exchanged for a platform token against the cluster's trusted OIDC issuer | This is managed identity ([11](11-identity.md)) and it is the reason a tenant's app needs no stored secret |

**Tokens are short (10 minutes) and scoped to a tenant.** Long-lived tokens are the single most common
cloud-credential incident, and a 10-minute token with a refresh flow costs the SDK one line.

## What the gateway must never do

| Never | Because |
|---|---|
| Query a database directly | Every read is a grain call or the resource-graph projection. A gateway with a `DbContext` is a second write path within a year |
| Hold per-request state across pods | It is stateless by construction; SignalR connection state lives in a grain |
| Expose an internal route the portal uses and the SDK does not | [00](00-vision-and-principles.md) — one API, or the SDK is a second-class citizen |
| Perform *authorization* itself | One seam, in the resource manager. ⚠ **Tenant establishment is a different thing and it IS the gateway's job** — see below |
| Proxy raw Kubernetes | A tenant who wants `kubectl` gets a kubeconfig for *their* cluster from the cluster resource's `listCredentials` action. The gateway is not a Kubernetes proxy, and turning it into one would put the fabric's credentials on the request path |

## Effort

| Piece | EM |
|---|---|
| Pipeline, routing from registry, validation, error shaping, versioning | 1.2 |
| Auth: JWT, service principals, workload identity exchange, region proxy | 1.0 |
| Rate limiting + quota surfacing | 0.4 |
| SignalR hubs, connection grains, interest authorization, reconnect | 1.2 |
| LRO endpoints, OpenAPI serving | 0.4 |
| **Total** | **4.2** |
