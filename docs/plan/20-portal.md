# 20 — The Portal

Angular 22, zoneless, SSR, Tailwind 4, built from [`@xui/*`](https://xuijs.org) — ADR-017. The single
largest frontend item in the plan (5.0 EM) and the one most likely to be underestimated, because a
cloud portal is not a CRUD app: it is a hundred resource types, each with a form, a list, a metrics
view and a set of actions, plus live updates and a topology view.

## The shape that makes 100 resource types affordable

**Almost every screen is generated** (ADR-012). A resource type contributes a JSON Schema; the portal
renders it.

```
libs/resource-forms/
  ├─ schema-renderer      ← JSON Schema → xUI controls
  ├─ widgets/             ← x-cybercloud-widget hints: region, cluster, storageclass,
  │                         subnet, sku, secret-ref, cron, cidr, duration
  ├─ layout/              ← @section annotations → tabs and groups
  └─ validation/          ← schema + async server validation, one message shape
```

Schema → widget mapping, decided once:

| Schema | Widget |
|---|---|
| `string` + `enum` | `@xui/select` (≤ 8) or `@xui/suggest` (more) |
| `string` + `x-widget: region` | Region picker with capacity/latency hints |
| `string` + `x-widget: cluster` | Cluster picker, filtered by subscription and capability |
| `string` + `format: password`/`x-secret` | `@xui/input` + a Vault `SecretRef` picker — **never a plain value** |
| `integer` + `min`/`max` | `@xui/slider` + `@xui/numeric-input` |
| `object` + `additionalProperties: string` | `@xui/tag-input` (tags, labels) |
| `array` of strings or numbers | `@xui/tag-input` chips. ⚠ A list of numbers carries `items` in the forms document and goes back as numbers — before #41 a budget's thresholds went back as text and the write path refused the body |
| `array` of objects | `@xui/data-table` with inline add/remove |
| `x-immutable` | Disabled after create, with a tooltip naming why |
| `x-cozy-preset` (sizing) | The `t1.micro`/`c1.large` family picker from [12](12-managed-data-services.md) |

**The escape hatch, and its limit.** A hand-written form may replace the generated one, keyed by
`(resourceType, apiVersion)`, and lives in `libs/resource-forms-overrides/`. Expect ~10 of these — the
resources people create daily. **Every override must render the same schema**, verified by a test that
submits the override's output against the schema. An override that accepts something the API rejects
is worse than the generated form.

## Information architecture

Copied from Azure's portal, because it is a good design that a million people already know, and
because deviating costs onboarding for no benefit.

| Element | Behaviour |
|---|---|
| **Blades** | Stacked, deep-linkable panels. `@xui/panel-stack` + `@xui/dock-manager` |
| **Resource blade** | Left rail: Overview · Activity · Access (ReBAC) · Tags · Locks · Metrics · Logs · Diagnose · Settings · type-specific |
| **Omnibar** (`Ctrl/⌘ K`) | `@xui/omnibar` — resources, actions, docs, tenants. **The primary navigation.** Deep hierarchies are unnavigable by clicking and everyone who uses a cloud daily uses the search box |
| **Resource list** | `@xui/data-table` over the resource-graph projection ([08](08-resource-manager.md)) — virtual scroll, server-side filter/sort, column chooser, saved views, CSV export |
| **Breadcrumbs** | tenant → subscription → resource group → resource, each clickable |
| **Notifications** | A tray fed by `/hubs/operations` — every LRO the user started, with progress |
| **Context bar** | Tenant + subscription switcher, always visible. Getting this wrong means people act in the wrong subscription, which is a real and expensive class of mistake |

## Live updates

`/hubs/resources` and `/hubs/operations` ([10](10-gateway-and-api.md)). A blade declares its interests
on open and drops them on close; the connection grain manages the subscription set.

**Signals throughout.** Every stream is a `signal`; the templates are `OnPush` and zoneless. Since xUI
is zoneless and signal-based, this is the natural style rather than a discipline — a `ChangeDetectorRef`
in portal code is a code-review failure.

⚠ **Optimistic UI is used narrowly and deliberately.** Tags and names update optimistically; anything
that creates, deletes or costs money does not — it shows the operation's real progress. An optimistic
"deleted!" that later fails is how trust is lost.

⚠ **A hub is opened with a ticket, and the bearer token is never in a URL.** The portal holds its
token in memory and puts it in a header; a WebSocket has no header, so every hub connection starts
with `POST /hubs/{hub}/ticket` and opens the socket with the thirty-second, single-use value that comes
back — [10 § SignalR](10-gateway-and-api.md#signalr). `HubTicketsApi` is the one place the portal
builds a socket address, and its spec sabotages the property. The cloud terminal is the first hub the
portal opens this way; the three live-update hubs will use the same call.

## SSR

Server-side rendered with hydration, for three reasons and not for SEO:

1. **First paint on a cold load** matters when the alternative is a spinner on a 3 MB bundle.
2. **The docs site and the marketing pages** share the app shell.
3. **Deep links** — a link to a resource blade from an alert email must render something immediately.

⚠ **The authenticated portal is rendered per request and must never cache a rendered page across
users.** The SSR process holds no tokens; it renders the shell and the client hydrates with the user's
token. Getting this wrong leaks one tenant's data to another through a CDN cache, which is the worst
bug this document can prevent. It is an explicit test: two concurrent SSR requests with different
tenants, asserting no shared state.

⚠ **The SSR process trusts no proxy header unless the deployment names it.** The render never reads
the request's origin — the identity issuer is a `<meta>` in the document, redirects are relative, and
the shell is the same for every scheme and host — so `Forwarded` and `X-Forwarded-*` are removed at
the process's edge and `@angular/ssr` is handed the trust list explicitly (`NG_TRUST_PROXY_HEADERS`,
empty by default). A deployment behind an ingress that terminates TLS or serves a prefix names exactly
the headers that ingress sets, and sets `NG_ALLOWED_HOSTS` to the names it serves, because the built
bundle allows `localhost` alone. The same two SSR gates assert the engine warns about nothing —
portal/README.md § The Angular pin and § SSR isolation.

## The pages that are not generated

| Area | Why it is bespoke | EM |
|---|---|---|
| Dashboard / home | Cost, health, recent, quick-create. Nothing generic about it | 0.4 |
| Cost analysis | `@xui/echarts` — breakdowns by tag, resource group, service, day. Forecast, budgets. ⚠ **Landed (#41) except by tag**, at `subscriptions/{s}/cost` and `…/resourceGroups/{g}/cost` (`portal/apps/portal/src/pages/cost`): a period picker (this month, last month, 30 and 90 days, two custom days) and a grouping (service, resource group, resource, meter) in the URL; one cost query with `granularity: daily` drawn as a stacked bar per day and listed beside it as a table, the table being the chart's accessible copy; a month-end **forecast, linear on the trailing seven days** — this month so far plus the trailing week's spend per hour times the hours left, the budget grain's method, computed from a second `groupBy: day` query and labelled an estimate; on a group, its **budgets** with the thresholds that fired this period, through the generated client's `listBudget` and the new `showStatus` action, created and edited by the generated blades; and the tenant's **invoices** at `/invoices` with each one's lines at `/invoices/{number}`, read from the gateway's new `…/providers/CyberCloud.CostManagement/invoices`. Tags aren't on the usage ledger, so by tag waits for [22 § What is owed](22-billing-metering-and-quota.md) `cost-by-tag-and-export`; the rest of the row's debt is [§ What is owed](#what-is-owed) | 0.6 |
| Metrics explorer | Query builder, chart types, pinning to dashboards. ⚠ Landed 2026-09-23 (#41) as `…/providers/CyberCloud.Monitor/workspaces/{name}/metrics`, `portal/apps/portal/src/pages/monitor`: a metric picker fed by the workspace's own label API, label filters, `rate`, an aggregation `by` labels, the PromQL it writes (editable by hand), a time-series chart and the same numbers as a table — over `queryMetrics` and `listMetricLabels`, [16 § Querying a workspace](16-observability.md). No pinning: the portal has no dashboards to pin to | 0.6 |
| Log search | `@xui/code-block` + a results grid over ClickHouse. ⚠ Needs a query cost preview or someone will run a 400-day scan. ⚠ Landed 2026-09-23 (#41) at `…/workspaces/{name}/logs` over `searchLogs`: a query box whose grammar (`severity:error service:api "text" key=value trace:…`) is parsed into a structured filter rather than a query language — 16 § Querying a workspace says why — a time range, a severity-stacked histogram whose bars narrow the window, and the newest records as a table — time, severity, service and message, the time in the viewer's zone to the millisecond — whose rows expand to their attributes and the stored nanosecond timestamp. The cost preview is ClickHouse's `EXPLAIN ESTIMATE`: on demand, and asked first by any search over more than a day, which stops for a confirmation above ten million rows. A plain input rather than `@xui/code-block`, which highlights code and does not edit it | 0.6 |
| Resource graph explorer | ⚠ Landed 2026-09-23 (#41) at `/graph`: a KQL box over #54's address ([08 § The resource-graph projection](08-resource-manager.md)), results with the columns the translator names, and "load more" by `$skipToken` — never by following `nextLink`'s URL. The subset's refusal is shown as the platform wrote it, because it is the documentation. `ResourceGraphApi` is hand-written beside `RoleAssignmentsApi`, for the reason [10 § Shape](10-gateway-and-api.md) gives | — |
| Network topology | `@xui/node-graph` — VPCs, subnets, endpoints, peerings. The one view that is genuinely better than a list | 0.5 |
| Cloud terminal | `xterm.js` over a console's `connect` ([19](19-cloud-terminal-and-virtual-desktop.md)). ⚠ Landed as a routed page under the resource group — `subscriptions/{s}/resourceGroups/{g}/terminal`, `portal/apps/portal/src/pages/terminal` — not the dockable panel this row first said: a shell runs in a console resource, and a console has a group, so the page has a scope and a link can name a console. A dockable pane over the same `TerminalSession` is a later affordance, not a second terminal | 0.4 |
| Access (ReBAC) | Role assignments, the effective-permissions explorer, "why does this user have access" | 0.6 |
| Identity admin | Users, groups, apps, MFA, sign-in logs. ⚠ Three of five landed with #41 as three routed pages under `/identity` — `portal/apps/portal/src/pages/identity` — sharing a tab strip: **members** (the tenant's users, the invite form, pending invitations with resend and revoke, remove), **applications** (tenant-registered OAuth clients: register with redirect URIs, public or confidential and scopes, the confidential client's secret shown once, rotate, delete), and **my sessions** (the signed-in person's own, with this one marked and a sign-out per row). Groups, MFA and sign-in logs are the owed list below | 0.5 |
| Onboarding | Sign-up → tenant → first cluster → first resource, as a guided flow | 0.4 |
| Webmail | [17](17-communication-and-email.md), counted there | — |

**"Why does this user have access"** deserves the call-out. It renders the ReBAC `Expand` as a tree,
showing the path that grants a permission. Without it, an authorization system that supports nested
groups and inheritance is unauditable, and the support cost of an unauditable authorization system is
enormous.

**Identity admin, as it landed (#41).** The three pages call the identity administration API at the
gateway — `/tenants/{t}/providers/CyberCloud.Identity/…`, [10 § Shape](10-gateway-and-api.md) — through
`IdentityAdminApi`, the portal's second hand-written client beside `RoleAssignmentsApi` and on the same
transport, because the reserved namespace keeps the address out of the document the client is generated
from. Every directory call needs Owner on the tenant (`assignRole`, fully consistent); the page shows the
API's `403` or `404` rather than guessing at roles the token doesn't carry. The sessions page needs no
role: the address names no user. ⚠ The client secret lives in one signal on the applications page and
nowhere else — not a store, not the URL, not a blade title — and is dropped on a tenant switch and on
deleting its application; a reload loses it, and the answer is Rotate. `identity-pages.spec.ts` and
`identity-admin.spec.ts` drive the pages and the client against a recorded platform, axe included.

⚠ **What identity admin still owes**, each named where the code stops:

- **Groups.** `IGroupGrain` exists and membership is tuples ([11 § The object model](11-identity.md)), but
  no HTTP surface creates a group, lists one or adds a member — so the page has nothing to call.
- **MFA administration and sign-in logs.** Enrolment is the identity app's, per person; an owner can't
  see or reset another member's factors. Sign-in logs wait on the authentication events of
  [11 § Auditing](11-identity.md) reaching ClickHouse, which nothing writes yet.
- **Editing a registration.** Redirect URIs and scopes are fixed at registration: `IApplicationGrain.UpdateAsync`
  exists and no address reaches it. Delete and re-register is the workaround, and it changes the client id.
- **Two live secrets.** Rotation kills the old secret in the same turn, so a server rotating in place is
  refused between the rotation and its redeploy.
- **Sign out everywhere.** One row at a time today; a cookie session's token sessions end at their next
  refresh and stay listed until then.
- **Paging, and "who is this".** Each list is one directory index read whole (capped at ten thousand);
  a session shows its client id, not the application's display name; the members page doesn't hide
  Remove on the owner's own row — the API's `409` is the refusal.
- **A tenant from before #41 lists only the people added since** — the directory index can't be
  backfilled from ids nothing recorded.
- **`@xui/table` gives its cells no ARIA role**, so every `xui-th` and `xui-td` on these pages carries
  `role="columnheader"` or `role="cell"` by hand; axe fails `aria-required-children` on any rendered row
  without it. The fix is the library's, and until it lands the other pages' tables need the same.

## Admin app

`portal/apps/admin` — a second Angular app, same libraries, **same public API** with a platform scope
([06](06-tenancy-and-resource-model.md)). Tenants, regions, shards, clusters, quota overrides, feature
flags, impersonation (with the consent and notification rules from 06), platform health.

Separate app rather than a route, so that a bug in tenant-facing code cannot reach admin functionality
and vice versa, and so admin can be served on a separate origin with stricter network controls.

## Accessibility, i18n, theming

- **WCAG 2.2 AA is a gate, not a goal.** `@xui/core/a11y` provides focus management, roving tabindex
  and live regions; axe runs in CI on every route. Cloud portals are used all day by people who
  navigate by keyboard, and a modal that traps focus wrongly is a bug that stops work.
- **i18n from day one** with `@angular/localize`. English at M1, but the string extraction is in place
  from the first commit — retrofitting i18n across 200 components is a quarter.
- **Theming is tokens**, not `dark:` classes. Light and dark both work by construction (ADR-017), and a
  white-label per tenant is a token override rather than a fork.

## Performance budget

Enforced in CI, failing the build:

| Metric | Budget |
|---|---|
| Initial JS (shell, gzipped) | < 250 KB |
| Route chunk | < 120 KB |
| Chart library — ECharts, one deferred chunk | < 180 KB — ⚠ proposed by #41, not signed off ([§ What is owed](#what-is-owed) `chart-library-ceiling`) |
| LCP on a resource list, cold, 4G | < 2.5 s |
| INP | < 200 ms |
| Data table, 10 000 rows, virtualised | 60 fps scroll |
| Blade open → first content | < 300 ms warm |

⚠ **The chart library has a ceiling of its own, and the 180 KB is #41's proposal, not a decision.** The
need for a separate ceiling is a measurement; the number is the implementer's, written here so the gate
has one, and it is owed a sign-off (`chart-library-ceiling`). ECharts is `@xui/echarts`' engine and
ADR-017 keeps the portal on xUI, so the cost page draws with it. Bundled alone with esbuild, minified
and gzipped at level 9, **ECharts' core and canvas renderer are 129.3 KB** — no build of it fits the 120
KB route ceiling — the portal's tree-shaken set (bar, a plain grid and legend, tooltip, canvas:
`portal/libs/charts/src/lib/echarts-engine.ts`) is 173.8 KB, and adding the line chart, the full grid
and the scrolling legend makes it 185.2 KB. ⚠ **One engine since the merge (2026-09-24).** The
metrics explorer and log search (#41) were built beside the cost page with a second copy of this seam
and a line chart of their own; the merge kept one — `lib/engine.ts` over `lib/echarts-engine.ts`,
registering the union of what the pages draw (line and bar, the full grid, whose axis pointer the
explorers' axis tooltip needs, the plain legend and the tooltip, on canvas) — and this script's
marker check rather than the explorers' match on a chunk name, because the marker also catches the
library split, merged into a route, or in the initial set. The Angular production build emits the merged set as a
chunk of **177.3 KB** (172.8 KB before the line chart joined it), which is the number `scripts/bundle-budget.mjs` reports and holds to the ceiling.
It is never in the initial set (196.2 KB of 250 with the cost pages) and never in a route chunk (the
cost page's is 10.1 KB): it is one chunk, fetched by `import()` the first time a chart renders, and the
page shows its totals, its table and its budgets without it. The script finds it by the
`_echarts_instance_` attribute only ECharts contains, in a chunk with no Angular definition in it, and
fails if the library is split, merged into a route chunk (a chunk with the marker *and* a compiled
component is measured against the route ceiling and failed by name) or reached from the initial set.
Every chart type a later page registers is paid for there.

⚠ **`xui-th` and `xui-td` carry no ARIA role at `@xui/table` 3.0.0**, while `xui-tr` is a `row`, so a
table with body rows fails axe's `aria-required-children`. The explorers set `role="columnheader"`
and `role="cell"` on each cell; the fix belongs in xUI, and no spec axe-checks a table that predates
#41 — the resource list's, the access page's — with a row in it, which is how the defect went unseen.

⚠ **Route-level code splitting is mandatory**, and with 100 resource types the generated form renderer
must not pull every schema into the main bundle. Schemas are fetched per type, cached, and versioned by
the api-version — which is also what lets the portal support an old api-version without shipping two
apps.

## What is owed

What the portal's landed pages do not do yet, each with the id a later change closes by name.

| Id | What | Why it is not here |
|---|---|---|
| `cost-by-tag` | Cost analysis grouped by tag | The usage ledger carries no tags — [22 § What is owed](22-billing-metering-and-quota.md) `cost-by-tag-and-export`. The picker offers only what the cost query can answer |
| `per-resource-cost-on-the-blade` | [22 § Cost visibility](22-billing-metering-and-quota.md)'s "this database costs €4.10/day" on every resource blade | One cost query per blade, `groupBy: resource` narrowed to the resource's group, is the whole call; the blade's layout for it is the work, and #41 was the cost page |
| `subscription-budgets` | The subscription's cost page lists no budgets; it sends the reader to a group's | A budget is a resource in a group, and listing a subscription's is a `listBudget` per group — the resource graph (#54) answers it in one query once the page reads it |
| `invoice-draft-over-http` | The running month's draft invoice | `IBillingAccountGrain.PreviewAsync` rates the month on every read and needs the issuer; the invoices address serves finalized documents only, and the page points at cost analysis for the running month |
| `billing-account-pages` | Configuring the billing profile, attaching subscriptions, issuing credit notes | The rest of [22 § What is owed](22-billing-metering-and-quota.md) `billing-http-surface`: #41 landed the read half — the invoices — and these are writes on the account, each with its own permission question |
| `billing-reader-role` | A role that reads invoices without reading resources | Invoices are `read` on the tenant today (`IInvoiceQueryGrain`'s remarks). A billing reader is a new relation in the ReBAC schema and a `SchemaVersion` bump |
| `invoice-pdf-download` | Downloading the invoice as its PDF | The PDF itself is owed — [22 § What is owed](22-billing-metering-and-quota.md) `invoice-pdf` |
| `cost-tile-on-home` | The dashboard's cost, [§ The pages that are not generated](#the-pages-that-are-not-generated)'s first row | The home page is its own row; the cost query it would call now exists |
| `xui-table-cell-roles` | `@xui/table` 3.0.0 gives `xui-tr` `role="row"` and its cells no role, so axe fails every row that has content (`aria-required-children`) | The fix is xUI's (ADR-017). The cost pages set `role="columnheader"` and `role="cell"` by hand and their specs run axe over filled tables; the resource list and the access page still render rows without cell roles, and no spec runs axe over them with rows in |
| `charts-in-a-real-browser` | A chart drawn by ECharts on a canvas and looked at | jsdom has no canvas: the specs hand the chart a recording engine and assert the option the page built, and the colour-contrast gap `a11y.spec.ts` records is the same gap. The browser-driven suite that lands with the e2e layer is where both are closed |
| `chart-library-ceiling` | A signed-off ceiling for the chart library's chunk | #41 needed one to gate the build and chose 180 KB against a 172.8 KB chunk; it is an eighth of headroom, one line chart and a scrolling legend would spend it, and whether the portal should pay that for a chart is a product decision, not a measurement |
| `cost-query-across-two-processes` | A cost query and an invoice read through the gateway's HTTP pipeline into a silo in another process | `BillingAcrossTheHostsTests` makes both calls from the real gateway host's client to the real silo host in one OS process; `CyberCloud.AppHost.Tests` is where a request crosses two |

## Effort

| Piece | EM |
|---|---|
| Shell: blades, navigation, omnibar, context bar, notifications, auth | 1.0 |
| Generated forms: renderer, widgets, layout, validation, overrides | 1.2 |
| Resource lists, filters, saved views, tags, locks, activity | 0.6 |
| Live updates: hubs, interests, reconnect, optimistic rules | 0.4 |
| The bespoke pages above | 4.6 → **but half are M2/M3** |
| SSR, performance budget, a11y, i18n | 0.6 |
| **M1 subset** (shell, forms, lists, live, onboarding, terminal, access) | **5.0** |
