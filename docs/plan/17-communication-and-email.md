# 17 — Communication and Email

Two products that get conflated and should not be:

| | `CyberCloud.Communication` | `CyberCloud.Mail` |
|---|---|---|
| What | **Sending** — SMS, WhatsApp, transactional email, chat, push | **Hosting** — real mailboxes with IMAP, a domain's MX, a webmail UI |
| Azure analogue | Azure Communication Services | *none* — Azure does not offer this |
| Shape | An API a tenant calls | A server we run per tenant |
| Hard part | Carrier relationships and compliance | Deliverability and IP reputation |

The platform itself is `CyberCloud.Communication`'s first customer — every OTP, alert, invitation and
invoice goes through it — which is the right forcing function.

## `CyberCloud.Communication/services` · M2 · 2.0 EM

### The channel abstraction

```csharp
public interface IChannelProvider
{
    ChannelKind Kind { get; }                       // Sms | WhatsApp | Email | Push | Voice
    Task<Result<DispatchReceipt>> SendAsync(Message message, CancellationToken ct);
    Task<Result<DeliveryStatus>> GetStatusAsync(string providerMessageId);
    ValueTask<Result> HandleWebhookAsync(HttpRequest request);   // delivery receipts, inbound
}
```

Implementations: Twilio, Vonage, Meta Cloud API (WhatsApp), Amazon SES/our own Postfix (email), APNs
and FCM (push). A tenant's service resource selects a channel and either uses the platform's account
(marked-up, no setup) or their own credentials (BYO, cheaper) — and **BYO is offered from day one**,
because a tenant with an existing Twilio contract will not move it and refusing them is refusing the
customer.

⚠ **We are a broker, not a carrier, and the product must say so.** Sender-id registration, 10DLC
campaign approval in the US, WhatsApp template pre-approval, and per-country content rules are the
tenant's compliance obligations with our tooling, not obligations we assume. Getting this wrong is a
regulatory problem, not a bug.

### The parts that are actually the work

The `SendAsync` call is a day. These are the rest:

| Piece | Why it is not optional |
|---|---|
| **Idempotency** | Every send carries a client-supplied key; a retry after a timeout must not send twice. An OTP sent twice is confusing; an invoice notice sent twice is a support call |
| **Delivery receipts** | Webhooks in, correlated to the message grain, surfaced as status. Without them "did it arrive" is unanswerable |
| **Suppression list** | Bounces, complaints, opt-outs — per tenant, honoured before dispatch. Ignoring a complaint is how a sending domain gets blocked |
| **Templates** | Named, versioned, localised, with typed parameters. Because WhatsApp *requires* pre-approved templates, and because the alternative is string concatenation in twenty providers |
| **Rate and spend limits** | Per tenant, per channel, per day. An SMS loop is a five-figure incident within an hour, and the limit is the only thing between a bug and that invoice |
| **Inbound** | Replies and `STOP` keywords routed to a webhook or a queue. `STOP` handling is legally required in most jurisdictions |

