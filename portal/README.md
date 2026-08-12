# `portal/` — Angular 22 + xUI

A **pnpm workspace**, mirroring xUI's Nx conventions so the two feel like one codebase.

```
portal/
├── apps/portal/                     # the tenant-facing portal — Angular 22, zoneless, SSR
├── apps/admin/                      # platform admin — NOT BUILT, see § What is not here
├── libs/api/                        # GENERATED TypeScript client from OpenAPI — never hand-edited
├── libs/resource-forms/             # the schema → xUI form renderer (ADR-012) — stub, interface only
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
portal work starts, or local and CI will silently differ". Four inputs decided it:

| Input | Value | Effect |
|---|---|---|
| Angular 22's own `engines.node` | `^22.22.3 \|\| ^24.15.0 \|\| >=26.0.0` | 22, 24 and 26 are all permitted — Angular does not decide it either |
| Node release state, 2026-08 | 22 is **Maintenance**, 24 is **Active LTS**, 26 is **Current** | 24 is the only one that is both supported and LTS today |
| xUI's own pin | `engines.node: 24.x`, `.node-version: 24` | The portal is xUI's largest consumer; matching keeps the two workspaces one codebase |
| The dev host | 26.5.0 | Needs to keep working, so the pin cannot be a wall |

So: **24**, which corrects docs/plan/02's "Node 22 LTS" (now maintenance-only) and matches xUI
rather than diverging from it. Node 26 was rejected on one ground: it does not become LTS until
2026-10-20, and a platform's portal should not build on a Current release.

⚠ **How the pin is enforced, and why the two halves differ.** `scripts/check-node.mjs` warns
locally and fails in CI (`pnpm node:gate`, which `pnpm gates` runs first). `engine-strict` is
deliberately off in `.npmrc`. The reasoning is that a wall in the developer's path and a wall in
CI's path have opposite costs: a blocked local install stops work over a version that will build
fine, while a drifted CI image silently produces artefacts nobody can reproduce. So the warning is
the nudge and the CI gate is the wall. **The CI image must be Node 24** — that is the half of this
decision that lives outside this repo.

## The Angular pin

**`@angular/*` is pinned to exactly `22.0.8`, and `@angular/cdk` to exactly `22.0.6`.**

docs/plan/02 § ADR-017 records the peer range as `@angular/*: 22` — "a **major range**" — and
concludes the portal "is free within Angular 22.x". ⚠ **That is not true of every `@xui/*` package
at 2.2.0**, and the exceptions are load-bearing:

| Package | Peer | Note |
|---|---|---|
| `@xui/panel-stack` | `"@angular/common": "22.0.8"` | **Exact**, not `22` |
| `@xui/popover` | `"@angular/common": "22.0.8"` | Exact |
| `@xui/tooltip` | `"@angular/common": "22.0.8"` | Exact |
| `@xui/breadcrumb` | `"@angular/common": "22.0.8"` | Exact |
| `@xui/echarts` | `"@angular/cdk": "22.0.6"` | Exact, and a *different* version again |
| everything else | `"@angular/*": "22"` | The major range ADR-017 describes |

`@angular/common@22.0.8` in turn peers `"@angular/core": "22.0.8"` exactly, so the whole framework
follows. Four of those six packages are in the M1 shell, so this is not a corner case — with
`@angular/*` at the 22.1.1 head and `strict-peer-dependencies=true`, `pnpm install` fails.

Pinning to 22.0.8/22.0.6 satisfies both the exact pins and the major ranges, and it is the version
xUI's own checkout pins, so the portal is running what xUI is tested against. **`@ng-icons/*` is
pinned to `34.0.0`** for the same reason: `@xui/*` peers `34` while the registry head is `35.0.1`.

The build tooling (`@angular/cli`, `@angular/build`, `@angular/ssr`) is pinned to 22.0.8 as well.
It versions independently of the framework — the tooling head is 22.1.3 — but keeping the two in
step means one number to reason about.

## Gates

Every one of these fails the build rather than warning. `pnpm gates` runs them in order, and
`./build.sh Portal` runs `pnpm gates` — docs/plan/23 § Build, row `Portal`, and `build/Build.Portal.cs`,
which invokes this chain rather than restating it. Locally that target runs `pnpm verify` instead,
which is `gates` without the Node wall; see § Node above for why that asymmetry is deliberate.

| Gate | Command | What it enforces |
|---|---|---|
| Node | `pnpm node:gate` | The pin above, in CI only |
| Lint | `pnpm lint` | `ChangeDetectorRef` and web storage are **banned identifiers**; `OnPush` is mandatory; every template string carries an `i18n` marker |
| Tests | `pnpm test` | Components, stores, axe on every route, and the conventions suite |
| Build + budget | `pnpm build` | The production build, then `scripts/bundle-budget.mjs` |
| SSR isolation | `pnpm test:ssr` | `scripts/ssr-isolation.test.mjs`, run by `pnpm build` once the bundle exists |

### The performance budget

docs/plan/20 § Performance budget, "Enforced in CI, failing the build". `scripts/bundle-budget.mjs`
gzips the emitted files and compares real bytes rather than the builder's estimate:

| Metric | Budget | Actual |
|---|---|---|
| Initial JS, gzipped | < 250 KB | **178.6 KB** |
| Largest route chunk, gzipped | < 120 KB | **0.5 KB** |

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

| Not built | What it needs before it can be | Where it is specified |
|---|---|---|
| **The form renderer** | The schema emitter's output contract — see the header of `libs/resource-forms/src/index.ts` for the six things it needs, of which the load-bearing one is that schemas are **fetched at runtime, never imported**, or 100 resource types land in the bundle | docs/plan/20 § The shape that makes 100 resource types affordable |
| **Cost analysis** | The billing aggregates from docs/plan/22, plus forecast and budget models | docs/plan/20 § The pages that are not generated, 0.6 EM |
| **Metrics explorer** | A query builder over the hot-tier pre-aggregates (docs/plan/16), and dashboards to pin to | 0.6 EM |
| **Log search** | ClickHouse, and ⚠ a **server-side query cost preview** — docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan". The portal cannot estimate this itself | 0.6 EM |
| **Network topology** | The VPC/subnet/peering graph from docs/plan/14. `@xui/node-graph` is the easy half; the data shape is the work | 0.5 EM |
| **`apps/admin`** | The platform-scope API from docs/plan/06, and a separate auth scope. It is a **separate app on purpose** — "so that a bug in tenant-facing code cannot reach admin functionality and vice versa" — so it is not a route away | docs/plan/20 § Admin app |
| **Webmail** | docs/plan/17, and counted there rather than here | docs/plan/20 § The pages that are not generated |

See [docs/plan/20](../docs/plan/20-portal.md) and [docs/plan/03 § portal](../docs/plan/03-repository-layout.md).
