# `portal/` — Angular 22 + xUI

A **pnpm workspace**, mirroring xUI's Nx conventions so the two feel like one codebase.

```
portal/
├── apps/portal/                     # the tenant-facing portal — Angular 22, zoneless, SSR
├── apps/admin/                      # platform admin — NOT BUILT, see § What is not here
├── libs/api/                        # GENERATED TypeScript client from OpenAPI — never hand-edited
├── libs/resource-forms/             # the schema → xUI form renderer (ADR-012), over generated/forms
├── libs/resource-forms-overrides/   # hand-written forms that replace the generated one, by type+version
├── libs/shell/                      # navigation, breadcrumbs, resource blades, the omnibar
└── libs/charts/                     # metric/log views over @xui/echarts — stub
```

## Rules

- **One design system: xUI.** No Angular Material, no PrimeNG, no per-page bespoke widgets that
  duplicate an xUI component (ADR-017). Where the portal needs a component xUI does not have, it is
  built **in xUI and released there**, not here.
- **`libs/api` has no hand-written files.** The generator owns the directory — docs/plan/03
  § Assembly graph rules, rule 6.
- **The portal has no privileged path.** It calls the same public REST API as the CLI, with the same
  token. There is no `/internal` the portal uses and the SDK does not.
- **Version coupling runs one way.** Angular and Tailwind versions are xUI's pins; the portal
  follows. An Angular major upgrade is an xUI task first.
- Nothing .NET lives here. `CyberCloud.Portal.Host` (the API shim + static serving) is in
  [`src/Hosts/`](../src/Hosts); the Angular SSR node process is separate from it.

## Node

**Node 24 (Active LTS). Pinned in `.nvmrc`, `.node-version` and `package.json` `engines`.**

docs/plan/02 § Platform baseline left this open — the plan said 22 LTS, the dev host runs 26.5.0,
and no `@xui/*` peer range constrains it, so it is the portal's call and it needed settling "before
portal work starts, or local and CI will silently differ". Four inputs decided it on 2026-08-11,
and they were re-checked on 2026-08-12 after `@xui/*` released 2.2.1 through 2.2.4:

| Input                           | Value                                                          | Effect                                                               |
| ------------------------------- | -------------------------------------------------------------- | -------------------------------------------------------------------- |
| Angular 22's own `engines.node` | `^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0`                         | 22, 24 and 26 are all permitted — Angular does not decide it either  |
| Node release state, 2026-08     | 22 is **Maintenance**, 24 is **Active LTS**, 26 is **Current** | 24 is the only one that is both supported and LTS today              |
| ~~xUI's own pin~~               | ~~`engines.node: 24.x`~~ — see below                           | ⚠ **Withdrawn 2026-08-12.** It is not a constraint on this workspace |
| The dev host                    | 26.5.0, and it is the only Node installed                      | Needs to keep working, so the pin cannot be a wall                   |

⚠ **The input this decision called strongest does not exist.** It was recorded as "xUI itself pins
`engines.node: 24.x`, which is the strongest signal". Re-checked against **npm** on 2026-08-12,
across `2.2.0` (current when the decision was made) through `2.2.4` (current now), and against the
unpacked `@xui/core@2.2.4` tarball rather than only the registry metadata: **no published `@xui/*`
package carries an `engines` field at all.** The `24.x` is in the root `package.json` of xUI's
development monorepo — the checkout this repository's own standing rule says not to read versions
from, for exactly this class of reason. It binds xUI's contributors, not xUI's consumers.

That is a real correction, and it does **not** move the pin. Re-evaluated, the surviving form of
the input still points at 24: every `@xui/*` package at 2.2.4 was published from Node 24.18.0
(`_nodeVersion` in the registry metadata), so 24 remains the runtime the library is built and
tested on. (Re-checked at 3.0.0 on 2026-09-15: still no `engines` field in any of the 23 packages,
all published from Node 24.20.0.) It is now a _weak_ input where the decision claimed a strong one
— it is corroboration, not a constraint — and with it demoted, the pin rests on the Node release
calendar alone.

