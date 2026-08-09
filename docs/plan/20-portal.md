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

## The pages that are not generated

| Area | Why it is bespoke | EM |
|---|---|---|
| Dashboard / home | Cost, health, recent, quick-create. Nothing generic about it | 0.4 |
| Cost analysis | `@xui/echarts` — breakdowns by tag, resource group, service, day. Forecast, budgets | 0.6 |
| Metrics explorer | Query builder, chart types, pinning to dashboards | 0.6 |
| Log search | `@xui/code-block` + a results grid over ClickHouse. ⚠ Needs a query cost preview or someone will run a 400-day scan | 0.6 |
| Network topology | `@xui/node-graph` — VPCs, subnets, endpoints, peerings. The one view that is genuinely better than a list | 0.5 |
| Cloud terminal | `xterm.js` in a dockable panel ([19](19-cloud-terminal-and-virtual-desktop.md)) | 0.4 |
| Access (ReBAC) | Role assignments, the effective-permissions explorer, "why does this user have access" | 0.6 |
| Identity admin | Users, groups, apps, MFA, sign-in logs | 0.5 |
| Onboarding | Sign-up → tenant → first cluster → first resource, as a guided flow | 0.4 |
| Webmail | [17](17-communication-and-email.md), counted there | — |

**"Why does this user have access"** deserves the call-out. It renders the ReBAC `Expand` as a tree,
showing the path that grants a permission. Without it, an authorization system that supports nested
groups and inheritance is unauditable, and the support cost of an unauditable authorization system is
enormous.

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
| LCP on a resource list, cold, 4G | < 2.5 s |
| INP | < 200 ms |
| Data table, 10 000 rows, virtualised | 60 fps scroll |
| Blade open → first content | < 300 ms warm |

⚠ **Route-level code splitting is mandatory**, and with 100 resource types the generated form renderer
must not pull every schema into the main bundle. Schemas are fetched per type, cached, and versioned by
the api-version — which is also what lets the portal support an old api-version without shipping two
apps.

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
