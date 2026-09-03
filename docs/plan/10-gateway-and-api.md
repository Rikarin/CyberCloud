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
          ├─ /.well-known OIDC discovery (proxied from Identity)
          └─ /openapi     the generated document, per api-version
```

**The scope API is the first four and six segments of the resource path, and it is a different
component behind the same door.** `GET` and `PUT` on `/tenants/{t}/subscriptions/{s}` and on that plus
`/resourceGroups/{rg}` reach `IScopeManager`; everything longer reaches `IResourceManager`. The two
grammars are disjoint — a resource address is at least ten segments and must contain `/providers/` —
so the router tries both without a precedence rule. A scope answers `201` on a create and `200` on a
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

**Reconnect** uses SignalR's automatic reconnect plus a `since` version on resubscribe, so a portal tab
that slept through a deploy catches up rather than showing stale state forever.

## Authentication inputs

| Caller | Credential | Notes |
|---|---|---|
| Portal | Authorization Code + PKCE → access token in memory, refresh in an `HttpOnly` cookie scoped to the identity host | Access token never in `localStorage` |
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