So: **24**, unchanged, because none of the four inputs reversed. Node 26 is still rejected on the
one ground it was rejected on: it does not become LTS until 2026-10-20, and a platform's portal
should not build on a Current release.

⚠ **This pin has a dated expiry, and it is close.** On **2026-10-20** Node 24 goes Maintenance and
26 becomes Active LTS — the exact condition that made the previous "Node 22 LTS" wrong starts
applying to 24, on a known date. The pin was already stale once because nothing was watching for
that. Revisit on or before that date; the move is then `.nvmrc`, `.node-version` and
`package.json` `engines`, and nothing else, because CI reads the file rather than restating the
number.

⚠ **How the pin is enforced, and why the halves differ.** `scripts/check-node.mjs` warns locally
and fails in CI (`pnpm node:gate`, which `pnpm gates` runs first). `engine-strict` is deliberately
off in `.npmrc`. The reasoning is that a wall in the developer's path and a wall in CI's path have
opposite costs: a blocked local install stops work over a version that will build fine, while a
drifted CI image silently produces artefacts nobody can reproduce. So the warning is the nudge and
the CI gate is the wall.

⚠ **That reasoning was tested, and the nudge half failed.** A session of portal work — a layout
fix, a class-coverage gate, two SSR suites, 73 jest tests — was measured on the host's Node 26 and
reported as if it were the pinned runtime. The warning had fired. It fired once, before the work,
minutes and thousands of lines of build output before the figures it applied to. The wall half
still holds and is not negotiable here: the dev host has **only** Node 26, so `engine-strict=true`
would mean the portal could not be installed at all.

The fix is a third mode rather than a fourth wall. `pnpm node:recap`
(`scripts/check-node.mjs --recap`) runs **last** in `pnpm test` and `pnpm build`, prints the
runtime next to the numbers it qualifies, and exits 0 either way. Off-pin it says so in a banner:
the failure to prevent was never "built on the wrong Node" — that is allowed on purpose — it was
"recorded a figure without knowing which runtime produced it".

**The CI image must be Node 24**, and both paths that reach the portal now say so from this file:
`.github/workflows/gate.yml` § portal and `.github/workflows/release.yml` § publish, each via
`node-version-file: portal/.node-version`. The release path is the less obvious one — `Publish`
`.DependsOn(… Portal …)` in `build/Build.cs`, so a release runs the portal gate.

## The Angular pin

**`@angular/*` is pinned to exactly `22.0.8`, and `@angular/cdk` to exactly `22.0.6`.** ⚠ **Since
`@xui/*` 3.0.0 those numbers are no longer forced by any peer.** They were, and the reason is kept
here because it is the reason every xUI bump has to re-measure rather than reuse this table.

At `@xui/*` 2.2.x, docs/plan/02 § ADR-017's claim that the peer range is `@angular/*: 22` — "a
major range" — was not true of every package. `@xui/panel-stack`, `popover`, `tooltip`,
`breadcrumb` and `overflow-list` peered `"@angular/common": "22.0.8"` **exactly** (five packages;
an earlier version of this table, measured at 2.2.0, listed four), and `@xui/echarts` peered
`"@angular/cdk": "22.0.6"` — exact, and a different version again. `@angular/common@22.0.8` peers
`"@angular/core": "22.0.8"` exactly, so one exact peer dragged the whole framework to a point
release; with `@angular/*` at the then-head 22.1.1 and `strict-peer-dependencies=true`,
`pnpm install` failed.

**Re-measured on 2026-09-15 from the registry for `@xui/*@3.0.0`** — all 22 packages the portal
declares plus `@xui/echarts`, which the charts stub will need — with `npm view @xui/<pkg>@3.0.0
peerDependencies`, and diffed against 2.2.4 package by package. Every peer that changed:

