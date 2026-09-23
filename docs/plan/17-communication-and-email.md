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

**The second thing the platform sends: invitations (#43).** `IInvitationGrain`, in the inviting
tenant, mails its link through the same platform service and across the same edge —
`CommunicationInvitationDelivery`, idempotent on the invitation id so a retry after a relay outage is
one mail. In Development the silo routes it through the platform's service when a relay is
configured and `CyberCloud:Identity:Invitations:PageBaseUri` names the identity app (the AppHost
sets both); anywhere else it takes the configured OTP route, and with neither it refuses and says
which setting is missing — there is no console fallback, because the link makes a member.
`Identity.Host.Tests § InvitationsOverHttpTests` sends it to Mailpit and follows the link back out.
⚠ **The message is a template in code, not a `templates` resource**, and that is owed: the
platform's service has no template registered and nothing bootstraps one, so the subject, the body
and the link's line are rendered by `CommunicationInvitationDelivery.Render`. Moving it to a
registered template is `PlatformBootstrapTask` writing one beside the service, and a localized body
is the same change.

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

⚠ **None of the three actions is declared, and `verify` is the one that cannot be.** It answers
whether a domain's records resolve, which needs to ask the public DNS — and **this repository has no
DNS resolution seam at all**. `actions-without-handlers.txt` is not the escape hatch: it permits a
handler-less action only on an *already published* api-version, and `CyberCloud.Mail/domains`'
`2026-08-01` is published by the same change that would declare one. ⚠ The half that *is* derivable
is derived and needs no action to reach: `MailDomains.TryRequiredRecords` is a pure function of the
domain and the resolved DKIM key, so "the exact records to add" above is answerable today. What is
owed is reading them back — and `CyberCloud.Network/dnsZones`, which would provide it, is itself
unbuilt. **The consequence is that "the platform will not enable sending until the DNS records
verify", below, is NOT built and cannot be until that seam exists.**

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
