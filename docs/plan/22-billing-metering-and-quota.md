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

⚠ **CORRECTED 2026-09-23 (#38): below the usage ledger, the pipeline above is not what shipped.** Two
boxes are gone and one moved. There is **no `IBillingLedgerGrain` and no stored charge**: rating reads
`IUsageLedgerGrain` — the usage ledger, which *is* durable and append-only — and prices it on every
read, so a draft invoice, a cost row and a budget's figure are all the same ledger rated by the same
path (`CyberCloud.Billing.Pricing.UsagePricing`) and cannot disagree with it or with each other. What
is stored is what cannot be derived again: the finalized invoice and the credit note, in
`IBillingAccountGrain`, and the gap-free number sequence in `IInvoiceNumberingGrain`. The hourly
aggregate the rating reads is the usage ledger's own entry, not `usage_hourly` in ClickHouse, which is
still the analytical copy nothing in this repository writes (`IUsageSink`). ⚠ **And in production the
ledger is empty today**: the silo hosts metering since #38 (it never had — no host line existed), but
the sampler's only input, `IMeteredResourceSource`, is still the refusing default until the
resource-graph projection is its source — [§ What is owed](#what-is-owed), `the-sampler-has-no-source`.

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
  ⚠ **CORRECTED (#38): versioned, dated and never retroactive — and committed data, not a resource.**
  The price sheet is `src/CyberCloud.Billing/Pricing/price-sheet.json`, embedded in the assembly so a
  silo cannot run one commit's code against another's prices. A price is the platform's decision and no
  tenant PUTs one, so a resource type would be a read-only document with a write path to guard; the
  negotiated per-tenant price of an enterprise agreement (M3) is where a resource starts to earn its
  place. A version starts on the first instant of a month in UTC, and the parser refuses one that does
  not: tiers are monthly, and a mid-month price would put two prices under one ladder and one invoice
  line. Every meter is priced in every version, at zero when free. A version in force is never edited —
  `PriceSheetTests.NoVersionInForceHasBeenEdited` pins each by digest — and a price change is a new
  version. What the sheet prices by is **meter and tier**; `region` and `plan` in the formula above are
  not dimensions of it yet, and neither are discounts or commitments (M3).
- **Tiered pricing** (first 100 TB at X, then Y) is per meter per month. It must be computed against
  the *monthly* aggregate, not per event — a common and expensive mistake.
  ⚠ **As built (#38): per subscription, per meter, per calendar month, climbed hour by hour in time
  order.** Pricing the monthly aggregate alone cannot say what one resource cost, and a cost view and a
  budget need exactly that; climbing the ladder in window order charges each hour for the tier portions
  it occupies and sums to exactly what the aggregate would cost
  (`RatingTests.PricingEveryHourSumsToPricingTheMonthlyAggregate`). The consequence: the free tier goes
  to whoever used the meter first that month. The ladder is per *subscription* because the usage ledger
  is, and a billing account with several subscriptions has one ladder per subscription.
  ⚠ **So an hour's price says how much was used before it, and only a subscription reader sees that
  ladder.** Priced over the whole subscription, 80 GiB of a group's egress cost nothing when it was the
  month's first and 3.00 € when another group used 80 GiB earlier — a reader of one group dividing the
  amount by the quantity would learn the other group's usage. The cost query and a resource-group budget
  therefore price what their reader may see *on its own*, as if it were the subscription's only usage
  (`CostQueryResult.PricedAlone`, `CostVisibilityTests.AGroupReadersFiguresDoNotMoveWithAnotherGroupsUsage`,
  `BudgetTests.AResourceGroupBudgetIsPricedWithoutTheOtherGroupsTiers`). Untiered meters cost the same
  either way; for a tiered one the figure is the group's cost alone, not the invoice's share of it.
- **Commitments and reservations** (M3) are prepaid quantities consumed before on-demand.
- **Free tier** per subscription per month, per meter, applied at rating so the portal can show
  "you have used 40 % of your free tier" rather than a surprise.

**Everything is a resource**: `CyberCloud.Billing/priceLists`, `/budgets`, `/invoices`,
`/paymentMethods`, `/commitments`. Same authorization, same audit, same CLI.

⚠ **CORRECTED (#38): one of the five is a resource, and it is `CyberCloud.Billing/budgets`.** The price
list is committed data (above). An invoice is a document the platform issues to a tenant, not desired
state a tenant writes: a `PUT` on one would have nothing it could mean, and the grain that holds them —
`IBillingAccountGrain`, one per tenant — is reached by the billing surface rather than the resource
manager. Its invoices are read at `GET /tenants/{t}/providers/CyberCloud.CostManagement/invoices[/{number}]`
since #41, by whoever may read the tenant ([10](10-gateway-and-api.md)); the account's writes are still owed
(§ What is owed, `billing-http-surface`). A payment method is a token at the PSP, and commitments are M3.

### Rounding and currency

Written once, in `MoneyRounding`'s remarks, and pinned at the boundaries by `MoneyRoundingTests`:

1. **Rating never rounds.** An hour is priced at full `decimal` precision.
2. **An invoice line is rounded once**, to the currency's minor unit, after its hours are summed —
   744 hours of a public IP at 0.004 € is 2.98 €, where rounding each hour would bill nothing.
3. **The subtotal is the sum of the rounded lines**, so an invoice adds up as printed.
4. **Tax is computed once on the subtotal and rounded once**, never per line.
5. **A half goes away from zero** — 0.125 € is 0.13 €, and −0.125 € is −0.13 €, so a credit note for a
   whole invoice is exactly its negation. ⚠ Not banker's rounding, `Math.Round`'s default, which prints
   0.12 €.
6. **A cost view is not a document**: its rows are rounded for display and its total is the unrounded
   sum rounded once.

**Currency**: a closed list with each code's ISO 4217 exponent (`Currencies` — `JPY` has none). A billing
account invoices in one currency; a meter priced in another is refused at finalization by name. The
platform converts nothing, and a figure that summed two currencies would be a number in neither.

## Cost visibility

The single most effective thing for both customer satisfaction and support load:

| Feature | Behaviour |
|---|---|
| **Near-real-time cost** | Current-period estimate updated hourly, broken down by resource group, resource, service and tag. ⚠ **Landed (#38) except by tag**: `POST {subscription or group}/providers/CyberCloud.CostManagement/query` with a period and `groupBy` of `resource`, `resourceGroup`, `resourceType`, `meter` or `day`, rated on read from the usage ledger. Every row is filtered by ReBAC inside the cost grain — a reader of one group sees that group's cost, priced on its own (`pricedAlone`, [§ Rating](#rating)), and a `filtered` flag; a stranger gets the absent subscription's 404, decided before anything is priced, so a failure to price says nothing to them either (`CostVisibilityTests`). The namespace is reserved like the resource graph's, because on a group the address is also a collection path. Since #41 `granularity: daily` splits any grouping by day, which is the portal's cost chart ([20 § The pages that are not generated](20-portal.md)) |
| **Per-resource cost** | On every resource blade. "This database costs €4.10/day" answers the question at the point it is asked |
| **Budgets and alerts** | Threshold at 50/80/100/forecast, delivered via [17](17-communication-and-email.md). ⚠ **Landed (#38)** as `CyberCloud.Billing/budgets`: thresholds are two arrays of percentages, on the actual and on the forecast, each firing once per period through a `CyberCloud.Communication/services` resource in the budget's own resource group — another group's service would send, on its own spend limits, for someone who may not use it ([§ What is owed](#what-is-owed), `budget-service-grant`). An alert recorded and not yet sent when an evaluation ended is sent by the next one, under the same idempotency key (`BudgetTests.AnAlertRecordedBeforeACrashIsSentOnTheNextEvaluation`). A budget covers its resource group; `scope: subscription` covers the subscription once the budget itself has been granted `reader` there — `reader-resource-{budget GUID}`, the resource-as-principal grant #90 introduced — because anyone who can write in one group can create a budget, and a subscription's spend is not theirs to read by default. Its figures and the thresholds that fired this period are read by the budget's `showStatus` action (#41), from the last hourly evaluation — the action reads and never evaluates, because an evaluation sends the alerts. ⚠ `showStatus` needs `read` on the budget and `read` on what it covers — the group, or for `scope: subscription` the subscription — checked fully consistent at every call (`BudgetStatusHandler`): the figures, the fired thresholds and every alert's figure are that scope's spend. A reader of the budget's group, whom the cost query answers `filtered` on the subscription, gets a `403` that says why from a subscription budget; so does a reader granted on the budget resource alone, whom it answers `filtered` on the group, from any budget. A reader of the subscription reads every group's budgets through the group's parent. Granting the budget `reader` on the subscription lets it be evaluated and discloses nothing to its group's readers; a revoke of the caller hides the figures at once, not at the next hourly evaluation |
| **Forecast** | Linear on the trailing 7 days. ⚠ Deliberately simple and labelled an estimate — a clever forecast that is wrong is worse than a simple one that is honestly bounded. The portal's month-end figure (#41) is the budget grain's method over day rows: the month so far plus the trailing week's spend per hour times the hours left |
| **Cost by tag** | Which is why tags are M1 in [06](06-tenancy-and-resource-model.md) |
| **Export** | Daily CSV/Parquet to the tenant's bucket. Big customers reconcile in their own systems and will not use our UI |

## Invoicing and payment

| Piece | Decision |
|---|---|
| Cycle | Monthly, closed on the 1st, with a 48-hour late-usage window before finalisation. ⚠ That window exists because a usage event *will* arrive late; closing instantly means correcting invoices instead. ⚠ **Landed (#38) as the account's own reminder, and only for an account something has set up.** Attaching a subscription arms `close-months` on the billing account, hourly; each tick runs `IBillingAccountGrain.CloseMonthsAsync`, which finalizes, oldest first, every month since the first attach whose window has passed — so a month is finalized on the 3rd, and the first refusal (no profile, no issuer) stops the close and is logged. **Nothing in the platform yet configures an account or attaches a subscription**: sign-up and `PUT subscription` create a subscription and never reach billing, so in production today no account exists and no month closes — [§ What is owed](#what-is-owed), `billing-account-provisioning` |
| Payment | **Stripe** (or an equivalent PSP) — card, SEPA, invoice terms for enterprise. **We do not touch card data.** PCI scope is the PSP's; ours is a token |
| Tax | ⚠ VAT/GST is a **jurisdictional minefield**: EU OSS, reverse charge, US sales tax nexus. Use a tax service (Stripe Tax / Avalara). Do not implement tax logic. This is stated as an engineering decision because someone always proposes a `TaxCalculator` class. ⚠ **CORRECTED (#38), and the decision survives in its useful half:** the seam is `ITaxService`, and its default, `EuVatTaxService`, implements exactly one case — an EU issuer selling an electronically supplied service: the customer country's **standard** rate from a committed, dated table (`Tax/eu-vat-rates.json`), **reverse charge** for a business in another member state with a VAT number of the right shape (the invoice carries the Article 196 sentence and both numbers), and **out of scope** outside the EU. No reduced rates, no US nexus, no OSS return, and a non-EU issuer is refused rather than guessed for. That is enough for an EU issuer's first paying customers and small enough to be read in one sitting; a Stripe Tax or Avalara implementation replaces it rather than extends it. The VAT number is checked for shape only — [§ What is owed](#what-is-owed) |
| Dunning | Payment failed → retry schedule → `Warned` → `Suspended` → `Disabled` ([06](06-tenancy-and-resource-model.md)). Every step notified, with the timeline stated |
| Credits and refunds | Ledger entries with a reason, an approver, and an audit trail. ⚠ **Landed (#38) as credit notes**: numbered in their own gap-free series per issuer, each line capped at what its invoice line still carries after earlier credits, a reason and an approver required, a request id making a retry one note. The invoice's tax treatment and rate are reused, never re-quoted. `IBillingAccountGrain.ProposeCorrectionAsync` re-rates a finalized month against the corrected ledger and proposes the credit — and reports usage now worth *more* than was invoiced, which is owed a debit note |
| Invoice PDF | Generated, stored in a platform bucket, downloadable. Legally must be immutable and retained per jurisdiction |

⚠ **The invoice as built (#38).** A **billing account** is one per tenant — the tenant is the contracting
party, the thing that signs up and holds a VAT number — with subscriptions **attached** explicitly, each
checked against the tenant's own subscription grain. The **invoice grain is the month**: a draft is not
stored but rated from the attached subscriptions' ledgers on every read, so it accrues as usage lands;
`FinalizeAsync` is refused until the 48-hour window after the month has passed, and then rates once
more, takes a number and stores a document nothing can change again; the account's month-close reminder
calls it, and so can anyone (`InvoicingTests.TheMonthCloseFinalizesEveryDueMonthOldestFirstAndThenNothing`).
A credit note's tax is the invoice's rate on everything credited so far, rounded once, less what earlier
notes carried — rounding each note alone returned more tax than the invoice charged
(`InvoicingTests.PartialCreditNotesNeverReturnMoreTaxThanTheInvoiceCharged`). **Gap-free numbering per issuer**
(`IInvoiceNumberingGrain`, a platform singleton): a number is allocated only at finalization, keyed by
the document it is for, so a retry after a crash between allocation and write gets the same number;
the invoice confirms once written, and an allocation never confirmed is listed by `AuditAsync` rather
than silently skipped (`InvoicingTests.TwoAccountsFinalizingGetConsecutiveNumbersAndARetryGetsTheSameOne`,
`InvoicingTests.TenConcurrentFinalizationsGetTenConsecutiveNumbers`). ⚠ **A number is answered only once
it is on disk.** Both grains re-read their state before the first call after one that threw, rather
than answer a retry from memory a failed write left ahead of storage — the first version did, and two
faults together gave two invoices one number (`InvoicingTests.ANumberWhoseWriteFailedIsNeverAnsweredFromMemory`).
A document is **dated by the instant its number was allocated**, which the numbering grain records
with the number and gives back on a retry, so dates run in the order of the numbers even when a
finalization is retried after a later invoice took the next one
(`InvoicingTests.AnInvoiceRetriedAfterALaterOneKeepsTheDateItsNumberWasTakenAt`). A credit-note request
id reused for a different credit is refused, not answered with the earlier note
(`InvoicingTests.ARequestIdReusedForADifferentCreditIsRefusedAndIssuesNothing`). Confirming forgets
the document's key, so the singleton's state is what is in flight, not every document ever
numbered; and a month is finalized only after the month before it, since the first
attach, and never after a later one, so numbers follow months
(`InvoicingTests.AMonthIsNotFinalizedPastAnUnfinalizedOneOrAfterALaterOne`). The issuer is configuration
(`CyberCloud:Billing:Issuer`) with no default: a silo without one prices costs and budgets and refuses to
compute an invoice, because tax depends on the issuer's country. **Payment**: `IPaymentServiceProvider`
with a Stripe adapter over its REST API — customers, payment methods collected by setup intent so card
data never reaches the platform, off-session invoice payment with an idempotency key, and webhook
signature verification — tested against `stripe/stripe-mock` in a container; no key is in the
repository, and nothing wires the adapter into a host yet.

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

## What is owed

What #38 did not land, each with the id a later change closes by name. None of it is a stub in the tree:
where a seam exists, its default refuses and says which of these it is waiting for.

| Id | What | Why it is not here |
|---|---|---|
| `storage-is-declared-not-observed` | `StorageGbMonths` is rated on the size a resource's **desired body** declares (`MeterDerivation` — for a storage account, `volumeServers × storage.size + 10Gi`), not a measurement. **The error is exact and one-sided per case:** while a claim's expansion is pending, and for as long as it keeps failing (a storage class without `allowVolumeExpansion`), the line bills the declared size over the smaller volume that exists — **billed high** by the difference, per hour; after a shrink, which Kubernetes refuses for a claim, the body says less than the volume holds — **billed low** by the difference, indefinitely. The API server's respelling of a quantity (`102400Mi` → `100Gi`) is the same number and costs nothing. Every such line carries `DeclaredQuantity` and the invoice prints why (`InvoiceBuilder.DeclaredQuantityNote`). ⚠ `BackupGbMonths` is not one of them: it shares the storage quota family, the sampler accrues only a family's first meter, and a backup reaches the ledger as its provider's own emission — which no provider makes yet | Fixing it is an observation: the claim's `status.capacity` read back per sample, which the sampler cannot do while it has no source (next row). A difference found is corrected by credit note |
| `the-sampler-has-no-source` | `IMeteredResourceSource`'s only implementation refuses, so no production silo samples anything and the usage ledger stays empty | The intended source is the resource-graph projection (#54), filtered to a subscription and joined to the committed quota draws |
| `vies-validation` | A VAT number is checked for shape only, and `TaxQuote.VatIdCheck` says `format`. Reverse-charging a number VIES would reject leaves the issuer liable for the VAT | The VIES `checkVatNumber` call is network no test here may have, and its availability windows need a retry policy and a recorded consultation number per invoice |
| `vat-rates-are-a-committed-table` | The EU standard rates are a dated table as known on 2026-09-23 | A rate change is a new row today; a feed, or the tax service that replaces this default, is the durable answer |
| `psp-host-wiring-and-webhook-endpoint` | The Stripe adapter is tested and wired into no host; there is no webhook endpoint and no payment attempted at finalization, and nothing stores the customer id `CreateCustomerAsync` returns — which is what stops a second Stripe customer for one tenant once the 24-hour idempotency window has passed (`StripePaymentServiceProvider.CustomerIdempotencyKey`) | The API key and the webhook signing secret come from the vault, and the endpoint is an unauthenticated ingress whose only authentication is the signature — the shape [17 § The outbound carrier](17-communication-and-email.md)'s receipt ingress is owed in too |
| `psp-flow-against-test-mode` | `stripe-mock` is stateless: a setup intent is a fixture, a charge never declines | A Stripe test-mode account in CI proves the flow, and it is a credential the repository will not hold |
| `dunning` | Failed payment → retry → `Warned` → `Suspended` → `Disabled`, every step notified | Needs a payment attempted first (row above) |
| `invoice-pdf` | The invoice as a document in a platform bucket, immutable and retained | The object store exists (#29); the renderer and the retention policy per jurisdiction do not |
| `debit-note-for-late-usage` | Usage past the 48-hour window that makes a finalized month worth more is reported by `ProposeCorrectionAsync` and invoiced nowhere | A debit note, or carrying it to the next invoice, is a legal decision per issuer country |
| `cost-by-tag-and-export` | Cost by tag, and the daily CSV/Parquet export | The usage ledger's entries carry no tags; `MeteredResource.Tags` has them at sampling and nothing carries them further |
| `billing-account-provisioning` | **Nothing creates a billing account.** No production code calls `ConfigureAsync` or `AttachSubscriptionAsync`: sign-up (`SignUpOrchestrator`) and `PUT subscription` (`ScopeManagerService`) create a subscription that no account carries. The month close is built and armed by the first attach, so until something attaches, no month is ever finalized and no usage is invoiced | Attaching at creation is a new edge from the resource manager or the identity host into billing, which `module-layering.txt` doesn't have and #38 did not decide; the profile it needs (legal name, country, VAT number) is typed by a person on the portal's billing page (next row) |
| `issuer-configuration` | **Nothing deployed sets `CyberCloud:Billing:Issuer`** — no chart value, no AppHost parameter — so once an account exists, every draft and finalization on a production silo is refused for want of an issuer. Only `BillingAcrossTheHostsTests` passes one, on its command line | The issuer is the legal entity that invoices: its name, country, VAT number and number prefix are the operator's to state, and a default would print a placeholder on a legal document. A chart value with no default, refused at install when billing is on, is the likely shape |
| `budget-service-grant` | A budget sends through a service in its own resource group only (`Budgets.InSameGroup`), because nothing on the write path asks whether a `SchemaFormat.ResourceId` reference's author may use what it names. ⚠ Two gaps remain: a contributor on one existing budget, and on nothing else in its group, can still point it at a service in that group; and `Monitor/workspaces/alertRules` (`MonitorAlertRules.ToSpec`) checks the tenant only, so a rule in one group can send through another group's service | The general answer is a check at the resource manager, for every `ResourceId`-format field, that the caller holds a permission on the referenced resource — a write-path change for every type, not #38's — or a grant from the service's owner to the budget as principal, the shape `scope: subscription` already uses |
| `billing-http-surface` | ⚠ **Half landed (#41): reading invoices.** `GET /tenants/{t}/providers/CyberCloud.CostManagement/invoices[/{number}]`, answered to `read` on the tenant — the account's own ReBAC question, decided as the smallest answer the schema has ([20 § What is owed](20-portal.md) `billing-reader-role` is the narrower one). Still grain calls with no route: configuring the account, attaching subscriptions, issuing credit notes, and the running month's draft | Each is a write on the account with its own permission question, and the draft needs the issuer on every read — [20 § What is owed](20-portal.md) `billing-account-pages` and `invoice-draft-over-http` |
| `ledger-reads-are-whole` | `UsagePricing` reads a subscription's whole usage ledger and filters | A range read on the ledger, or the cost view reading `usage_hourly` once a sink writes it |
| `invoices-in-one-grain` | Every invoice a tenant has is in one grain's state | Small for decades at a few dozen lines a month; a tenant with thousands of subscriptions is a grain per month and a new key shape |

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