| Package                                                            | Peer                               | 2.2.4    | 3.0.0                      |
| ------------------------------------------------------------------ | ---------------------------------- | -------- | -------------------------- |
| `panel-stack`, `popover`, `tooltip`, `breadcrumb`, `overflow-list` | `@angular/common`                  | `22.0.8` | `22`                       |
| `echarts`                                                          | `@angular/cdk`                     | `22.0.6` | `22`                       |
| the ten that already peered it (`breadcrumb` … `toast`)            | `@ng-icons/core`, `material-icons` | `34`     | `35`                       |
| `icon`                                                             | `@ng-icons/core`                   | —        | `35`                       |
| all 22                                                             | `@xui/*` on each other             | `2.2.4`  | `3.0.0` (exact, as before) |

Nothing else moved: `clsx ^2.1.1` (`>=2.0.0` in `@xui/core`), `class-variance-authority ^0.7.1`,
`rxjs ^7.8.0`, `luxon >=3.0.0` and `tailwind-merge >=3.0.0` peer exactly as they did at 2.2.4, and
the portal's pins satisfy them. **No exact `@angular/*` peer survives in any of the 23 packages**;
`pnpm install` under `strict-peer-dependencies` reported none unmet.

**So why is the pin still 22.0.8/22.0.6?** Because moving Angular is a different change from
moving xUI, and this one was xUI's. The gate is green at 22.0.8 under 3.0.0: the 3.0.0 bundles
carry `version: "22.1.4"` partial-compilation stamps — xUI's checkout pins `@angular/* 22.1.4` and
`@angular/cli 22.1.6` at the `v3.0.0` tag — with `minVersion` unchanged, and 22.0.8's linker
accepts them. ⚠ **The earlier justification, "the version xUI's own checkout pins, so the portal
is running what xUI is tested against", is no longer true and is no longer the reason.** xUI tests
against 22.1.4. Whoever moves the pin has a free choice inside `22.x` for the first time; the
principled target is what xUI tests against, not the registry head (22.1.6 framework / 22.1.8
tooling on 2026-09-15). `pnpm-workspace.yaml`'s single-version comment and
`libs/charts/src/index.ts`'s dependency note both point here and want the same edit when it
happens.

**`@ng-icons/*` is pinned to `35.1.0`**, the head of the `35` range `@xui/*@3.0.0` peers on. The
registry head is `36.0.0`, which is out of range.

The build tooling (`@angular/cli`, `@angular/build`, `@angular/ssr`) is pinned to 22.0.8 as well.
It versions independently of the framework — the tooling head is 22.1.8 — but keeping the two in
step means one number to reason about.

## The pages, and what each one calls

Issue #22's M1 pages, each a lazy route over the generated client (`libs/api`) and the generated
form document (`generated/forms/{apiVersion}.json`, staged into `/forms/` by `scripts/sync-forms.mjs`
before every build and serve):