**Message grains** (hot tier, TTL'd) hold state per message: queued → dispatched → delivered/failed,
with the provider id and receipts. That gives per-message status, retry with backoff, and idempotency
in one place.

### Resource model — landed 2026-09-15 (#33)

The module above had been wired into the silo host and carrying the platform's OTPs since before any
provider existed; this is the tenant-facing surface over it. Four types, one api-version, and the grains
are the ones the platform already runs:

```
CyberCloud.Communication/services/{name}
  ├─ defaultLocale                 → the locale a send that names none is rendered in
  ├─ channels/{name}               → kind (sms|whatsapp|email|push|voice), provider, enabled,
  │                                  account (platform|tenant) with accountRef/authRef/signingRef
  │                                  as SecretRef handles, limits per UTC day, estimatedUnitCost
  ├─ templates/{name}              → channel, locale, subject, body, variables, optionalVariables;
  │                                  action: render
  ├─ suppressions/{name}           → channel, destination, note — a manual block
  └─ actions: send, status, checkSuppression, listSuppressions
```

⚠ **The grains are keyed by the resource's *address*, not the resource manager's GUID** —
`CommunicationGrainKeys.ResourceIdFor`. A child's reconcile pass knows its parent by name and nothing
hands it the parent's GUID, so `services/{name}` and everything under it derive the service grain's id
from the service's canonical path. The consequence worth wanting: a service deleted and recreated under
the same name lands on the **same suppression list**. The consequence an operator has to know: the
platform's own service — `SiloIdentityOptions.ServiceId`, the one every OTP goes through — is that
derived id, not a GUID read off a listing.

⚠ **Three decisions the schema model forced, each recorded rather than hidden.** This platform's
schema has no array of objects (the remarks on `SchemaKind.Array` say why), which is what separates the
table above from the interface at the top of this section:

- **Channels, templates and suppressions are child types, not arrays on the service.** Each is a thing
  a tenant adds and removes on its own, so this is the better shape anyway; the cost is that a channel
  kind is a body property and one service holds one configuration per kind, owned by one resource, and
  a second resource naming the same kind is refused by name.
- **A template is one locale.** "Localised" here means one `templates` resource per language, chosen by
  name; the grain's per-locale fallback still runs over the one body each resource gives it. Multi-locale
  templates are owed to the api-version that grows the tree.
- **There is no `messages` type; a send is an action and its receipts come back on `status`.** A
  message is an event, not desired state: a `messages` resource would have a PUT whose second body the
  grain refuses by design (`Conflict` — one key, one message) and a DELETE that cannot un-send. The
  idempotency the resource shape would have offered is already the grain's. `status`'s receipts are
  one text line each, for the same schema reason.

⚠ **Suppression is enforced in the send path for the tenant's sends and the platform's alike, and one
test suite pins both.** An OTP is `IOtpDeliverySeam` → `CommunicationOtpDelivery` → `IMessageSender` →
`MessageGrain.DispatchAsync`, whose suppression check runs before a carrier is even resolved; a
tenant's `send` action is the same `IMessageSender`. The platform's own service is a `services`
resource like any tenant's, so its list is the same grain the `suppressions` type writes to. What the
resource surface adds is the rule that a resource **owns a manual block and never downgrades a
complaint**: reconciling a `suppressions` resource for an address the carrier said complained leaves
the complaint in place, and deleting the resource leaves it in place too — the two ordinary operations
that would otherwise have un-unsubscribed a recipient. `SuppressionEnforcementTests` in
`CyberCloud.Providers.Communication.Tests` was sabotage-tested on both.

⚠ **What ✅ on the roadmap row does not mean, said here as well as there.** One carrier client ships
— email, [§ The outbound carrier](#the-outbound-carrier--the-client-landed-2026-09-18-93-stays-open) below — and the other
four channels resolve to the module's refusing seam unless a host registers a real `IChannelProvider`,
so an SMS `send` today refuses honestly rather than sending. The sender-id registration flow has its
grain (`ISenderIdentityGrain`) and no resource surface, so `ChannelConfiguration.SenderId` is always
empty from this surface. Inbound `STOP` suppresses and is forwarded nowhere. **Delivery receipts have
their read half only**: `status` renders what `IWebhookRouter` recorded, and no host maps a path a
carrier's callback could reach — `HandleWebhookAsync(HttpRequest)` at the top of this document is the
provider's half, and the ingress in front of it lands with the first deployed relay, because the
carrier's signature is the only authentication a callback has (`charts/bundle/bundle.yaml § owed`,
`communication-receipts-have-no-ingress`, which since #93 carries the email-specific design: a
Postfix relay's bounce is an RFC 3464 DSN *mailed* to the envelope sender, SES's is an SNS
notification, and neither is a webhook). And the platform's own outbound MTA — "our own Postfix"
above, the warmed pool with its PTR records and feedback loops — is still not deployed, which is what
the same section's `the-platform-has-no-mta` now says.

### The outbound carrier — the client landed 2026-09-18, #93 stays open

⚠ **What landed is the client, and #93 asked for the carrier.** The issue names the platform's
outbound MTA — a warmed pool with PTR records, feedback loops, RBL monitoring and separate sending
IPs — the SMS, WhatsApp and voice carrier accounts, and the receipt ingress. None of those is in this
section or in this repository: what follows is the SMTP submission client, a development relay for
the AppHost, and the platform's own service reachable through it. The issue stays open for the rest,
and the **What remains** paragraph at the foot of this section is the list.

`SmtpChannelProvider` in `CyberCloud.Communication/Providers/Smtp` is the email `IChannelProvider`:
one SMTP submission per message to whatever relay `CyberCloud:Communication:Smtp` names — a host, a
port, `StartTls` | `ImplicitTls` | `None`, an optional `AUTH PLAIN`/`LOGIN` credential, and the
`From` address. That is the shape Amazon SES's SMTP endpoint (587, `STARTTLS`, a credential pair)
and a Postfix relay in the same cluster (25, no auth on a private network) both speak, so the
"Amazon SES/our own Postfix" of [§ The channel abstraction](#the-channel-abstraction) is one client.
⚠ **Hand-written, not MailKit**, for the reason `CyberCloud.ObjectStorage` hand-wrote SigV4:
[02 § Dependency register](02-technology-decisions.md) admits nothing without an ADR, and RFC 5321
with `STARTTLS`, `AUTH` and dot-stuffing fits in one file (`SmtpConnection`). It is proven against a
real server — Mailpit in a Testcontainer, `SmtpChannelProviderTests`, reading the headers back
through the server's API — and its refusals against a scripted one (`SmtpRefusalTests`: a relay
without `STARTTLS` when the section insists, an untrusted certificate, a credential the client will
not send in the clear, a `550`, a relay that never answers). ⚠ The happy halves of `STARTTLS` and
`AUTH` were read against RFC 3207 and RFC 4954 and not run to their end, because both need a
certificate the client trusts. The first relay with TLS is the first run of those two paths.

**What is applied on the way out**, which is the half of [§ Deliverability](#deliverability--the-part-that-decides-whether-this-works)
that belongs to the *message* rather than to the sending IP:

| Rule | How, and why it is not optional |
|---|---|
| A per-message `Message-ID` | Minted under the `From` domain from the message grain's id, and returned as the provider message id. It is the handle a bounce or a feedback-loop report quotes back, so it is the key the receipt path will correlate on. ⚠ SES rewrites it to its own id; the owed ingestion has to map |
| `List-Unsubscribe` | A `mailto:` at the configured `UnsubscribeMailbox`, on every message. Gmail and Yahoo require it of bulk senders since 2024 and score its absence on everything else; a recipient who uses it is a recipient who did not cost the domain a complaint. ⚠ `mailto:` and not RFC 8058's one-click `https:`, because one-click needs an ingress that accepts the `POST`, and there is none yet |
| `Auto-Submitted: auto-generated` | RFC 3834. An OTP is not written by a person, and saying so stops every vacation responder from answering the platform's `From` |
| `Date`, `MIME-Version`, `Content-Type: text/plain; charset=utf-8`, quoted-printable | The basics whose absence is a spam signal, and an explicit charset so a Czech template is not mojibake |
| **The suppression check** | Not in the carrier — `MessageGrain.DispatchAsync` runs it before a provider is resolved, exactly as before — and asserted against the real server: a suppressed address produces no connection to the relay at all |
| Header and body injection | A subject with a line break is RFC 2047-encoded as one value, the body is quoted-printable, and the transport dot-stuffs, so a template author can add neither a header nor an SMTP command |

**The development carrier.** The AppHost runs Mailpit (`axllent/mailpit`, SMTP on 1025, inbox on
`http://localhost:8025`) and points both silos at it through `CyberCloud:Communication:Smtp` with
`Security=None` and no credential — the one arrangement `SmtpRelayOptions.Validate` accepts without
TLS, because there is no password to put on the wire. A tenant's `channels` resource with
`kind: email` sends through it, and so does the platform: `PlatformBootstrapTask` writes **the
platform's own `services` grain** — addressed as `services/platform` in the platform tenant's
`platform` resource group, keyed by the id that address derives, so a resource created there later
adopts it — with an email channel on the `smtp` carrier, and in Development `DevelopmentOtpDelivery`
mails every sign-up code through it *beside* the console line, which stays. `PersonOverHttpTests`
reads the code off the console and asserts the same code is in the inbox.

⚠ **The cross-tenant edge this needed, and the defect it found.** A code is minted by `UserGrain`
in the user's tenant and sent through the platform tenant's message grain — a grain-to-grain
cross-tenant call, which `PlatformCrossTenantAuthorizer` refuses. Every test of the route to the
platform's service had run without the separation wired; on a real silo the first OTP would have
died with `Tenant "X" attempted to access tenant "0000…"`. `CyberCloudGrainCallTenantSeparator` now
opens exactly one edge: a call **into the platform tenant's `IMessageGrain`** is not tenant-separated.
Nothing else in the platform tenant is reachable that way — its tuple store above all — and
[11 § Sign-up](11-identity.md) and the separator's own remarks carry what the edge costs.

⚠ **What remains — the part of #93 that is still open — each with its row in
`charts/bundle/bundle.yaml § owed`.** The relay itself
(`the-platform-has-no-mta`): a warmed outbound pool with PTR records, feedback-loop registrations,
RBL monitoring and the separate sending IPs [25](25-risks-and-open-questions.md)'s closed row 2
requires; on a cluster it is a Postfix relay or an SES SMTP credential, and the configuration section
is ready for either. Bounce and complaint ingestion (`communication-receipts-have-no-ingress`): the
design is written there — a DSN parser for a Postfix relay, an SNS-signature-verified endpoint for
SES, both landing on `DeliveryReceipt.Suppresses`. A tenant's own SMTP account
(`CredentialMode.TenantAccount`) is refused by the carrier rather than sent through the platform's
relay under the platform's name, because a BYO relay has a *host* per tenant and
`ChannelConfiguration` cannot carry one yet. And `SMTPUTF8` addresses, which the client refuses by
name.

⚠ **A manual block has one owner, and it was the review of #33 that found the hole.** The first cut
let any number of `suppressions` resources name one address: the second read the first's entry as its
own and reported converged, and deleting either released the block while the other still declared it —
an address sendable with a resource saying it is not, and nothing to re-converge it, because
[08 § The reconcile loop](08-resource-manager.md)'s drift scan is per cluster and this family has none.
`SuppressionEntry.OwnerResourceId` is the fix and it is the same one `channels` already had for a
kind: a second resource for an address another resource holds as a manual block is refused by name
with `Conflict`, and a delete releases only its own. An entry from before the field existed reads back
unowned and is adopted by the first pass that names it.

### Chat — M3

The Azure Communication Services chat surface: threads, participants, read receipts, typing. Over
SignalR and grains, which is the shape we already have. Scoped small and deliberately: **it is a chat
API, not a chat product.** No moderation, no search, no compliance export in M3.

## `CyberCloud.Mail` — the managed mail server · M2 · 3.5 EM

From the brief. Azure has no equivalent, and the reason is instructive: **email hosting is mostly a
reputation and abuse-management problem**, and hyperscalers avoid it. Doing it anyway is a real
differentiator and it needs to be entered with eyes open.

### Components

| Piece | Choice |
|---|---|
| MTA | **Postfix** |
| IMAP/POP + delivery | ⚠ **Dovecot, not Cyrus** — see below |
| Filtering | Rspamd (spam, DKIM signing/verification, DMARC, rate limits, greylisting) |
| Antivirus | ClamAV via Rspamd, plus [18](18-security-vault-and-malware-scan.md)'s scanner for attachments |
| Sieve | Dovecot's Pigeonhole — server-side rules |
| Webmail | **Ours**, Angular + xUI, against a JMAP-shaped API over the Dovecot backend |

> ⚠ **The brief says Cyrus; this recommends Dovecot.** Cyrus IMAP is solid and its Murder aggregation
> is genuinely good at very large scale. Dovecot is chosen because: it is what the ecosystem
> standardises on (so operators and answers exist), its Postfix integration via LMTP + SASL is the
> best-documented path in existence, Pigeonhole is the reference Sieve implementation, its
> `mdbox`/`sdbox` formats are far better than maildir for the object-storage-backed setup we want, and
> replication (`dsync`) is simpler to operate than Murder. This is a recommendation, not a
> countermand — if there is a reason for Cyrus that is not visible here, the rest of the design is
> unchanged, because the seam is LMTP and IMAP either way.

### Topology — the brief's question, answered

*"Standalone instance per tenant? It would need separate IP due to ports."*

**Per-tenant instance, yes. Separate IP, only for outbound, and only above a threshold.**

| Concern | Decision |
|---|---|
| **Inbound (25)** | ⚠ **A shared inbound MTA pool is fine and is the right answer.** MX records point at our pool; the pool routes by recipient domain to the tenant's Dovecot over LMTP. Port 25 does not need one IP per tenant — the recipient domain disambiguates |
| **Submission (587/465)** | Shared. Authenticated, so the tenant is known from the credential |
| **IMAP (993)** | Shared, with SNI per tenant domain. TLS certificates from cert-manager per verified domain |
| **Outbound (25 → the world)** | **This is where dedicated IPs matter, and only here.** Reputation is per sending IP. Small tenants share a well-warmed pool; tenants above a volume threshold, or who ask, get a dedicated IP with a warm-up schedule |
| **Storage** | Per-tenant Dovecot instance with its own volume — the isolation boundary that matters for data, and the one that lets a tenant be moved or restored independently |

So: **shared front doors, per-tenant back ends, dedicated outbound IPs by plan.** The
"separate IP due to ports" instinct is correct for outbound and unnecessary for the rest, and getting
that right is the difference between one IPv4 address per tenant (which does not scale — v4 is scarce
and metered) and a handful per region.

### Deliverability — the part that decides whether this works

Software is maybe 30 % of this product. The rest:

| Requirement | How |
|---|---|
| SPF, DKIM, DMARC | Generated per domain; **the platform will not enable sending until the DNS records verify.** If the tenant's zone is ours ([14](14-networking.md)) it is one click |
| Reverse DNS / PTR | Per outbound IP, matching the HELO name. Requires the address block to be ours |
| Feedback loops | Registered with the major providers per IP |
| Warm-up | Automatic volume ramp on a new IP over ~4 weeks. Skipping it gets the IP blocked in a day |
| Abuse handling | ⚠ **`abuse@` must be monitored by a human with the authority to suspend a tenant within the hour.** This is an operational commitment, not a feature. A platform that does not do this loses its address blocks |
| Blocklist monitoring | Automated checks against the major RBLs per outbound IP, alerting to on-call |

⚠ **The decision to make before starting**, and it is a business decision: are we prepared to run an
abuse desk? If not, this module should be cut or fronted by a wholesale relay (which loses the "own
mail server" proposition but keeps the mailbox hosting). Building it and then not staffing the abuse
desk is the one path that ends with our IP ranges blocked and the *rest of the platform's* transactional
email failing.

### Resource model

⚠ **CORRECTED 2026-09-09 — `{domain}` cannot be the address segment, and the block below said it
was.** `ResourceNaming` applies the Kubernetes **DNS-1123 label** rule to every resource name on this
platform — `[a-z0-9]([-a-z0-9]*[a-z0-9])?`, 1–63 characters, **no dots** — because a name becomes a
Kubernetes object name *and* a label value. Every mail domain contains a dot, so
`domains/example.com` is refused by `ResourceId`'s own constructor before any provider code runs. It
was found by a test failing on that constructor, not by re-reading this document.

⚠ **Relaxing the rule would be the wrong fix even though it is where the eye goes.** An object *name*
may contain dots; the DNS-1123 *label* that a `StatefulSet`'s pod names and its `Service`'s DNS
records are built from may not — so a type that allowed them would render objects the API server
accepts and pods it will never schedule. The domain is therefore an **immutable property** beside an
ordinary name, and the shape is `domains/example-com` with `properties.domain = "example.com"`.

```
CyberCloud.Mail/domains/{name}          ⚠ an ordinary DNS-1123 name, NOT the domain
  ├─ domain: the mail domain itself, required and immutable
  ├─ verification: SPF/DKIM/DMARC/MX status, with the exact records to add
  ├─ mailboxes/{local}     → quota, aliases, forwarding, password (Vault), Sieve rules
  ├─ groups/{name}         → distribution lists
  ├─ catchAll, relayHosts, dedicatedIp
  └─ actions: verify, sendTest, exportMailbox
```

⚠ **As built (#34, 2026-09-23), the block above reads:** `mailboxes/{name}` with `localPart` as a
required immutable property — the name rule that moved `domain` off the address moves the local
part too, since `john.doe` is not a DNS-1123 label — carrying `quota`, `aliases`, `forwardTo`,
`keepCopy` and `passwordRef`, a vault handle and never a value. Sieve rules are edited over
ManageSieve and are not a property. `verification` is two actions rather than a stored field —
`dnsRecords` (read: the seven records and a zone file) and `verify` (write: resolve them and move the
sending gate) — because verification is an observation of a DNS the platform does not own, not state.
`groups`, `sendTest` and `exportMailbox` are not built. [§ Mailboxes and the sending gate](#mailboxes-and-the-sending-gate--landed-2026-09-23-34)
below has the rest.

⚠ **CORRECTED 2026-09-23 — this paragraph said `verify` could not be declared, and the reason has
gone.** It argued that verifying needs the public DNS and that the repository had no DNS resolution
seam, so the gate below — "the platform will not enable sending until the DNS records verify" — could
not be built. `IMailDnsResolver` is that seam: a hand-written RFC 1035 stub resolver in the provider,
proven against CoreDNS serving the zone file the platform hands the tenant. `verify` and `dnsRecords`
are declared with handlers and the gate is built — [§ Mailboxes and the sending gate](#mailboxes-and-the-sending-gate--landed-2026-09-23-34).
`CyberCloud.Network/dnsZones` was never going to be the seam: it would *host* zones, not ask the
internet about them.

### Mailboxes and the sending gate — landed 2026-09-23 (#34)

**What a mailbox is.** Dovecot opens one password file per login and Postfix builds one map of every
address, and both run in the *domain's* pod — so a mailbox owns no object. It is four keys of the
domain's `{name}-mail-users` Secret, written as a second writer through `ReconcileContext.CoWriter`
([09 § A second writer on an object](09-kubernetes-fabric.md)), the shape `virtualNetworks/peerings`
established: `{local}.passwd` (the Dovecot `passwd-file` line), `{local}.virtual` (its alias-map lines)
and a `{address}.claim` holding the mailbox's GUID for its own address and each alias. Two mailboxes
claiming one address write one key with two values, which the co-writer's merge refuses atomically —
no check-then-write race. The kubelet refreshes the mounted files and a loop in the Postfix container
rebuilds its maps on a checksum change, so no pod restarts for a mailbox.

**The password is a vault handle.** `passwordRef` names a field in the tenant's own vault (a path
outside `tenants/{tenantId}/` is refused before the vault is asked, the rule
`VirtualMachines.ParseCloudInitRef` set); the reconciler resolves it for one pass and writes a
`SHA512-CRYPT` hash at 100,000 rounds. ⚠ The salt is derived from the mailbox's GUID, not random —
a hash rendered every pass must be the same every pass or the co-owned apply never settles. ⚠ A
mailbox with no handle is `{CRYPT}!` and `nologin=y`: Dovecot reads an empty password field as "any
password". App passwords (a second credential per client) are not built.

**The seven records and the gate.** `dnsRecords` returns MX, SPF, DKIM, DMARC, the MTA-STS TXT and
policy-host CNAME, and TLS-RPT, each typed and as a zone file split into 255-byte strings; the platform's
hosts they name are configuration (`CyberCloud:Mail`), and a region that has not configured them is
refused rather than handed invented names. SPF, DKIM and DMARC **gate**; the MX and the TLS records do
not, because a domain can send before it receives. `verify` resolves all seven and decides the gate;
every reconcile pass decides it too, through the same function. A closed gate is **refused at `RCPT
TO`** for any recipient outside the domain — "held until its SPF, DKIM and DMARC records verify" — and
defers the smtp transport so a mailbox's `forwardTo` cannot leave either. ⚠ A tenant in
`CyberCloud:Mail:SuspendedTenants` is closed whatever its records say: the abuse desk's lever. It is
deliberately **not** [06](06-tenancy-and-resource-model.md)'s `Suspended` tenant status, which an
overdue invoice sets and whose data plane keeps running.

**Proven how.** On a real k3s through the real write path (`MailDeliveryOnK3sTests`): the pod starts
from the real images, a mailbox signs in over IMAP, a message submitted with `AUTH` is fetched by
another carrying a `DKIM-Signature` that a verifier knowing only the record resolved from CoreDNS
accepts, a relay is refused until `verify` sees the published records and accepted after, and mail to
an alias reaches its mailbox. Each rendered configuration is run by its daemon without a cluster too
(`MailDataPlaneTests`, both Dovecot majors and Rspamd).

⚠ **Four defects in the first cut, found only by running it.** The milter was Rspamd's normal worker
(11333, HTTP) rather than its proxy (11332), which with `milter_default_action = tempfail` defers every
message; the catch-all was `luser_relay`, which a virtual domain never consults; the antivirus module
pointed at a ClamAV socket nothing served; and the DKIM key was mounted `0400` into all three
containers, unreadable by Rspamd's user. And one trap the second cut walked into: a key path that is
valid base64 — `/etc/mail/secrets/dkimPrivateKey` is — is taken by Rspamd for an *inline key*, and it
signs every message `ed25519` under the path's own bytes while reporting `DKIM_SIGNED`. The key is now
`/etc/mail/dkim/cc.key`, and the dot is what keeps it a path.

⚠ **What is still owed**, each with its row in `charts/managed/mail/conformance.yaml § owed` or
`charts/managed/mail-mailbox/conformance.yaml § owed`: the Postfix image is built from
`deploy/images/mail-postfix` and published by nothing; the gate is decided on a pass or a `verify` and
nothing re-decides it for a converged domain, so records removed later keep sending until something
re-verifies; a suspension needs a trigger to reach a converged domain; the shared front doors (so
submission and IMAP are plaintext on a `ClusterIP` until then), the outbound pool and warm-up, the
MTA-STS policy host, SRS for forwards, DKIM rotation (designed as a `dkimKeys` child type), groups,
and roughly four hundred mailboxes per domain before the Secret's annotation budget refuses the next.

⚠ **CORRECTED 2026-09-24 by the #34 review — four things the landing above got wrong.**

- **An open gate relayed for a mailbox as anyone.** `permit_sasl_authenticated` says who signed in,
  not who the mail is from, and every tenant's SPF includes the same platform include — so one
  tenant's mailbox could send as another tenant's domain and pass SPF, and DMARC through SPF
  alignment. Submission now refuses any envelope sender the login does not own
  (`smtpd_sender_login_maps`, built from each mailbox's own claimed addresses, and
  `reject_sender_login_mismatch`), and Rspamd rejects an authenticated message whose `From:` is not
  that envelope sender. Both run on k3s against a real client.
- **Inbound mail never met the alias map.** [§ Topology](#topology--the-briefs-question-answered)
  above says the pool delivers "to the tenant's Dovecot over LMTP", and the back end exposed exactly
  that. Dovecot knows mailboxes and nothing else: aliases, `forwardTo` and the catch-all are
  Postfix's `virtual_alias_maps`, and spam filtering is Postfix's milter — so internet mail to an
  alias was refused and nothing inbound was scanned. **The inbound pool delivers over SMTP to the
  domain's own Postfix on 25**, which hands Dovecot LMTP inside the pod; the Service carries no LMTP.
  The Cyrus argument moves one hop in and still holds.
- **A vault path could climb out of its prefix.** `tenants/{mine}/../{theirs}/db` starts with the
  tenant's prefix, and the resolver's HTTP client collapses the dot segments before OpenBao sees the
  path. Every tenant-spelled path — the mailbox's and `Compute/virtualMachines`' cloud-init, where the
  check was copied from — is now confined by `SecretRef.IsConfinedTo`, and the resolver itself refuses
  a path with an empty, `.` or `..` segment.
- **The Postfix image was named under somebody else's namespace.** `docker.io/cybercloud` is a Docker
  Hub organisation an unrelated party registered in 2018; an unpublished tag there is a tag they could
  publish. It is `ghcr.io/rikarin/cybercloud/mail-postfix` now.

Also from the review: a mailbox's observed state no longer carries the Secret (every mailbox's hash
had been persisted in every mailbox's grain), and a DNS that did not answer no longer closes a gate a
previous answer opened. ⚠ **Still owed after it, and said plainly:** DKIM rotation (the selector is
the constant `cc`, and rotating needs desired state naming the active selector — a new property is a
new api-version, and the `dkimKeys/{selector}` child type is new public surface in five SDKs, which is
the issue's scope decision rather than a review fix); the suspension's *trigger* (nothing re-drives a
converged resource anywhere on the platform, and the lever that works within the hour without one is
the shared submission pool, which knows the tenant from the credential); and Rspamd's rate limits and
greylisting, both of which keep their counters in Redis this pod does not have.

Webmail is a portal app (Angular + xUI) against a JMAP-shaped API. ⚠ **Building a good webmail client
is 2 EM on its own** and is not in the 3.5 above — the M2 deliverable is IMAP/SMTP access with a
minimal web client (list, read, compose, search). A full client is M3 and it is honest to say so
rather than to discover it.

### Effort

| Piece | EM |
|---|---|
| Postfix/Dovecot/Rspamd charts, per-tenant instances, LMTP routing | 1.0 |
| Domain verification, DKIM key management, DNS integration | 0.5 |
| Mailbox/alias/group resource model + provisioning | 0.6 |
| Outbound pools, dedicated IPs, warm-up automation, RBL monitoring | 0.8 |
| Minimal webmail (list/read/compose/search) | 0.6 |
| **M2 total** | **3.5** |
| Full webmail (threads, filters UI, calendar/contacts) | +2.0 (M3) |
