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

⚠ **The tenant does hold a list of its users, and that is not the member list refused above.** Every
directory object is keyed by a random id, and the two indexes over them are keyed by digests (the
address, the `client_id`), so until #41 nothing could answer "who is in this organisation" or "which
applications has it registered" short of a scan of the durable store. `IDirectoryIndexGrain` —
durable, tenant-qualified, `idx/dir/{users|invitations|applications}` — is that answer: the ids, oldest
first, written by the object's own grain *before* the object (`UserGrain.CreateAsync`,
`InvitationGrain.CreateAsync`, `ApplicationGrain.CreateAsync`), so a crash between the two leaves an id
a reader skips as "not found" and never an object nothing lists. It records existence, not
membership: who belongs to a *group* is still the tuples. Capped at ten thousand ids a list, refused
past it rather than trimmed. ⚠ A tenant created before #41 lists only the users created since —
nothing can backfill a list of ids no index ever held, and the dev run's tenants are the only ones
that exist.

## Protocol

OpenIddict 7.3.0 (ADR-015), OAuth 2.1 + OIDC.

| Flow | For | Notes |
|---|---|---|
| Authorization Code + PKCE | Portal, third-party apps | The only interactive flow. No implicit, no hybrid |
| Device Authorization | `cyc login` on a headless box | RFC 8628, served since #43 — see the bullet below |
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

