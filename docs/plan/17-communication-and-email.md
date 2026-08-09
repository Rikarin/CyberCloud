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

```
CyberCloud.Mail/domains/{domain}
  ├─ verification: SPF/DKIM/DMARC/MX status, with the exact records to add
  ├─ mailboxes/{local}     → quota, aliases, forwarding, password (Vault), Sieve rules
  ├─ groups/{name}         → distribution lists
  ├─ catchAll, relayHosts, dedicatedIp
  └─ actions: verify, sendTest, exportMailbox
```

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
