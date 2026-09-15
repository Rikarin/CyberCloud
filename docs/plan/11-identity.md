# 11 — Identity

"Basically Azure Entra", per the brief. The identity system is on the hot path of every request in the
platform, which is why ADR-015 rejects running someone else's.

## Hosts

Two kinds, and the split is a security boundary rather than a scaling one: the one host that sets a
cookie, and every host that accepts a token.

| Host | Serves | Auth style |
|---|---|---|
| `CyberCloud.Identity.Host` | `/authorize`, `/token`, `/userinfo`, `/.well-known/*`, sign-up, sign-in, reset, MFA enrolment, consent | **Cookies** — and only here |
| `CyberCloud.Gateway.Host` | The resource API — everything under `/subscriptions/…` | **Bearer tokens** — and only these |
| `CyberCloud.Registry.Feeds.Host` | The NuGet, npm and Maven wire protocols of `ContainerRegistry/feeds` ([13 § Artifact feeds](13-compute-vm-containers.md#artifact-feeds--cybercloudcontainerregistryfeeds--m2--15-em)) | **Bearer tokens** — the same tokens, validated against the same JWKS by the shared `CyberCloud.Identity.Validation`, and carried the way each client can carry one: `Authorization: Bearer`, a Basic password, or `X-NuGet-ApiKey` |

⚠ **A data-plane host is a third row, not a third auth style.** The feeds host (#29) validates
exactly what the gateway validates and mints nothing; it exists on its own origin because a package
client speaks its own protocol, not the resource API. What it must never do is accept a credential
of its own — a feed-local API key would be a second identity system with none of [§ Credentials](#credentials)'
rules — and its isolation test pins that it references no identity store.

A session cookie must never be a credential the resource API accepts. If it is, every CSRF becomes a
control-plane write. Separate hosts on separate origins makes that structural instead of a middleware
configuration somebody will change.

## The object model

| Object | Grain | Notes |
|---|---|---|
| **User** | `IUserGrain` (durable) | A human. Belongs to one tenant. GUID id, email is an attribute, not the key |
| **Group** | `IGroupGrain` | Membership is ReBAC tuples ([07](07-rebac-authorization.md)), not a list in the grain — so nesting, inheritance and `ListObjects` come free |
| **Service principal** | `IServicePrincipalGrain` | Machine identity with client credentials or a certificate |
| **Application** | `IApplicationGrain` | OAuth client registration: redirect URIs, grant types, scopes, secrets |
| **Managed identity** | `IManagedIdentityGrain` (durable) | A workload identity bound to a cluster + namespace + service account. Its grain key is `mi/{managedIdentityId:N}` — [06 § Grain keys](06-tenancy-and-resource-model.md) |
| **Credential** | *(inside the user)* | Password hash, TOTP secret ref, passkey credentials, recovery codes |
| **Session** | `ISessionGrain` (**hot**) | Device, IP, issued-at, refresh chain, revocation |

**Groups hold no member list.** This is the decision that makes the identity module small: membership
is `group:X#member@user:Y`, so "is Alice in Eng" is a `Check`, "who is in Eng" is an `Expand`, nested
groups work with no extra code, and revoking a group's access is a tuple write. A member list in grain
state would be a second source of truth and a hot spot for large groups.

## Protocol

OpenIddict 7.3.0 (ADR-015), OAuth 2.1 + OIDC.

| Flow | For | Notes |
|---|---|---|
| Authorization Code + PKCE | Portal, third-party apps | The only interactive flow. No implicit, no hybrid |
| Device Authorization | `cyc login` on a headless box | |
| Client Credentials | Service principals, CI | |
| Refresh Token | All of the above | Rotating, one-time-use, with reuse detection → revoke the whole chain |
| Token Exchange (RFC 8693) | Workload identity | A cluster's SA token → a platform token |
| ~~Resource Owner Password~~ | — | ✗ Removed in OAuth 2.1 and it defeats MFA |

**Tokens.** Access tokens are JWTs, 10 minutes, signed with a rotating key set (30-day rotation, both
keys published for 60). `aud` names the API, `tid` the tenant, `sub` the GUID, plus `scp`, `azp`, and
an `auth_time`/`amr` pair so step-up authentication can be required for sensitive actions.

⚠ **That sentence is short by two claims, and the gateway cannot do its job without either of them.**
Both were discovered when `AccessTokenClaims.Permitted` — a *closed* allow-list built from the
sentence above — met the requirements the gateway had written down independently in
`ICallerContextResolver`:

| Claim | Why the token needs it |
|---|---|
| `sub_typ` | The **subject type**, one of `user`, `servicePrincipal`, `managedIdentity`. ReBAC subjects are typed ([07 § The model](07-rebac-authorization.md)), so `user:abc` and `servicePrincipal:abc` are different subjects and `sub` alone does not identify one. ⚠ A dedicated claim, never a `type:id` prefix on `sub` — a prefix makes the type a substring of a value that is also a key, an audit field and a log line, and every consumer then needs the same splitting rule |
| `act_sub` | The **impersonating operator**, for [06 § Platform administration](06-tenancy-and-resource-model.md). The flattened `act.sub` of RFC 8693 § 4.1, absent entirely on an ordinary token. ⚠ Minted by the identity host and read from no request surface: that document says the value rides in an `X-CyberCloud-Impersonated-By` header, which is correct on the internal gateway→resource-manager hop and **wrong at the public edge**, where a caller sets their own headers and could therefore name any operator in the audit trail |

The set is still closed — fourteen claims, and adding a fifteenth is an edit in two assemblies plus
an assertion on both sides, which is what closure is for.

⚠ **Roles and permissions are *not* in the token.** They are looked up per request from ReBAC. Putting
role claims in a 10-minute token means a revoke takes up to 10 minutes, and packing a large user's
groups into a JWT produces the header-size failures every large enterprise hits. The cost is a
`Check` per request, which is the p99 < 10 ms budget in [00](00-vision-and-principles.md), and is why
that budget exists.

⚠ **What is served today, and what is not.** Three rows of the flow table are served, and a person
can hold a token. `/authorize` + PKCE mints an authorization code from the fully authenticated
session cookie; `/token` exchanges it for an access token, an id_token and a refresh token, and
rotates the refresh token; the client-credentials grant serves service principals as before. The
server runs in OpenIddict's *degraded mode*: ADR-015's "the stores are grains" means the library's
own client validation has nothing to read from, so the host validates requests itself
(`DegradedModeHandlers` in the identity host) and OpenIddict handles the protocol, the signing and
the discovery document. `aud` is spelled once, as `AccessTokenPolicy.Audience`, and the gateway pins
it. The decisions that shape the served half, each argued in the type that makes it:

- **One issuer, and the tenant is a request parameter.** An address alone resolves nothing on this
  platform ([§ Sign-up and tenant creation](#sign-up-and-tenant-creation) refuses a global email
  index), so `/authorize` and the three first-factor sign-in bodies take `tenant` — a tenant id or a
  slug — and `TenantHint` resolves it through the platform tenant directory *before* any per-tenant
  grain is touched, which is what answers the objection that a caller-chosen tenant "would let an
  unauthenticated caller choose which tenant's lockout counters and email index it probes": a
  made-up value activates nothing. Absent, the configured tenant applies, or the platform tenant in
  Development only. Never a tenant in the path or the issuer: OpenIddict serves one issuer and the
  gateway pins one issuer string, and `tid` carries the tenant on every token.
- **First-party clients are static.** `cyc-portal` and `cyc-cli` are `ApplicationRegistration`
  records in the host (`FirstPartyClients`), consulted before a tenant's `IClientIndexGrain` so no
  tenant can shadow them; `cyc-cli`'s loopback redirect matches any port, RFC 8252 § 7.3. Every
  other client is the tenant's own, resolved through its index — and answered `consent_required`
  at `/authorize` until the consent page exists.
- **The refresh token is OpenIddict's envelope around the session grain's handle.** The exchange
  opens one `ISessionGrain` per (user, client) — a *token session*, bound to the interactive cookie
  session by `cyc:isid` — and the refresh token carries its handle as `cyc:rh`. Rotation, one-time
  use and reuse detection are `ISessionGrain.RefreshAsync`'s; a replay revokes the chain, and a
  refresh whose cookie session has been signed out revokes the token session on the spot, which is
  how `/logout` ends every chain a sign-in produced without enumerating them. For the browser client
  the refresh token lives in `__Host-cyc-refresh`, an `HttpOnly` cookie on this origin — the row in
  [10 § Authentication inputs](10-gateway-and-api.md#authentication-inputs) — read back only when
  the request's `Origin` is one of the portal's redirect-URI origins; for the CLI it stays in the
  body. The access token on the wire is exactly `AccessTokenClaims.Permitted`: OpenIddict's own
  `scope`, `client_id` and presenter claims are stripped before signing, because `scope` is on the
  forbidden list and the gateway refuses a token that carries it.
- **Keys persist on the development run, and nowhere else.** `IdentityHostOptions.DevelopmentKeyDirectory`
  keeps the ES256 signing key, the encryption key and the data-protection ring on disk under the
  AppHost's `.identity/`, so a restart does not sign every portal tab out — both keys, because codes
  and refresh tokens are encrypted with the second. Set outside Development the host refuses to
  start, naming `CyberCloud.Vault`; unset means ephemeral keys. The rotation story above stays owed
  to the vault.

⚠ **The `client_id` → application index degraded mode requires has landed, which is the half #68
closed toward the person's token path.** `IClientIndexGrain` (`CyberCloud.Tenancy.Contracts`) maps a
`client_id` to its `applicationId` per tenant — keyed `idx/client/{sha256(tenantId + clientId)[..16]}`,
the same shape as the email index — and `ApplicationGrain.CreateAsync` claims it before it writes, so
two applications cannot share a `client_id` and an authorization request can resolve one to its
registration without OpenIddict's own store (which degraded mode turns off). `ClientIndexTests` and
`ApplicationRegistrationTests` pin it. The index is claimed in the order [06 § Two-phase
create](06-tenancy-and-resource-model.md) fixes, and where that document sweeps a resource orphaned
between the write and the confirm with a reaper reminder, `ApplicationGrain` settles itself on its
next call instead — `ApplicationGrainState.ClientIdConfirmed` is the marker, and an orphan whose
`client_id` another application has since taken is dropped rather than left as a second registration
naming one id. `ClientResolver` in the identity host is the reader.

**What is still owed on the person's path**, each named where the code refuses it:

- **The consent page.** A tenant-registered client is answered `consent_required` at `/authorize`;
  first-party clients are consent-free.
- **One-time use of an authorization code.** Degraded mode has no token store to burn a code in;
  a code lives five minutes and PKCE binds a replay to the verifier only the legitimate tab holds.
  A hot-tier code store is the fix.
- **`/userinfo`.** Not mapped; the portal reads `tid` and `sub` off the access token and `email`
  and `name` off the id_token.
- **The signing key from the vault.** `DevelopmentKeyFile` is the development run's answer and
  refuses to be anything else; the fix is `CyberCloud.Vault` through the seam docs/plan/18 names,
  wired in `Identity.Host` beside `IClientSecretSeam` and `ITotpSecretSeam`.
- **An HTTP surface that creates a service principal at a tenant** — `TenantOverHttpTests` still
  creates one by grain, and the client-credentials grant still takes its tenant from configuration
  rather than from an application registration.
- **The person half of `TenantOverHttpTests`** — the path `GrantsOverHttpTests` drives against the
  identity host alone (password, delivered code, `/authorize`, `/token`, refresh, replay, restart,
  `/logout`), with the gateway on the far end.
- **Device authorization and token exchange (RFC 8693)** remain owed as before — the device flow
  needs a verification page and a code store, and token exchange has `ITokenExchange` built and
  waiting on `/token` to accept the grant.

## Credentials

| Method | M | Implementation | Notes |
|---|---|---|---|
| **Passkeys (WebAuthn)** | M1 | `Fido2.AspNet` behind `IPasskeyService` | ⚠ The **default** offered credential at sign-up, not an upsell. A platform starting in 2026 that leads with passwords is choosing the worse security posture on purpose |
| Password | M1 | Argon2id, `m=64MB, t=3, p=4`, per-user salt, pepper from Vault | Breach-list check against a local k-anonymity HIBP mirror at set time |
| TOTP | M1 | RFC 6238 in-house, ~200 lines, ±1 window, replay-blocked per (user, counter) | Secret stored as a Vault `SecretRef`, never in grain state |
| Email OTP | M1 | 6 digits, 10 min, 5 attempts | Via `CyberCloud.Communication` |
| SMS OTP | M1 | Same | ⚠ Weakest factor — SIM swap. Offered, never the only factor for an admin |
| WhatsApp OTP | M2 | Meta Cloud API | Template pre-approval is a business task |
| Recovery codes | M1 | 10 × 10 chars, single-use, hashed | Shown once. The thing that prevents "I lost my phone" tickets |
| Certificate | M2 | mTLS for service principals | |

**Rate limiting and lockout.** Per-account exponential backoff with a global per-IP limit, and — the
detail that matters — **the lockout counter lives in the hot tier keyed by the user id**, so it is a
Redis `INCR`, not a grain call. An authentication endpoint whose failure path costs a grain activation
is a denial-of-service amplifier.

**Enumeration.** Sign-in, password reset and sign-up return the same response and take the same time
whether or not the account exists. The reset email is the only signal, and it goes to the address
typed regardless.

## Sign-up and tenant creation

Two paths, and they are different products.

**Self-serve.** Email + passkey → verify → create tenant → create default subscription and resource
group → seed ReBAC (`tenant:X#owner@user:Y`) → optionally provision an in-house cluster. A long-running
operation with a step list ([06](06-tenancy-and-resource-model.md)).

⚠ **What shipped for M1 is a synchronous, re-drivable step list, and the long-running operation is
still owed.** The chicken and the egg the sentence above hides: a one-time code is minted and
delivered by a *user's* grain, which lives in a *tenant's* shard, and a self-serve sign-up has
neither yet. So the pre-tenant state — the address, the pre-allocated tenant, user and subscription
ids, the enrolment code's keyed digest, and which create steps have run — lives in a platform-tenant,
hot-tier `ISignUpGrain` keyed `signup/{id:N}` by a random id the identity host hands the browser in
a data-protected `__Host-cyc-signup` cookie. Never by the address: a sign-up reachable by address is
the global email index this section refuses. The identity host's `SignUpOrchestrator` then drives
the create steps in order — tenant (through `IScopeManager.CreateTenantAsync`, as the seeded
sign-up operator `servicePrincipal:00000000-0000-0000-0000-00000000c1c0`, owned by the new user),
user and email-index claim, credential, default subscription and default resource group (both
through `IScopeManager.CreateAsync` *as the new user*, which exercises the owner tuple the way the
portal would) — recording each in the grain so a retried `complete` resumes rather than duplicates.
The orchestrator is in the host rather than in the grain because `PlatformCrossTenantAuthorizer`
denies a platform grain reaching into a tenant, and the host is an Orleans client outside that
filter. Two things make it possible on a fresh run at all: the silo's `PlatformBootstrapTask` seeds
the shard map from the configured durable shards and writes `platform:root#operator` for the sign-up
operator when `CyberCloud:Identity:SelfServeSignUp` is on, and — there being no MTA (#93) — a
Development-only `DevelopmentOtpDelivery` writes the enrolment code to the silo's log, where the
Aspire dashboard shows it. The progress UI, the welcome mail and the optional cluster are the part
of [06 § Tenant lifecycle](06-tenancy-and-resource-model.md)'s operation still owed; the step record
in the grain is its seed.

**Invited.** An existing tenant owner invites an email into their tenant with a role. The invitee
either signs in (if they already have a user in *another* tenant — see below) or signs up.

⚠ **A user belongs to exactly one tenant.** The same human with accounts in two tenants has two user
objects with two GUIDs and (probably) the same email. This is Azure's guest-user problem and Azure's
answer (B2B guests) is complicated. The M1 answer is the simple one: one user, one tenant, and the
portal's account switcher is a client-side list of tokens. Revisit at M3 if customers actually ask;
committing to cross-tenant identity in M1 would put a global user index on the hot path, which
[05](05-state-and-storage.md) is specifically arranged to avoid.

**Email uniqueness is per tenant**, enforced by `IEmailIndexGrain` keyed by
`hash(tenantId + normalized email)`. Global email uniqueness would be a global index — the thing we do
not have and do not want.

## Managed identity — the feature that removes stored secrets

A tenant's workload in a tenant cluster needs to read a Vault secret or write to a bucket. The bad
answer is a client secret in a Kubernetes `Secret`. The good answer:

1. The tenant creates `CyberCloud.ManagedIdentity/userAssignedIdentities/app-prod`.
2. They bind it to `(cluster, namespace, serviceAccount)`.
3. The platform records the cluster's OIDC issuer URL and JWKS (read once, refreshed).
4. The workload's projected SA token is presented to `/token` with `grant_type=token-exchange`.
5. The **identity host** validates the SA token against that issuer, matches the binding, and issues
   a platform token for the managed identity.
6. ReBAC grants are made to `managedIdentity:{id}` like any other subject.

⚠ **Step 5 used to say "the gateway", and that was a defect rather than a wording preference.**
Step 4 puts the exchange at `/token`, and [§ Hosts](#hosts) above puts `/token` on
`CyberCloud.Identity.Host`. The gateway serves bearer tokens and mints none — it references
`OpenIddict.Validation.SystemNetHttp` and deliberately not `OpenIddict.Server.AspNetCore`, which is
the package-level expression of that boundary. A gateway that could issue a token would be a second
authorization server on the origin whose entire job is to accept them.

⚠ **This sentence named `OpenIddict.Validation.AspNetCore` until the gateway actually took the
reference, and the package it took is a different one for a reason worth keeping.** The gateway's
stage 2 ([10 § Request pipeline](10-gateway-and-api.md)) is not an ASP.NET Core authentication
handler: the pipeline resolves its own `ICallerContextResolver`, and the AspNetCore integration would
register a scheme nothing consults — a second place authentication appears to happen. What stage 2
needs is the validation core plus the piece that fetches the discovery document and the JWKS over
`HttpClient`, and that is `OpenIddict.Validation.SystemNetHttp`. The boundary the paragraph above
draws is unchanged: Validation and never Server.

⚠ **`managedIdentity` is a third subject type, and it only works because the subject type is a
claim.** The token minted at step 5 carries `sub_typ: managedIdentity` beside `sub`, so step 6's
`managedIdentity:{id}` is an ordinary `SubjectRef` the gateway can build — see
[§ Protocol](#protocol) and `AccessTokenClaims.SubjectType`. Had the type been a prefix convention on
`sub`, this step would need the checker to know that a workload's subject is spelled differently from
a user's.

No secret is ever stored, on either side. This is exactly Azure Workload Identity and it is worth the
1.2 EM because it removes an entire incident class.

⚠ It requires the tenant's cluster to expose a **publicly reachable** OIDC discovery document, or that
we fetch the JWKS through the `AgentInitiated` tunnel ([09](09-kubernetes-fabric.md)). For BYO clusters
that is not automatic, and the portal must say so at binding time rather than failing at token
exchange.

## Sessions and revocation

Sessions are hot-tier grains. Refresh tokens carry a session id; refresh checks the session is live.
Revoking a session (sign out everywhere, password change, admin action, refresh-reuse detection)
invalidates the refresh chain immediately.

**Access tokens are not revocable and are not made so.** They live 10 minutes. An introspection call
per request would put the identity system on the hot path of every request, which is precisely what a
short token is for. Actions that genuinely cannot tolerate 10 minutes of stale authorization use
`FullyConsistent` ReBAC checks ([07](07-rebac-authorization.md)) — which is the right place for that
guarantee, because it is about *authorization*, not authentication.

## Auditing

Every authentication event — success, failure, MFA challenge, credential change, consent, token issue,
impersonation — goes to the telemetry pipeline as a structured event with the correlation id, IP,
user agent and geo. To ClickHouse, not to a SQL audit table (ADR-006), because the query people
actually run is "everything for this user in this window across every host" and that is a columnar
query.

**PII rule, enforced by the analyzer:** no email, name or IP in a log *message*. They go in structured
fields, which are subject to the retention and redaction policy; a message string is not.

## Effort

| Piece | EM |
|---|---|
| OpenIddict wiring, grain-backed stores, key rotation, discovery | 1.0 |
| User/group/app/SP grains, invitations, tenant bootstrap | 1.0 |
| Sign-up/in/reset/consent pages (Angular + xUI, SSR, on the identity host) | 0.8 |
| Passkeys, TOTP, recovery codes, email/SMS OTP, step-up | 1.0 |
| Managed identity + token exchange | 1.2 |
| Sessions, revocation, refresh rotation with reuse detection | 0.5 |
| Auditing + the enumeration/timing hardening suite | 0.5 |
| **Total** | **6.0** |