⚠ **What is served today, and what is not.** Four rows of the flow table are served, and a person
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
  gateway pins one issuer string, and `tid` carries the tenant on every token. ⚠ And the host is
  *handed* that string rather than inferring it from the request, on the AppHost as in production:
  a person's `/authorize` is resumed through the identity app's dev server, whose proxy forwards it
  with its own `Host` header, and the first dev run minted an authorization code under
  `http://localhost:4201/` that `/token` on `5101` refused (OpenIddict's ID2088) with every host
  healthy. `CyberCloudTopology` sets `CyberCloud:Identity:Issuer` to the one string the gateway and
  the feeds host already validate against.
- **First-party clients are static.** `cyc-portal` and `cyc-cli` are `ApplicationRegistration`
  records in the host (`FirstPartyClients`), consulted before a tenant's `IClientIndexGrain` so no
  tenant can shadow them; `cyc-cli`'s loopback redirect matches any port, RFC 8252 § 7.3. Every
  other client is the tenant's own, resolved through its index — and consent-gated: `/authorize`
  sends the person to the consent page (`portal/apps/identity`, `/consent`) unless `IConsentGrain`
  holds a grant for this (person, client) that covers every scope asked for, or `prompt=consent`
  asks anyway. The page renders the *registered* display name (`GET /api/consent` resolves it; a
  name from the request's own query would be a phisher's) and posts the request's parameters back
  to `/authorize` with `consent=allow` or `consent=deny`, honoured only on a `POST` from the page's
  origin — so a `GET` link carrying `consent=allow` is a request with no answer. Allow records the
  grant (durable, per (person, client), scopes unioned) *before* the code is minted; deny is
  `access_denied` at the registered redirect URI through OpenIddict's own error redirect;
  `prompt=none` with nothing on record is `consent_required`. A grant is revocable
  (`IConsentGrain.RevokeAsync`) and the next request asks from scratch. #94.
- **A confidential client authenticates on the code and refresh grants.** RFC 6749 § 4.1.3 and
  § 6: `DegradedModeHandlers.ValidateTokenRequest` requires `client_secret` from a client whose
  registration is not public and verifies it through `ClientSecretVerifier` before the origin and
  grant checks and before the grant's grains are touched; missing, wrong and unreadable are one
  `invalid_client` sentence. Landed with the consent page (#94), which is what made a tenant client's
  code mintable at all; a public client that sends a secret is still refused. ⚠ **A secret lives in
  one of two places since #41, and the verifier asks the right one.** A registration that names a
  `ClientSecretRef` is checked through `IClientSecretSeam` against the vault — the seam the
  client-credentials grant checks a service principal through, and the one #94's tests register.
  A registration made through the administration API has a secret the *platform* minted — 256
  random bits, shown to the owner once — and nothing ever needs it back, only compared, so the
  application grain keeps its SHA-256 (`IApplicationGrain.IssueClientSecretAsync`) and answers
  `VerifyClientSecretAsync` in constant time; `ApplicationRegistration.ClientSecretIssuedAt` says
  which, and the issued secret wins where both exist, because a rotation is the newer intent. A
  plain digest rather than Argon2id is a decision about the input: there is no dictionary for 256
  random bits, and a slow hash would tax every exchange a server client makes. Rotation replaces the
  digest in one turn, so the old secret dies at once — `IdentityAdministrationThroughTheGatewayTests`
  registers a client through the gateway, exchanges a code with the secret it was shown, rotates, and
  watches the old one get `invalid_client` at `/token`.
- **An authorization code is exchanged once.** RFC 6749 § 4.1.2, both halves. Degraded mode has
  no token store, so OpenIddict's own `CreateTokenEntry` never gives a code an id;
  `DegradedModeHandlers.StampAuthorizationCodeId` sets `oi_tkn_id` on the code's principal before
  it is signed, and `TokenApi.MintForCodeAsync` burns that id in `IAuthorizationCodeGrain` —
  hot tier, keyed `code/{jti:N}`, never by the code — *between* the interactive-session check and
  the token session's open, under the token session id it is about to open. A second exchange
  finds the record, is refused with one sentence, and revokes the session the first exchange
  opened (`RevocationReason.AuthorizationCodeReuseDetected`); `SessionGrain.OpenAsync` refuses to
  open over a revocation, so the race between two exchanges cannot lose it. The record clears
  itself one minute after the code's own expiry (`AccessTokenPolicy.AuthorizationCodeLifetime`,
  five minutes, one number for the host and the grain). PKCE stays in front of it: a wrong
  verifier is refused before the code is burnt, so a thief guessing verifiers costs the legitimate
  tab nothing. #94.
- **`/userinfo` is served, from the session.** OIDC Core § 5.3, `GET` or `POST`, advertised in the
  discovery document. OpenIddict validates the bearer access token; the passthrough asks the token
  session (`sid`) whether it is still live — a revoked one is `401 invalid_token` though the JWT
  has minutes left, which is the one thing this endpoint knows that the token cannot — and reads
  `name` and `email` off `IUserGrain` under the `profile` scope, as the id_token was minted from it.
  `sub` matches the id_token's; `tid` and `sub_typ` ride beside it so a relying party can build
  the subject reference the gateway builds. A token for anything but a person answers `sub` alone.
  #94.
- **The refresh token is OpenIddict's envelope around the session grain's handle.** The exchange
  opens one `ISessionGrain` per (user, client) — a *token session*, bound to the interactive cookie
  session by `cyc:isid` — and the refresh token carries its handle as `cyc:rh`. Rotation, one-time
  use and reuse detection are `ISessionGrain.RefreshAsync`'s; a replay revokes the chain, and a
  refresh whose cookie session has been signed out revokes the token session on the spot, which is
  how `/logout` ends every chain a sign-in produced without enumerating them. For the browser client
  the refresh token lives in `__Host-cyc-refresh`, an `HttpOnly` cookie on this origin — the row in
  [10 § Authentication inputs](10-gateway-and-api.md#authentication-inputs) — written and read back
  only when the request's `Origin` is one of the portal's redirect-URI origins; for the CLI it
  stays in the body. ⚠ Both directions, because the write is the login-CSRF: `Set-Cookie` on a
  top-level cross-site form `POST` is honoured whatever `SameSite` says, so a `/token` that set the
  cookie for any origin would let an attacker exchange a code for *their own* account from a page
  in the victim's browser and have the victim's next silent refresh sign them into the attacker's
  tenant. `DegradedModeHandlers.ValidateTokenRequest` refuses the browser client's code exchange
  and body-borne refresh from any other origin before a grain is touched, and
  `MoveRefreshTokenToCookie` writes the cookie for no other origin even if a response reaches it;
  `ExtractRefreshTokenFromCookie` is the read side. The access token on the wire is exactly
  `AccessTokenClaims.Permitted`: OpenIddict's own `scope`, `client_id` and presenter claims are
  stripped before signing, because `scope` is on the forbidden list and the gateway refuses a token
  that carries it.
- **Device authorization is RFC 8628 over the same degraded mode, with a grain for a store.**
  `/device` is open to `cyc-cli` alone (the one registration with the grant, and the request names no
  tenant to resolve another in), counted per IP (`IdentityRateLimits.DeviceAuthorization`, 20 per 10
  minutes). OpenIddict cannot make a user code self-contained and has no token store to remember one,
  so `DegradedModeHandlers.StoreDeviceCodes` mints both codes into `IDeviceAuthorizationGrain` —
  platform-tenant, hot, keyed `device/{digest(userCode)}` — at generation: the user code is eight
  letters from RFC 8628 § 6.1's twenty consonants, shown `BCDF-GHJK` and typed any way; the device
  code is `{userCode}.{256 random bits}`, and the grain keeps only the secret's SHA-256, so the user
  code on a screen is half a credential. Both live ten minutes and the response says `interval: 5`;
  ⚠ the grain measures the ten minutes from its own clock, as it does the interval, because an
  expiry instant stamped by the host would tie every code's life to the skew between two machines.
  A poll is answered from the grain, in one turn, with exactly § 3.5's errors — `authorization_pending`,
  `slow_down` (and five seconds more for this and every later poll, as state), `access_denied`,
  `expired_token` — and `invalid_grant` for a code that is spent or never existed; a guessed secret
  moves nothing. The person's half is the identity app's device page (`/device-code`, reached from
  `/device/verify`, which only redirects): enter the code (`/api/device/lookup`, anonymous), sign in
  through the existing flow if the cookie is not a complete sign-in, then allow or deny
  (`/api/device/decision`, the cookie's session and never the body, and only from the page's
  origin) — both in the `code-verify` bucket, and a user code is answered once. An approved code is
  redeemed at `/token` like an authorization code — the token session id recorded before the session
  opens, a second redemption refused and the first session revoked
  (`RevocationReason.DeviceCodeReuseDetected`), and, like a code, refused once the sign-in behind it
  is gone: the approving session must still be live, and the user still `Active` after the new
  session is tracked, so a code approved before a suspension or a "sign out everywhere" opens nothing
  (⚠ the first cut read neither, and the review of #43 got tokens and a live refresh chain for a
  suspended user). One deliberate difference: once the device has its tokens, its token session is
  bound to **itself**, not to the browser session that approved it, because the device and the
  browser are two machines and a sign-out on the phone must not end a build agent's CLI.
  `/revoke` (RFC 7009) takes a refresh token and ends its token session (`RevokedByClient`) — what
  `cyc logout` calls — and answers an access token `unsupported_token_type`, since access tokens stay
  irrevocable. `DeviceFlowOverHttpTests` pins every answer over the wire; `DeviceFlowThroughTheSdkTests`
  runs the SDK's credential, its refresh and its sign-out against the host. #43.
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

- ~~The consent page~~, ~~one-time use of an authorization code~~, ~~a confidential client's
  `client_secret` on the code and refresh grants~~ and ~~`/userinfo`~~ — landed as #94's four
  security items, each argued in the bullets above and pinned over the wire in
  `GrantsOverHttpTests`: `ATenantClientNeedsConsentAndGetsACodeOnceItIsGiven`,
  `AReplayedCodeIsRefusedAndRevokesTheSessionTheFirstExchangeOpened`,
  `AConfidentialClientMustPresentItsSecretOnTheCodeAndRefreshGrants` and
  `UserInfoAnswersTheSessionsClaimsAndDiesWithTheSession`. The same issue's per-IP limit on
  `/api/signup/begin` and the code-verify endpoints is in [§ Credentials](#credentials).
- **`/logout` on a bare link.** Any site can sign a person out: the end-session request is a
  top-level navigation, `Lax` sends the cookie, and `id_token_hint` is ignored. A nuisance, not a
  breach — a confirmation page, or binding `id_token_hint` and `state` to the cookie's session,
  closes it.
- **The signing key from the vault.** `DevelopmentKeyFile` is the development run's answer and
  refuses to be anything else; the fix is `CyberCloud.Vault` through the seam docs/plan/18 names,
  wired in `Identity.Host` beside `IClientSecretSeam` and `ITotpSecretSeam`.
- **An HTTP surface that creates a service principal at a tenant** — `TenantOverHttpTests` still
  creates one by grain, and the client-credentials grant still takes its tenant from configuration
  rather than from an application registration.
- ~~The person half of `TenantOverHttpTests`~~ — landed as `PersonOverHttpTests` in
  `CyberCloud.AppHost.Tests`: sign-up with the code read from the silo's console, `/authorize`,
  `/token`, the tenant and both scope collections through the gateway, a resource that converges,
  a refresh from the cookie, a replay refused, `/logout` — against the AppHost's own processes.
  #88's closing criterion (a person signs in, holds a `cyc.api` token, reads through the gateway,
  refreshes, survives a restart) was also performed by hand on the dev run, in a browser, on
  2026-09-15.
- ~~Device authorization~~ — landed with #43, in the bullet above. **Token exchange (RFC 8693)**
  remains owed: `ITokenExchange` is built and waiting on `/token` to accept the grant. ⚠ And two
  things the device flow leaves: a tenant-registered device client (the request would need a
  `tenant` to resolve one in), and the device page saying *where* the request came from — the
  flow's known weakness is a person talked into typing somebody else's code, and the page's only
  defence today is naming the account being lent and the scopes.
- **`displayName` on the tenant body.** `ScopeManagerService.ReadTenantAsync` renders the slug as
  `name` and `ScopeSnapshot` carries no display name, so `GET /tenants/{t}` has none and the
  portal's context bar (`portal/libs/shell`, `context-bar.ts`) shows `contoso` rather than
  "Contoso". `TenantDescriptor.DisplayName` holds the value; what is owed is the property on the
  snapshot and the scope body (`ScopeBodyProperties.DisplayName` is already the name the
  subscription body uses), the emitter's schema and the regenerated clients, and the bar reading it.
  #94's item 11, carried here so it is tracked somewhere; the same issue's items 9 and 10 — the
  sign-up long-running operation with its progress UI, and the welcome mail — are
  [§ Sign-up and tenant creation](#sign-up-and-tenant-creation)'s owed paragraph.

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

⚠ **The per-IP limit is the identity host's, not the gateway's, and it counts through the gateway's
counters.** [10 § Rate limiting](10-gateway-and-api.md)'s *per IP, unauthenticated* row names sign-in
and token, which live here; `IdentityRateLimits` (#94) carries two buckets over the sliding-window
counters that moved to `CyberCloud.ServiceDefaults.RateLimiting` so both hosts count the same way:
`/api/signup/begin`, ten per ten minutes per address, because each call issues a code and the grain
caps issues per *sign-up* rather than per caller; and the code-verify endpoints — `/api/signup/verify`,
`/api/signin/otp`, `/api/signin/totp` and `/api/signin/recovery-code` — sixty per minute per address,
because each call is a guess and the grain caps guesses per *code* (a recovery code is unguessable
in practice and is in the bucket anyway, so the rule stays "every route that takes a code").
Per IP and nothing finer, on purpose: a limit keyed by the address in the body would be a second
answer for an address somebody is hammering, which is the enumeration the next paragraph forbids.
The `429` depends on the connection's address alone and on nothing in the body, a made-up address
and a real one are refused alike, and the uniform answers below the limit are untouched. The
password endpoint carries no bucket — the lockout counter and the dummy hash are its. The accepted
risk beside this: an unknown `tenant` hint on `/api/signin/*` is answered before the 250 ms floor
(`SignInApi` argues it — slugs are public names, the lookup is one platform-grain call), which #94
recorded as a decision rather than an oversight.

⚠ **"Per address" is only true once the deployment has named its ingress.** Behind the Envoy
[10 § Shape](10-gateway-and-api.md#shape) puts in front of every host, the connection's address is
the ingress's for everybody, and both buckets become platform-wide caps — ten requests from one
hostile caller would close sign-up for everyone. `CyberCloud:Identity:TrustedProxies` (addresses or
CIDR blocks; `IdentityHostOptions` argues it) is the list of proxies whose `X-Forwarded-For` the host
believes; set, `IdentityComposition.MapIdentityHost` runs the forwarded-headers middleware first and
the address every bucket — and the hashed `SignInContext.ClientAddress` — sees is the one Envoy
appended. Unset, the header is a caller's claim and is ignored, which is right on the development run
and wrong on every deployment. The middleware is conditional rather than always on because the
options `AddServiceDefaults` leaves behind have both known lists cleared, and cleared lists believe
the header from anywhere — a rate-limit key the caller picks (`TrustedProxies` carries that). The
gateway's own per-IP row has the same shape and no knob yet. ⚠ And the counters count per replica
today: the Redis pair is registered when the container holds an `IConnectionMultiplexer`, and no host
composition in this repository registers one — N replicas are N× each budget, the same gap
`ILockoutCounter`'s registration names.

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
operator when `CyberCloud:Identity:SelfServeSignUp` is on, and a Development-only
`DevelopmentOtpDelivery` writes the enrolment code to the silo's log, where the Aspire dashboard
shows it — and, since #93 gave the AppHost a relay (Mailpit), mails it through the platform's own
communication service as well, so it is also in the inbox at `http://localhost:8025`
([17 § The outbound carrier](17-communication-and-email.md)). ⚠ That cross-tenant send — the user's
`UserGrain` into the platform tenant's message grain — is the one edge
`CyberCloudGrainCallTenantSeparator` opens through the separation this paragraph describes, and the
route to the platform's service was unreachable on a real silo until it did. ⚠ **Every code `begin`
issues draws on one platform-wide daily cap** (`PlatformCommunicationServiceOptions.MaxEmailsPerDay`,
shared with every tenant's sign-in codes), and `begin` is unauthenticated — so the "global per-IP
limit" [§ Credentials](#credentials) names is applied there first: `SignUpApi.BeginAsync` runs the
lockout ladder on the caller's address (`LockoutKey.ForCaller`) before any grain, five begins free
per window and then doubling waits, with the same body either way. One machine cannot spend the
platform's day; many machines still can, and the cap is what stops that from becoming a relay bill.
The progress UI, the welcome mail and the optional cluster are the part
of [06 § Tenant lifecycle](06-tenancy-and-resource-model.md)'s operation still owed; the step record
in the grain is its seed.

**Invited.** An existing tenant owner invites an email into their tenant ~~with a role~~. The invitee
either signs in (if they already have a user in *another* tenant — see below) or signs up.

⚠ **What shipped with #43, and the two places it departs from that sentence.** An owner `POST`s
`{ "email" }` to `/tenants/{t}/providers/CyberCloud.Identity/invitations` at the gateway;
`InvitationService` in the resource manager checks `assignRole` on the tenant, fully consistent, and
`IInvitationGrain` (durable, `invite/{id:N}`) claims the address in the tenant's email index, creates
the user in `Invited`, keeps the SHA-256 of a 256-bit secret and mails a seven-day link —
`{identity app}/invitation?tenant=…&invitation=…&token=…` — through the platform's own communication
service (`CommunicationInvitationDelivery`, a template in code, and a refusing seam on a silo with no
route). The identity app's invitation page describes the link and accepts it: the person chooses a
name and a password, the link is spent (a second use is told it was used), the user becomes
`Active`, and they are signed in with a session stamped password + delivered code, because the link
went to the address and nowhere else. (1) **No role.** The invitation makes a member; what they may
do is a role assignment — the existing `PUT …/roleAssignments/{name}`, which `GrainPrincipalDirectory`
now answers for the invited user — so a role is granted, audited and revoked in one place.
(2) **"Signs in" is not what an existing person does.** Under the one-user-one-tenant rule below, a
colleague who already has an account elsewhere is still a new user *here*: the same page asks them
for a name and a password for this organisation and leaves the other account untouched. Re-inviting
an address whose user is still `Invited` reuses that user, which is how an expired link is replaced;
inviting a member is a conflict. ⚠ So two links can name one user, and a link outlives a suspension
or a deprovision of the person it names: a link opens an `Invited` user and nothing else. Once the
user is anything else the invitation reads `withdrawn`, and `IUserGrain.AcceptInvitationAsync`
checks the status in the same turn as it sets the name, the password and `Active`. The first cut did
three separate writes with no check, and the review proved a second link un-suspending a member and
a link resurrecting a deprovisioned invitee, signed in as password + code past any second factor
enrolled since. Since #41 an owner withdraws one directly: `DELETE …/invitations/{id}` makes it
`revoked` (stored, where `withdrawn` is read from the user) and leaves the invited user `Invited`, so
a new invitation reuses them; `POST …/invitations/{id}/resend` replaces the link's secret, restarts
the seven days and mails again under its own idempotency key (`invite-{t}-{i}-{n}` from the second
mail — the first key alone made the communication service answer a resend with the first mail's
receipt and send nothing). ⚠ (2) stands as a departure rather than a gap: signing in as the account
elsewhere would need that account found by address across tenants (the global index this section
rules out) or a credential shared between two users (`IUserGrain` hands out no hash, by design), and
cross-tenant identity is the M3 question below. `InvitationThroughTheGatewayTests` (the `POST` through
the real gateway with a device-flow token), `InvitationsOverHttpTests` (Mailpit) and
`CyberCloud.Isolation § InvitationTests` pin it. ⚠ **Owed:** a registered Communication template in place of the
code template ([17 § The outbound carrier](17-communication-and-email.md)); a passkey at acceptance
(the page takes a password, the sign-up page's passkey ceremony is not reused yet); an idempotency
key the sender supplies, so a retried `POST` resumes one invitation rather than making a second; and
the welcome mail, which is still [§ the owed paragraph above](#sign-up-and-tenant-creation)'s.
~~Listing and revoking pending invitations~~ and ~~the portal page that sends one~~ landed with #41's
identity administration pages — [20 § The pages that are not generated](20-portal.md) and the
administration API below.

⚠ **The identity administration API (#41), and why it is the gateway's.** Everything under
`/tenants/{t}/providers/CyberCloud.Identity/` — `invitations` (list, send, `{id}` revoke,
`{id}/resend`), `members` (list, `{id}` remove), `applications` (list, register, `{id}` read and
delete, `{id}/rotateSecret`) and `sessions` (the caller's own: list, `{id}` sign out) — is a bearer
API on the gateway, not a cookie API here: [§ Hosts](#hosts) keeps cookies on this host and the
portal holds a token for the gateway. The gateway only routes (`IdentityAddress`, `IdentityDispatch`);
`IdentityAdministrationService` in the resource manager checks `assignRole` on the tenant, fully
consistent, for every directory call — the owner's permission, the one inviting already needs — and
the gateway's `GrainIdentityDirectory` does the work over the grains, the invitation's arrangement.
The sessions need no role: the address names no user, the list is read off the caller's own user,
and a session that isn't theirs is one `404` whether it exists or not. Removing a member deprovisions
the user (every session revoked, every credential cleared) *then* deletes every tuple naming them
through the reverse index, so a removed member holds no role anywhere and is out of every group;
nobody removes themselves. `IdentityRoutingTests`, `CyberCloud.Isolation § IdentityAdministrationTests`
and `IdentityAdministrationThroughTheGatewayTests` pin it. ⚠ **Owed:** an update of a registration
(redirect URIs and scopes are fixed at registration today — delete and re-register); a second live
secret for a rotation window, Entra's answer to rotating without downtime; a last-person rule (only
self-removal is refused, so a service principal holding `owner` can remove the one person who
does);
re-inviting an address whose user was removed (`Deprovisioned` keeps the email-index claim, so it is
a `409`); paging the three lists past one directory index read whole; and the address in the
generated OpenAPI document — the reserved namespace keeps it out of the registry the emitters read,
[10 § Shape](10-gateway-and-api.md)'s question again.

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

⚠ **A person sees and ends their own sessions since #41** — `GET` and `DELETE`
`/tenants/{t}/providers/CyberCloud.Identity/sessions[/{id}]` at the gateway, and the portal's *My
sessions* page. The list is the user grain's tracked ids, each read from its session grain and kept
only while live, and the one the request's own token belongs to is marked from its `sid` — which the
gateway now reads off the token (`TokenClaims.SessionId`) to mark that row and for nothing else. Two
honest limits: ending the browser's cookie session ends the token sessions it opened at their next
refresh, not at once, so they stay listed until then; and "sign out everywhere" as one button is
owed — each session is ended one row at a time.

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
