# 11 — Identity

"Basically Azure Entra", per the brief. The identity system is on the hot path of every request in the
platform, which is why ADR-015 rejects running someone else's.

## Hosts

Two, and the split is a security boundary rather than a scaling one.

| Host | Serves | Auth style |
|---|---|---|
| `CyberCloud.Identity.Host` | `/authorize`, `/token`, `/userinfo`, `/.well-known/*`, sign-up, sign-in, reset, MFA enrolment, consent | **Cookies** — and only here |
| `CyberCloud.Gateway.Host` | Everything else | **Bearer tokens** — and only these |

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
| **Managed identity** | `IManagedIdentityGrain` | A workload identity bound to a cluster + namespace + service account |
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
| Device Authorization | `cc login` on a headless box | |
| Client Credentials | Service principals, CI | |
| Refresh Token | All of the above | Rotating, one-time-use, with reuse detection → revoke the whole chain |
| Token Exchange (RFC 8693) | Workload identity | A cluster's SA token → a platform token |
| ~~Resource Owner Password~~ | — | ✗ Removed in OAuth 2.1 and it defeats MFA |

**Tokens.** Access tokens are JWTs, 10 minutes, signed with a rotating key set (30-day rotation, both
keys published for 60). `aud` names the API, `tid` the tenant, `sub` the GUID, plus `scp`, `azp`, and
an `auth_time`/`amr` pair so step-up authentication can be required for sensitive actions.

⚠ **Roles and permissions are *not* in the token.** They are looked up per request from ReBAC. Putting
role claims in a 10-minute token means a revoke takes up to 10 minutes, and packing a large user's
groups into a JWT produces the header-size failures every large enterprise hits. The cost is a
`Check` per request, which is the p99 < 10 ms budget in [00](00-vision-and-principles.md), and is why
that budget exists.

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
5. The gateway validates the SA token against that issuer, matches the binding, and issues a platform
   token for the managed identity.
6. ReBAC grants are made to `managedIdentity:{id}` like any other subject.

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
