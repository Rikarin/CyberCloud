# `portal/` — Angular 22 + xUI

A **pnpm workspace**, mirroring xUI's Nx conventions so the two feel like one codebase.

```
portal/
├── apps/portal/                     # the tenant-facing portal — Angular 22, zoneless, SSR
├── apps/admin/                      # platform admin — NOT BUILT, see § What is not here
├── libs/api/                        # GENERATED TypeScript client from OpenAPI — never hand-edited
├── libs/resource-forms/             # the schema → xUI form renderer (ADR-012), over generated/forms
├── libs/resource-forms-overrides/   # hand-written forms that replace the generated one, by type+version
├── libs/shell/                      # navigation, breadcrumbs, resource blades, the omnibar, sign-in
└── libs/charts/                     # chart views over @xui/echarts; ECharts itself is one deferred chunk
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
and fails in CI (`pnpm node:gate`, which `pnpm gates` runs first). `engineStrict` is deliberately
off in `pnpm-workspace.yaml` (it was in `.npmrc` until #87 found that pnpm 11 does not read it
there — § The Angular pin). The reasoning is that a wall in the developer's path and a wall in
CI's path have opposite costs: a blocked local install stops work over a version that will build
fine, while a drifted CI image silently produces artefacts nobody can reproduce. So the warning is
the nudge and the CI gate is the wall.

⚠ **That reasoning was tested, and the nudge half failed.** A session of portal work — a layout
fix, a class-coverage gate, two SSR suites, 73 jest tests — was measured on the host's Node 26 and
reported as if it were the pinned runtime. The warning had fired. It fired once, before the work,
minutes and thousands of lines of build output before the figures it applied to. The wall half
still holds and is not negotiable here: the dev host has **only** Node 26, so `engineStrict: true`
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

Two numbers, both read from xUI's `v3.0.0` tag, and one test that fails when either drifts.

**The framework — `@angular/core` and its siblings, `@angular/compiler-cli`, `@angular/cdk` — is
pinned to exactly `22.1.4`, the version xUI compiled `@xui/*@3.0.0` with.** That is the whole
rule: the portal runs what xUI is tested against (docs/plan/02 § ADR-017, "The portal does not
choose the Angular version; it follows xUI"), and `apps/portal/src/app/angular-pin.spec.ts` asserts
it — every framework entry in `package.json` is one exact version, and that version equals the
`version: "…"` stamp the Angular compiler leaves in every installed `@xui/*` bundle (237
declarations across 30 packages at 3.0.0, all `22.1.4`; xUI's `package.json` at the `v3.0.0` tag
pins `@angular/* 22.1.4` and `@angular/cdk 22.1.4` and says the same). The pin moves when that
stamp moves, which is on an xUI bump and at no other time.

**The tooling — `@angular/cli`, `@angular/build`, `@angular/ssr` — is pinned to exactly `22.1.6`,
the version the same `package.json` pins for xUI's own tooling, and it is a separate number on
purpose.** xUI does not keep its tooling in step with its framework — the tag pins `@angular/cli
22.1.6` and `@angular/ssr 22.1.6` next to a 22.1.4 framework — and the CLI could not have followed
the framework anyway (below). So the rule for the tooling is the framework's rule, read from the
same file: run what xUI runs. The test pins it as one exact version of its own. The three move
together because `@angular/build@22.1.6` peers `@angular/ssr` at `^22.1.6`; the framework is free
of them, because the same package peers `@angular/compiler-cli`, `localize` and `platform-server` at
`^22.0.0`.

⚠ **The test is there because nothing else catches a drift.** Angular's linker accepts a range of
compiler versions, so #26 took the 22.1.4-stamped 3.0.0 bundles with the framework still at `22.0.8`
and its gate was green — the linker was fine, the claim in this section was not, and nothing would
have said so had #87 not followed two and a half hours later. Moving Angular ahead of xUI also
builds. Either direction is a policy breach that the toolchain does not report, so the test does,
and its failure message names the package that moved and the version xUI stamps.

**Why neither number is the registry head.** On 2026-09-17 the head is `22.1.7` for the framework
and CDK and `22.1.8` for the tooling. xUI has not been built or tested against either; running ahead
of xUI would make the portal the first consumer to find whatever a point release changed, which is
the opposite of what following xUI is for. Every version between `22.0.8` and the pins was skipped
for the same reason.

⚠ **`@angular/cli` 22.1.0 through 22.1.4 fail `strictPeerDependencies` on the CLI's own tree, so
the framework's number was never available to the tooling.** Each of those releases depends on
`listr2@10.2.2` and on `@listr2/prompt-adapter-inquirer@4.2.4`, and the adapter peers `listr2:
"10.2.1"` — exact. Under strict peers the resolution is `ERR_PNPM_PEER_DEP_ISSUES` and the install
stops; measured at 22.1.4, then at every 22.1.x below it by reading the CLI's manifest (`npm view
@angular/cli@22.1.N dependencies`). 22.1.5 is the first release whose adapter and `listr2` agree, and
the pair is unchanged through the head: 22.1.6, 22.1.7 and 22.1.8 all carry `listr2 11.0.0` with
adapter `4.2.5`, whose peer is `listr2 11.0.0` (re-read 2026-09-17).

**What the move from 22.0.8 tooling cost, measured at the move (#87) and again here (#92).**
`@angular/build` 22.1.x is a different bundler: it builds on `vite@8.1.5` and `rolldown@1.2.0` where
22.0.8 built on `vite@7` and `rollup`, and the tree carries a second `rolldown@1.1.5` because that is
the one `vite@8.1.5` depends on. The lockfile lost 180 package versions and gained 95: 102 packages
left — the CLI's `pacote`/`sigstore`/`npm-registry-fetch` stack, `rollup` and its 25 platform
binaries, `algoliasearch` — and 40 entered, 35 of them `rolldown` and `oxc-parser` platform
bindings. No install script entered the tree (`pnpm ignored-builds`: "Automatically ignored builds
during installation: None", on both dates). At the move the portal's initial bundle was 730.46 kB
raw / 191.1 KB gzipped against 727.4 kB / 191.9 KB at 22.0.8 tooling — 3 kB more raw and 0.8 KB
less over the wire, the difference between two bundlers' output and not a regression. It is
741.71 kB / 195.3 KB now; the 11 kB between the two arrived with #88's sign-in flow and the rest of
#22, not with the bundler (§ The performance budget has the figure by part). The Node range is unchanged (`^22.22.3 ||
^24.15.0 || >=26.0.0` on `cli` and `build` at 22.1.6 and at the 22.1.8 head, as at 22.0.8), and a
fresh `pnpm install` at these pins under `strictPeerDependencies: true` resolves with no peer output
— "Already up to date" against the committed lockfile.

⚠ **`@angular/ssr` 22.1 strips `Forwarded` and `X-Forwarded-*` from every request it was not told
to trust and warns on stderr for each one — and since #92 both `server.ts` files decide, rather
than letting the engine decide and warn.** At the move the engine printed `Received "x-forwarded-for"
header but "trustProxyHeaders" was not set up to allow it` three times in every `pnpm build`,
because `scripts/ssr-identity.test.mjs` sent that header as a lure, and it would have printed once
per request behind any ingress that sets `X-Forwarded-For`, which is all of them. Each server now
passes `trustProxyHeaders` explicitly — the list in `NG_TRUST_PROXY_HEADERS`, the engine's own
variable, empty when unset — and a middleware removes every proxy header not on that list before
the engine sees it. Trusting none is the right default because the render never reads the request's
origin: the identity issuer is a `<meta>` in `index.html`, redirects are relative, and the shell is
the same document for every scheme and host. The day a deployment needs the engine to know its
public scheme or host, set the variable to exactly the headers the ingress in front sets and
validates (`x-forwarded-proto,x-forwarded-host`); a name outside `forwarded`/`x-forwarded-*` fails
the engine's constructor and the process does not start (`"x-real-ip" is not a valid proxy
header`). ⚠ A trusted `x-forwarded-host` is also checked against the engine's allowed hosts, and
that list is the sibling knob a deployment has to set — see the ingress row of § What is not here.
Both SSR gates pin the policy; § SSR isolation says how.

⚠ **`strictPeerDependencies` was not in force until #87, and every earlier "under strict peers"
claim on this page was made with it off.** The setting lived in `.npmrc`, and pnpm 11 — the version
this workspace has pinned since its first commit — reads only registry and auth settings from it:
"Only auth and registry settings are read from `.npmrc` files." (pnpm 11.0 release notes,
§ Configuration). `pnpm config get strict-peer-dependencies` answered `undefined`; the same
resolution that now fails printed `[WARN] Issues with peer dependencies found` and exited 0. The four
settings are in `pnpm-workspace.yaml` now, with the reasoning, and `.npmrc` is gone.

### How the pin got here

This section used to give a different reason, and the history is kept because it is the reason every
xUI bump re-measures rather than reuses.

At `@xui/*` 2.2.x, five packages — `panel-stack`, `popover`, `tooltip`, `breadcrumb` and
`overflow-list` (an earlier table, measured at 2.2.0, listed four) — peered `"@angular/common":
"22.0.8"` **exactly**, and `@xui/echarts` peered `"@angular/cdk": "22.0.6"`, exact and a different
version again. `@angular/common@22.0.8` peers `"@angular/core": "22.0.8"` exactly, so one exact peer
dragged the whole framework to a point release; with `@angular/*` at the then-head 22.1.1 the
install was recorded as failing — under a strict-peers setting that, it turned out, was never read,
so what actually stopped it is not on record. The pin was `22.0.8`/`22.0.6` because a peer said so.

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
the portal's pins satisfy them. **No exact `@angular/*` peer survives in any of the 23 packages.**
The xUI bump (#26) kept `22.0.8`/`22.0.6` because moving Angular is a separate change from moving
xUI; #87 made that change, and `pnpm install` under `strictPeerDependencies` — now actually on —
reported no unmet peer at a 22.1.4 framework with 22.1.6 tooling, as the table predicts.

**`@ng-icons/*` is pinned to `35.1.0`**, the head of the `35` range `@xui/*@3.0.0` peers on. The
registry head is `36.0.0`, which is out of range.

## Running it against the platform

`dotnet run --project src/Hosts/CyberCloud.AppHost` from the repository root starts both apps beside
the platform — see [src/Hosts/README.md](../src/Hosts/README.md) for the whole table. What makes
that work is two files and one rule:

- **`apps/portal/proxy.conf.json`** forwards `/api` to the gateway on `localhost:5100`, stripping the
  prefix, because `API_BASE_PATH` is `/api` and the gateway serves its routes at the root. It stands
  in for the API shim docs/plan/03 § `portal/` gives `CyberCloud.Portal.Host`, which does not exist.
  ⚠ The entry says `"ws": true`, and that is not decoration: the terminal pane opens
  `ws://localhost:4200/api/hubs/terminal?ticket=…` on the same prefix, and Vite forwards a WebSocket
  Upgrade only for an entry that asks (`ws: true`, or a `ws:` target) — every HTTP request would reach
  the gateway and the one socket would not, with no error on the server side to say so.
- **`apps/identity/proxy.conf.json`** forwards `/api`, `/connect` and `/.well-known` to the identity
  host on `localhost:5101`.
- ⚠ **The ports are pinned in `angular.json`'s `serve.options` and in `CyberCloudResources`, and
  `AppHostTopologyTests` reads the proxy files back and refuses a drift** — a proxy file naming a
  stale port is a portal that renders and cannot call anything, with the only symptom an
  `ECONNREFUSED` in the dev server's console. The same test refuses the `/api` entry without `ws`.

The apps run through Aspire's JavaScript hosting with `install: false` — run
`pnpm install --frozen-lockfile` here once first — and with whatever `node` is on `PATH`, which
§ Node above says has to be 24.

## Sign-in

docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token in
memory, refresh in an `HttpOnly` cookie scoped to the identity host … Access token never in
`localStorage`." `libs/shell/src/lib/auth` is that row, and each part is where it is for a reason:

| Part                            | What it does                                                                                                                                                                                                                                                                                                                                               |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IDENTITY_ISSUER`               | `<meta name="cyc-identity-issuer">` in `index.html` — `http://localhost:5101` on the dev run. One issuer for every tenant; the tenant rides in the token's `tid`                                                                                                                                                                                           |
| `AuthFlow.beginSignIn`          | Mints a PKCE pair (`pkce.ts`, checked against RFC 7636's appendix B vector), keeps `{state}.{verifier}.{returnTo}` in the `cyc-pkce` cookie — `Path=/auth/callback`, ten minutes — and leaves for `{issuer}/authorize` with `tenant=` from the `cyc-tenant` cookie when one is remembered                                                                  |
| `/auth/callback`                | The one route without `authGuard`. `AuthFlow.completeCallback` checks the state, POSTs the code and verifier to `{issuer}/token` as a raw cross-origin `fetch` with credentials — never `HttpClient`, never proxied — and `AuthSession.accept` puts the access token in `AccessTokenStore`, the person in the account menu, and the tenant in `cyc-tenant` |
| `TENANT_CONTEXT_SOURCE`         | The app's `PlatformTenantContextSource`: `GET /api/tenants/{tid}` and the subscriptions collection, into `TenantContextStore.loadFromToken`. The shell asks; the app answers, because the generated client is the app's                                                                                                                                    |
| `TokenRefresher`                | `grant_type=refresh_token&client_id=cyc-portal` with **no `refresh_token` field** — the host keeps it in `__Host-cyc-refresh` on its own origin. Armed at `exp − 60 s`, single-flight, and on a refusal it clears the store and begins a sign-in                                                                                                           |
| `authGuard`                     | Token in memory → pass; none → one refresh (a reload still holds the cookie) → pass; else leave for `/authorize` with the requested URL as the return path. On the server it passes and touches nothing                                                                                                                                                    |
| `accessTokenInterceptor`        | Bearer on same-origin calls only; on a 401, one refresh and one retry                                                                                                                                                                                                                                                                                      |
| The account menu (`ContextBar`) | `name`/`email` from the id_token — labels, never authority (`decodeJwtPayload`) — and "Sign out", which forgets the token and leaves for `{issuer}/logout`. The `cyc-tenant` hint stays                                                                                                                                                                    |

⚠ **Two cookies and no web storage.** The lint rule bans `localStorage` and `sessionStorage`
outright and `auth-flow.spec.ts` spies on both through the whole flow; a cookie is what is left, and
a path-scoped one is the better fit for the verifier anyway. Both are written with `Secure` — real
browsers accept that on `http://localhost`, jsdom does not, so the suites substitute
`MemoryCookieJar` from `@cybercloud/shell/testing` rather than relaxing the attribute.

## The pages, and what each one calls

Issue #22's M1 pages, each a lazy route over the generated client (`libs/api`) and the generated
form document (`generated/forms/{apiVersion}.json`, staged into `/forms/` by `scripts/sync-forms.mjs`
before every build and serve):

| Route                                                          | Page                 | Calls                                                                                                                                                                                                                                                                                  |
| -------------------------------------------------------------- | -------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `/subscriptions`                                               | list + create        | `GET /tenants/{t}/subscriptions` through `ScopeCollections` (a delegation to the generated `listSubscriptions`), then `createSubscription`                                                                                                                                             |
| `/subscriptions/{s}`                                           | subscription blade   | `getSubscription`                                                                                                                                                                                                                                                                      |
| `…/resourceGroups`                                             | list + create + open | `GET …/subscriptions/{s}/resourceGroups` through `ScopeCollections`, then `createResourceGroup`; open by name stays for a group the caller may act in but not enumerate                                                                                                                |
| `…/resourceGroups/{g}`                                         | resource group blade | `getResourceGroup`, plus a create and a list link per top-level type from the form document                                                                                                                                                                                            |
| `…/resourceGroups/{g}/resources?type=`                         | resource list        | `list{Type}` (#10), paged by `$skipToken`                                                                                                                                                                                                                                              |
| `…/resourceGroups/{g}/create/{ns}/{type}[/{child}]`            | create blade         | the generated form, then `createOrUpdate{Type}` → the operation view                                                                                                                                                                                                                   |
| `…/providers/{ns}/{type}/{name}`                               | resource blade       | `get{Type}`; delete is `delete{Type}` after the name is typed back → the operation view                                                                                                                                                                                                |
| `…/providers/{ns}/{type}/{name}/edit`                          | edit blade           | `get{Type}`, then a full `createOrUpdate{Type}` with the immutable fields locked and still sent                                                                                                                                                                                        |
| `/operations/{id}?then=`                                       | operation view       | `getOperation`, polled until terminal; every poll feeds `NotificationsStore`                                                                                                                                                                                                           |
| `/subscriptions/{s}/access`, `…/{g}/access`, `…/{name}/access` | access page          | `RoleAssignmentsApi` — `PUT`/`GET`/`DELETE {scope}/providers/CyberCloud.Authorization/roleAssignments/{name}` (#70), by hand; no list (#86)                                                                                                                                            |
| `…/resourceGroups/{g}/terminal?console=`                       | cloud shell          | `listCloudTerminal`, or the generated form and `createOrUpdateCloudTerminal` when the group has none; then `connectCloudTerminal`, `HubTicketsApi` (`POST /hubs/terminal/ticket`, by hand), the socket into an `xterm.js` pane, `terminateCloudTerminal` after a second click          |
| `/subscriptions/{s}/cost`, `…/{g}/cost?period=&groupBy=`       | cost analysis (#41)  | `CostManagementApi` — `POST {scope}/providers/CyberCloud.CostManagement/query` twice, by hand: the period `daily` for the chart and table, this month `by day` for the forecast; on a group, `listBudget` and `showStatusBudget` per budget, New and Edit through the generated blades |
| `/invoices`, `/invoices/{number}`                              | invoices (#41)       | `CostManagementApi` — `GET /tenants/{t}/providers/CyberCloud.CostManagement/invoices[/{number}]`, by hand                                                                                                                                                                              |

⚠ **The verb names are derived from the form's `title`**, exactly as `TypeScriptEmitter` derives
them from the type's display name, and `apps/portal/src/app/api/resource-verbs.spec.ts` asserts the
four verbs exist on `CyberCloudApi` for every type in the document. A hand-written table would be a
third copy of what two generated surfaces already say.

⚠ **The access page and the cost pages are the pages over a hand-written client, and the reason is
the address, not the page.** `CyberCloud.Authorization` is a reserved namespace the registry refuses,
so the emitters that read the registry never see
`{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}` — docs/plan/10 § Shape calls it
"#63's question asked a third time". The cost pages' two addresses are under
`CyberCloud.CostManagement`, reserved the same way (`apps/portal/src/app/api/cost-management.ts`).
`apps/portal/src/app/api/role-assignments.ts` builds the address for a tenant, a subscription, a
resource group or a resource, derives the name as `RoleAssignmentName` does
(`{role}-{principalType}-{principalId}`), and sends it through the same `HttpApiTransport` as the
generated client, so the token, the api-version and the error mapping are owned once. The day an
emitter learns the address, the three methods become delegations. The page grants, checks and
revokes; it cannot list, because the collection answers 400 (#86), and its table holds only what it
granted or checked in the visit, labelled as exactly that.

⚠ **The cloud shell is the second page with a hand-written call, and again the reason is the
address.** A hub is not a resource type, so no emitter sees `POST /hubs/{hub}/ticket`.
`apps/portal/src/app/api/hub-tickets.ts` mints the thirty-second, single-use ticket the gateway
redeems on the WebSocket upgrade and builds the socket address on this origin —
docs/plan/10 § SignalR — and its spec sabotages the property the ticket exists for: the bearer token
is never in a URL. `apps/portal/src/app/terminal/` holds the session driver (`connect` → ticket →
socket → `Attach`, bytes both ways, a fresh ticket on every reconnect) and the pane over `@xterm/xterm`,
both loaded with the terminal's own chunk and never on the server; `@microsoft/signalr` is the hub
client, opened with `skipNegotiation` because the ticket is spent by whichever request reaches the
gateway first. ⚠ The pane will show the hub refusing by name: docs/plan/19's session grain is not
built, and `charts/managed/cloud-shell/conformance.yaml § owed` carries what the grain will find
waiting on both sides of it.

⚠ **Every page renders a "no tenant" state until the sign-in has loaded one.** `TenantContextStore`
is filled by `AuthFlow.ensureContext` once a token is accepted — from the callback, or from the
reload path's refresh — and until then the pages say so rather than calling the API with no tenant.
`apps/portal/src/pages/pages.spec.ts` signs a tenant in and drives each page against a recorded
platform; `a11y.spec.ts` audits every route in the state before that.

## Gates

Every one of these fails the build rather than warning. `pnpm gates` runs them in order, and
`./build.sh Portal` runs `pnpm gates` — docs/plan/23 § Build, row `Portal`, and `build/Build.Portal.cs`,
which invokes this chain rather than restating it. Locally that target runs `pnpm verify` instead,
which is `gates` without the Node wall; see § Node above for why that asymmetry is deliberate.

| Gate           | Command          | What it enforces                                                                                                                                                                                |
| -------------- | ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Node           | `pnpm node:gate` | The pin above, in CI only                                                                                                                                                                       |
| Lint           | `pnpm lint`      | `ChangeDetectorRef` and web storage are **banned identifiers**; `OnPush` is mandatory; every template string carries an `i18n` marker                                                           |
| Tests          | `pnpm test`      | Components, stores, the sign-in flow against a fake `/token`, axe on every route and on every generated form, the pages against a recorded platform, the conventions suite, and the Angular pin |
| Build + budget | `pnpm build`     | The production build, then `scripts/bundle-budget.mjs`                                                                                                                                          |
| SSR isolation  | `pnpm test:ssr`  | `scripts/ssr-isolation.test.mjs`, run by `pnpm build` once the bundle exists                                                                                                                    |

### The performance budget

docs/plan/20 § Performance budget, "Enforced in CI, failing the build". `scripts/bundle-budget.mjs`
gzips the emitted files and compares real bytes rather than the builder's estimate:

| Metric                       | Budget   | Actual       |
| ---------------------------- | -------- | ------------ |
| Initial JS, gzipped          | < 250 KB | **195.3 KB** |
| Largest route chunk, gzipped | < 120 KB | **10.0 KB**  |

Measured by the script itself on 2026-09-17 at the pins § The Angular pin describes — `@angular/build`
22.1.6, the vite 8 and rolldown bundler — with the access page (#22) among the lazy chunks and the
sign-in flow (§ Sign-in) in the shell chunk — 4.5 KB of the initial set, measured against the same
build without it; the builder's own "estimated transfer size" column is smaller (182.8 KB) and is not
what the gate compares.

⚠ **`TENANT_CONTEXT_SOURCE` is provided through a dynamic import, and the budget is why.** Its
implementation reaches `PlatformApi`, which is the whole generated client, and a `useClass` provider
in `app.config.ts` put the client's 150 methods in the initial bundle — 8 KB gzipped, measured — where
`platform-api.ts` promises they are not. The factory imports the class on the first `load` instead.

⚠ The script also fails when the build emits **no** lazy chunk at all, because that means the lazy
routes have been inlined and docs/plan/20's "Route-level code splitting is mandatory" has quietly
stopped being true. `angular.json` carries a raw-byte budget as a coarse first line of defence; the
gzip gate is the authoritative one, since gzip is what a CDN serves.

⚠ **The raw-byte budget's warning tier is set where it does not fire on every build.** It has two
thresholds per app, `maximumWarning` and `maximumError`, and the sentence at the top of § Gates is
true only of the second. The first had been left behind by the bundle: at 700 kB for the portal and
340 kB for identity it fired on every production build — master's portal was 723.74 kB, 730.46 kB
/ 356.30 kB when #87 moved the tooling, and 741.71 kB / 360.87 kB on 2026-09-17 — and a warning that
fires on every run is read by nobody. #87 moved the tiers to 800 kB / 380 kB, which is a warning
again: roughly 58 kB and 19 kB of headroom before it speaks, and the error tiers (900 kB / 420 kB)
are where they were. Raise the warning when the bundle grows for a reason; a warning that is on all
the time is the same as none.

### SSR isolation

docs/plan/20 § SSR asks for the test by name and states the stakes: getting it wrong "leaks one
tenant's data to another through a CDN cache, which is the worst bug this document can prevent".

`scripts/ssr-isolation.test.mjs` boots the **built** server bundle and fires two concurrent requests
carrying different tenants, different session cookies and different bearer tokens. It asserts that
neither render carries the other's tenant, that the two documents are identical (so no request
identity reached the render at all), that no token or cookie appears in either, that
`Cache-Control` is `no-store, private` and `Vary` includes `Cookie`, and that no shipped browser
bundle writes to web storage. A second pair of requests renders `/auth/callback` with an
authorization code in the query and a PKCE cookie on the request, and asserts the server rendered
its "signing in" state without the code, the verifier or the tenant reaching the output — the
exchange is the browser's, after hydration.

**The proxy-header policy is pinned here too**, in both gates, through `scripts/ssr-proxy-headers.mjs`.
A third request carries every `Forwarded` and `X-Forwarded-*` header the engine knows, each with a
value that would be visible if it were honoured — `evil.example` as the host, `/evil-prefix` as the
prefix, a client address — and must come back as the same document byte for byte, with none of the
values in it and, because the gate hooks `console.warn` in the process it imports the bundle into,
with the engine having warned about nothing. Then a child process boots the bundle with
`NG_TRUST_PROXY_HEADERS=x-forwarded-host` and asks for a page as `evil.example`: a trusted forwarded
host is checked against the allowed hosts, so the answer must be 400, which is the proof that the
list reached the engine. Sabotaged — the middleware's `delete` removed and the engine handed an
empty list — the identity gate reports twelve warnings and `rendered as "evil.example" (HTTP 200)`.

It is a Node test against real HTTP rather than a Jest suite on purpose: the property is about the
deployed process's bytes and response headers, not about an Angular API call.

## What is not here, and what each needs first

M1 is the shell. Everything below is named in docs/plan/20 and deliberately absent.

| Not built                                             | What it needs before it can be                                                                                                                                                                                                                                                                                                                                                                                            | Where it is specified                                             |
| ----------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| **Region, cluster, storage-class and subnet pickers** | Endpoints that list them. The form renderer exists (`libs/resource-forms`, over `generated/forms/{apiVersion}.json`, fetched at runtime and never imported) and renders those four widgets as inputs carrying the schema's own pattern and example until the API can answer what a picker would offer                                                                                                                     | docs/plan/20 § The shape that makes 100 resource types affordable |
| **`/userinfo`**                                       | The identity host does not map it yet. The account menu reads `name` and `email` from the id_token the code exchange returns, so a name changed after sign-in shows on the next sign-in rather than the next refresh                                                                                                                                                                                                      | docs/plan/11 § Hosts                                              |
| **Quota and usage, the cloud-terminal surface**       | Endpoints. Nothing in `openapi/{apiVersion}.json` serves quota or usage (docs/plan/22 is M2), and `Terminal/consoles`' `connect` answers a WebSocket address the portal has no `xterm.js` host for yet                                                                                                                                                                                                                    | #22                                                               |
| **A role-assignment list**                            | `GET {scope}/providers/CyberCloud.Authorization/roleAssignments` — the collection answers 400 today, and what is assigned at a scope lives only on `ICheckGrain`'s role-assignment view. The access page grants, checks and revokes by name and says so in its empty state; when the collection lands it loads into the same table                                                                                        | #86                                                               |
| **Principal validation on a grant**                   | A directory the platform can ask. It accepts any well-formed id of a closed principal type and writes the tuple, so the access page checks the id's shape (`RelationNaming.IdPattern`) and no more, and its field hint says the id is the directory's — a typo grants something nobody can use                                                                                                                            | #86, the third point                                              |
| **The effective-permissions explorer**                | An HTTP address for a check. docs/plan/20 § Information architecture names it beside role assignments — "why does this user have access" — and `ICheckGrain.CheckAsync` answers yes or no, with no path and no expand tree behind it; `openapi/{apiVersion}.json` serves neither. The access page's Check is a `GET` of one assignment by name, not this                                                                  | docs/plan/20 § Information architecture, docs/plan/07 § Check     |
| **Cost analysis**                                     | The billing aggregates from docs/plan/22, plus forecast and budget models                                                                                                                                                                                                                                                                                                                                                 | docs/plan/20 § The pages that are not generated, 0.6 EM           |
| **Metrics explorer**                                  | A query builder over the hot-tier pre-aggregates (docs/plan/16), and dashboards to pin to                                                                                                                                                                                                                                                                                                                                 | 0.6 EM                                                            |
| **Log search**                                        | ClickHouse, and ⚠ a **server-side query cost preview** — docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan". The portal cannot estimate this itself                                                                                                                                                                                                                                            | 0.6 EM                                                            |
| **Network topology**                                  | The VPC/subnet/peering graph from docs/plan/14. `@xui/node-graph` is the easy half; the data shape is the work                                                                                                                                                                                                                                                                                                            | 0.5 EM                                                            |
| **`apps/admin`**                                      | The platform-scope API from docs/plan/06, and a separate auth scope. It is a **separate app on purpose** — "so that a bug in tenant-facing code cannot reach admin functionality and vice versa" — so it is not a route away                                                                                                                                                                                              | docs/plan/20 § Admin app                                          |
| **Webmail**                                           | docs/plan/17, and counted there rather than here                                                                                                                                                                                                                                                                                                                                                                          | docs/plan/20 § The pages that are not generated                   |
| **A deployment behind an ingress**                    | Two runtime settings the built bundle does not carry. `angular.json` → `security.allowedHosts` is `127.0.0.1` and `localhost`, so `@angular/ssr` answers 400 to any other `Host`; the deployment sets `NG_ALLOWED_HOSTS` to the names it serves. And if the ingress terminates TLS or serves a prefix, `NG_TRUST_PROXY_HEADERS` names exactly the headers it sets (§ The Angular pin). Neither has a chart to live in yet | docs/plan/20 § SSR, docs/plan/11 § Hosts                          |

See [docs/plan/20](../docs/plan/20-portal.md) and [docs/plan/03 § portal](../docs/plan/03-repository-layout.md).
