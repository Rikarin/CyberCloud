# 22 — Metering, Billing and Quota

The subsystem that is invisible until it is wrong, and then is the only thing anyone talks about. Its
non-negotiable property: **a usage record, once emitted, is never lost and never double-counted.**

## The pipeline

```
provider / sampler
   └─ UsageEvent { tenant, subscription, resourceId, meter, quantity, window, idempotencyKey }
        └─ NATS  cc.{tenant}.usage.{meter}      (JetStream, durable, 7-day retention)
             └─ rollup worker  →  ClickHouse  usage_raw   (dedup by idempotencyKey)
                  └─ hourly aggregate         usage_hourly
                       └─ rating (meter × plan × price) → charges
                            └─ IBillingLedgerGrain (durable, per subscription, append-only)
                                 └─ invoice
```

**Deduplication is by idempotency key at the ClickHouse insert**, using a `ReplacingMergeTree` keyed on
it. The key is deterministic — `sha256(resourceId | meter | windowStart | windowEnd)` — so a redelivery
after a silo restart collapses. NATS is at-least-once ([04](04-orleans-topology.md)) and this is the
only correct answer to that.

**The ledger is durable-tier and append-only.** Corrections are new entries with a reason and a link to
the original, never edits. An adjustable ledger cannot be audited and cannot be defended in a dispute.

## Two kinds of meter, and the distinction that prevents most bugs

| Kind | Emitted by | Example | Failure if done the other way |
|---|---|---|---|
| **State-based** | A 5-minute sampler over the resource-graph projection | vCPU-hours, GB-months, IP-hours | Event-based would miss a resource that exists but never changes |
| **Event-based** | The provider, at the moment | Requests, egress GB, SMS sent, scans run | Sampling would miss everything between samples |

⚠ **State-based meters must be derived from the platform's own record of the resource, not from
Kubernetes metrics.** A stopped VM still has a disk; a `Deployment` scaled to zero still has a
`PersistentVolumeClaim`. Metrics know about running pods; the resource graph knows what exists. Getting
this backwards under-bills storage and over-bills nothing, which sounds safe until the margin
disappears.

The sampler is per-subscription, reminder-driven, and emits one event per (resource, meter, window)
with a deterministic key — so a sampler that runs twice produces one record.

## Rating

```
charge = quantity × unitPrice(meter, region, plan, tier) − discounts + commitments
```

- **Price lists are versioned resources** with an effective date. A price change never applies
  retroactively; the rating engine picks the list in effect at the usage window.
- **Tiered pricing** (first 100 TB at X, then Y) is per meter per month. It must be computed against
  the *monthly* aggregate, not per event — a common and expensive mistake.
- **Commitments and reservations** (M3) are prepaid quantities consumed before on-demand.
- **Free tier** per subscription per month, per meter, applied at rating so the portal can show
  "you have used 40 % of your free tier" rather than a surprise.

**Everything is a resource**: `CyberCloud.Billing/priceLists`, `/budgets`, `/invoices`,
`/paymentMethods`, `/commitments`. Same authorization, same audit, same CLI.

## Cost visibility

The single most effective thing for both customer satisfaction and support load:

| Feature | Behaviour |
|---|---|
| **Near-real-time cost** | Current-period estimate updated hourly, broken down by resource group, resource, service and tag |
| **Per-resource cost** | On every resource blade. "This database costs €4.10/day" answers the question at the point it is asked |
| **Budgets and alerts** | Threshold at 50/80/100/forecast, delivered via [17](17-communication-and-email.md) |
| **Forecast** | Linear on the trailing 7 days. ⚠ Deliberately simple and labelled an estimate — a clever forecast that is wrong is worse than a simple one that is honestly bounded |
| **Cost by tag** | Which is why tags are M1 in [06](06-tenancy-and-resource-model.md) |
| **Export** | Daily CSV/Parquet to the tenant's bucket. Big customers reconcile in their own systems and will not use our UI |

## Invoicing and payment

| Piece | Decision |
|---|---|
| Cycle | Monthly, closed on the 1st, with a 48-hour late-usage window before finalisation. ⚠ That window exists because a usage event *will* arrive late; closing instantly means correcting invoices instead |
| Payment | **Stripe** (or an equivalent PSP) — card, SEPA, invoice terms for enterprise. **We do not touch card data.** PCI scope is the PSP's; ours is a token |
| Tax | ⚠ VAT/GST is a **jurisdictional minefield**: EU OSS, reverse charge, US sales tax nexus. Use a tax service (Stripe Tax / Avalara). Do not implement tax logic. This is stated as an engineering decision because someone always proposes a `TaxCalculator` class |
| Dunning | Payment failed → retry schedule → `Warned` → `Suspended` → `Disabled` ([06](06-tenancy-and-resource-model.md)). Every step notified, with the timeline stated |
| Credits and refunds | Ledger entries with a reason, an approver, and an audit trail |
| Invoice PDF | Generated, stored in a platform bucket, downloadable. Legally must be immutable and retained per jurisdiction |

**Suspension does not stop the data plane** ([06](06-tenancy-and-resource-model.md)). Taking a
customer's production down over a failed card, without a human decision, is a way to lose them
permanently. Control-plane writes are blocked, the banner is loud, and `Disabled` — which does stop
things — requires a deliberate action or a much longer timer.

## Quota

Distinct from billing and enforced earlier ([06](06-tenancy-and-resource-model.md)): a reservation in
`IQuotaGrain` before the provider is called, released on failure.

| Property | Value |
|---|---|
| Scope | Per subscription, per region, per meter family |
| Defaults | By subscription tier (trial, pay-as-you-go, enterprise) |
| Increase | A request resource with an approval workflow. Auto-approved below a threshold for accounts in good standing — because a trial user hitting a quota wall on a Saturday is a lost customer |
| Enforcement | Reservation, not a counter — the lease expires if the operation dies |
| Error | `429` naming the meter, the request, the current usage and the limit. Never a bare "quota exceeded" |

**Quota is a safety mechanism, not a sales mechanism.** Its primary purpose is to bound the damage from
a runaway loop — the tenant's, or ours. Fraud prevention is a separate concern: new accounts get low
limits and a payment-verification step, which is the actual defence against crypto-mining signups.

## Abuse

Named here because it is a billing problem in practice:

| Signal | Response |
|---|---|
| Sudden 100× compute spike on a new account | Automatic hold, human review |
| Egress spike with no matching ingress | Review — the shape of a proxy or a warez host |
| Outbound scanning or spam from a tenant network | Automatic network isolation, then review |
| Payment failure after heavy usage | Immediate quota freeze on *new* resources; existing keep running |

The consistent rule: **automated systems restrict growth; only humans destroy things.**

## Effort

| Piece | M | EM |
|---|---|---|
| Usage events, samplers, dedup, ClickHouse rollups | M1 | 1.2 |
| Rating, price lists, tiers, free tier, ledger grain | M2 | 1.2 |
| Cost views: near-real-time, per-resource, by tag, export | M2 | 0.8 |
| Budgets, alerts, forecast | M2 | 0.4 |
| Invoicing, PSP integration, tax service, dunning | M2 | 1.2 |
| Quota grains, defaults, increase workflow | M1 | 0.6 |
| Commitments, reservations, enterprise agreements | M3 | 0.8 |
| **Total** | | **6.2** |

⚠ **M1 ships metering and quota but not invoicing.** M1 tenants are design partners on manual
contracts; the meters must be *correct from the first resource* because usage that was never recorded
cannot be recovered, but turning meters into money can wait a milestone. That ordering is deliberate
and it is the cheapest correct one.