| Route                                                          | Page                  | Calls                                                                                                                                       |
| -------------------------------------------------------------- | --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `/subscriptions`                                               | list + create         | `createSubscription` — the list is `TenantContextStore`, because the API has no list endpoint                                               |
| `/subscriptions/{s}`                                           | subscription blade    | `getSubscription`                                                                                                                           |
| `…/resourceGroups`                                             | create + open by name | `createResourceGroup` — no list endpoint, and the empty state says so                                                                       |
| `…/resourceGroups/{g}`                                         | resource group blade  | `getResourceGroup`, plus a create and a list link per top-level type from the form document                                                 |
| `…/resourceGroups/{g}/resources?type=`                         | resource list         | `list{Type}` (#10), paged by `$skipToken`                                                                                                   |
| `…/resourceGroups/{g}/create/{ns}/{type}[/{child}]`            | create blade          | the generated form, then `createOrUpdate{Type}` → the operation view                                                                        |
| `…/providers/{ns}/{type}/{name}`                               | resource blade        | `get{Type}`; delete is `delete{Type}` after the name is typed back → the operation view                                                     |
| `…/providers/{ns}/{type}/{name}/edit`                          | edit blade            | `get{Type}`, then a full `createOrUpdate{Type}` with the immutable fields locked and still sent                                             |
| `/operations/{id}?then=`                                       | operation view        | `getOperation`, polled until terminal; every poll feeds `NotificationsStore`                                                                |
| `/subscriptions/{s}/access`, `…/{g}/access`, `…/{name}/access` | access page           | `RoleAssignmentsApi` — `PUT`/`GET`/`DELETE {scope}/providers/CyberCloud.Authorization/roleAssignments/{name}` (#70), by hand; no list (#86) |

⚠ **The verb names are derived from the form's `title`**, exactly as `TypeScriptEmitter` derives
them from the type's display name, and `apps/portal/src/app/api/resource-verbs.spec.ts` asserts the
four verbs exist on `CyberCloudApi` for every type in the document. A hand-written table would be a
third copy of what two generated surfaces already say.

⚠ **The access page is the one page over a hand-written client, and the reason is the address, not
the page.** `CyberCloud.Authorization` is a reserved namespace the registry refuses, so the emitters
that read the registry never see `{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}`
— docs/plan/10 § Shape calls it "#63's question asked a third time".
`apps/portal/src/app/api/role-assignments.ts` builds the address for a tenant, a subscription, a
resource group or a resource, derives the name as `RoleAssignmentName` does
(`{role}-{principalType}-{principalId}`), and sends it through the same `HttpApiTransport` as the
generated client, so the token, the api-version and the error mapping are owned once. The day an
emitter learns the address, the three methods become delegations. The page grants, checks and
revokes; it cannot list, because the collection answers 400 (#86), and its table holds only what it
granted or checked in the visit, labelled as exactly that.

⚠ **Every page renders a "no tenant" state on a fresh portal.** Nothing populates
`TenantContextStore` until the token exchange lands (docs/plan/11's M2 work), so the pages say so
rather than calling the API with no tenant. `apps/portal/src/pages/pages.spec.ts` signs a tenant in
and drives each page against a recorded platform.

## Gates

Every one of these fails the build rather than warning. `pnpm gates` runs them in order, and
`./build.sh Portal` runs `pnpm gates` — docs/plan/23 § Build, row `Portal`, and `build/Build.Portal.cs`,
which invokes this chain rather than restating it. Locally that target runs `pnpm verify` instead,
which is `gates` without the Node wall; see § Node above for why that asymmetry is deliberate.

| Gate           | Command          | What it enforces                                                                                                                      |
| -------------- | ---------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| Node           | `pnpm node:gate` | The pin above, in CI only                                                                                                             |
| Lint           | `pnpm lint`      | `ChangeDetectorRef` and web storage are **banned identifiers**; `OnPush` is mandatory; every template string carries an `i18n` marker |
| Tests          | `pnpm test`      | Components, stores, axe on every route and on every generated form, the pages against a recorded platform, and the conventions suite  |
| Build + budget | `pnpm build`     | The production build, then `scripts/bundle-budget.mjs`                                                                                |
| SSR isolation  | `pnpm test:ssr`  | `scripts/ssr-isolation.test.mjs`, run by `pnpm build` once the bundle exists                                                          |

### The performance budget

docs/plan/20 § Performance budget, "Enforced in CI, failing the build". `scripts/bundle-budget.mjs`
gzips the emitted files and compares real bytes rather than the builder's estimate:

| Metric                       | Budget   | Actual       |
| ---------------------------- | -------- | ------------ |
| Initial JS, gzipped          | < 250 KB | **191.1 KB** |
| Largest route chunk, gzipped | < 120 KB | **9.8 KB**   |

Measured by the script itself on 2026-09-15 at the pins § The Angular pin describes, with the access
page (#22) among the lazy chunks; the builder's own "estimated transfer size" column is smaller
(178.9 KB) and is not what the gate compares.

⚠ The script also fails when the build emits **no** lazy chunk at all, because that means the lazy
routes have been inlined and docs/plan/20's "Route-level code splitting is mandatory" has quietly
stopped being true. `angular.json` carries a raw-byte budget as a coarse first line of defence; the
gzip gate is the authoritative one, since gzip is what a CDN serves.

### SSR isolation

docs/plan/20 § SSR asks for the test by name and states the stakes: getting it wrong "leaks one
tenant's data to another through a CDN cache, which is the worst bug this document can prevent".

`scripts/ssr-isolation.test.mjs` boots the **built** server bundle and fires two concurrent requests
carrying different tenants, different session cookies and different bearer tokens. It asserts that
neither render carries the other's tenant, that the two documents are identical (so no request
identity reached the render at all), that no token or cookie appears in either, that
`Cache-Control` is `no-store, private` and `Vary` includes `Cookie`, and that no shipped browser
bundle writes to web storage.

It is a Node test against real HTTP rather than a Jest suite on purpose: the property is about the
deployed process's bytes and response headers, not about an Angular API call.

## What is not here, and what each needs first

M1 is the shell. Everything below is named in docs/plan/20 and deliberately absent.

| Not built                                             | What it needs before it can be                                                                                                                                                                                                                                                                                                     | Where it is specified                                             |
| ----------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| **Region, cluster, storage-class and subnet pickers** | Endpoints that list them. The form renderer exists (`libs/resource-forms`, over `generated/forms/{apiVersion}.json`, fetched at runtime and never imported) and renders those four widgets as inputs carrying the schema's own pattern and example until the API can answer what a picker would offer                              | docs/plan/20 § The shape that makes 100 resource types affordable |
| **A subscription or resource-group list**             | A collection route at tenant or subscription scope. #63 gave the scope API a `GET` and a `PUT` by id and nothing that enumerates, so the subscriptions page lists what the sign-in grants and the resource-groups page says "no list endpoint yet" and opens one by name                                                           | docs/plan/10 § Shape                                              |
| **Quota and usage, the cloud-terminal surface**       | Endpoints. Nothing in `openapi/{apiVersion}.json` serves quota or usage (docs/plan/22 is M2), and `Terminal/consoles`' `connect` answers a WebSocket address the portal has no `xterm.js` host for yet                                                                                                                             | #22                                                               |
| **A role-assignment list**                            | `GET {scope}/providers/CyberCloud.Authorization/roleAssignments` — the collection answers 400 today, and what is assigned at a scope lives only on `ICheckGrain`'s role-assignment view. The access page grants, checks and revokes by name and says so in its empty state; when the collection lands it loads into the same table | #86                                                               |
| **Cost analysis**                                     | The billing aggregates from docs/plan/22, plus forecast and budget models                                                                                                                                                                                                                                                          | docs/plan/20 § The pages that are not generated, 0.6 EM           |
| **Metrics explorer**                                  | A query builder over the hot-tier pre-aggregates (docs/plan/16), and dashboards to pin to                                                                                                                                                                                                                                          | 0.6 EM                                                            |
| **Log search**                                        | ClickHouse, and ⚠ a **server-side query cost preview** — docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan". The portal cannot estimate this itself                                                                                                                                                     | 0.6 EM                                                            |
| **Network topology**                                  | The VPC/subnet/peering graph from docs/plan/14. `@xui/node-graph` is the easy half; the data shape is the work                                                                                                                                                                                                                     | 0.5 EM                                                            |
| **`apps/admin`**                                      | The platform-scope API from docs/plan/06, and a separate auth scope. It is a **separate app on purpose** — "so that a bug in tenant-facing code cannot reach admin functionality and vice versa" — so it is not a route away                                                                                                       | docs/plan/20 § Admin app                                          |
| **Webmail**                                           | docs/plan/17, and counted there rather than here                                                                                                                                                                                                                                                                                   | docs/plan/20 § The pages that are not generated                   |

See [docs/plan/20](../docs/plan/20-portal.md) and [docs/plan/03 § portal](../docs/plan/03-repository-layout.md).
