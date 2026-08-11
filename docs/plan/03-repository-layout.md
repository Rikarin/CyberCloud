# 03 — Repository Layout

One repository. The alternative — a repo per provider — is tempting and wrong at this stage: the
provider contract changes weekly for the first year, and a cross-repo contract change costs a day
each time.

## Top level

```
CyberCloud/
├── .config/dotnet-tools.json     # nuke, dotnet-ef, dotnet-counters, dotnet-trace, dotnet-coverage
├── .github/workflows/            # ci.yml, release.yml, charts.yml, e2e-nightly.yml, chaos-nightly.yml
├── build/                        # Nuke — the single entry point for every build action
│   ├── _build.csproj
│   ├── Build.cs                  # partial: target graph
│   ├── Build.Compile.cs
│   ├── Build.Test.cs
│   ├── Build.Generate.cs         # provider registry → OpenAPI → CLI → SDK → portal forms (ADR-012)
│   ├── Build.Charts.cs           # helm lint/package/push, values.schema.json generation
│   ├── Build.Images.cs           # container images, SBOM, cosign signatures
│   ├── Build.Architecture.cs     # the gates in 00 § Non-negotiables
│   ├── Build.Licence.cs          # ADR-011 scan over charts + images
│   ├── Build.Portal.cs           # pnpm install/lint/test/build, performance budget, axe
│   ├── Build.E2E.cs              # ─┐
│   ├── Build.Chaos.cs            #  ├ against a real deployment; nightly and weekly, not per-PR
│   ├── Build.Load.cs             # ─┘
│   └── Build.Publish.cs          # NuGet, npm, charts, `cyc` binaries per RID
├── src/                          # ── all .NET ──
├── charts/                       # ── Helm charts we own or have forked ──
├── portal/                       # ── Angular 22 + xUI ──
├── cli/                          # ── `cyc` — .NET, but kept out of src/ because it ships separately ──
├── deploy/                       # ── how Cyber Cloud itself is installed ──
├── openapi/                      # ── generated, checked in, diffed (ADR-012, 21 § OpenAPI) ──
│   ├── index.json                # the api-version index; always written, even when empty
│   └── {yyyy-MM-dd}.json         # one document per api-version
├── test/                         # ── cross-cutting: e2e, conformance, chaos, load ──
├── docs/
│   ├── plan/                     # this directory — the design record
│   ├── adr/                      # ADRs promoted out of 02 as they accumulate
│   ├── overview.md               # the state, not the design — reconciled against the code
│   └── guide/                    # user-facing docs, built into the portal's docs site
├── references/                   # read-only, not built, not restored
│   ├── cozystack/                # ADR-010 — the operator survey lives here as a grep target
│   ├── orleans-multitenant/      # ADR-002
│   ├── malware-multiscan/        # github.com/Rikarin/MalwareMultiScan — design reference (18)
│   └── survival/                 # symlink → ~/Projects/Survival/Server — the Orleans reference
├── Directory.Build.props / .targets
├── Directory.Packages.props      # CPM — every version pinned (02)
├── global.json                   # SDK pin: 10.0.100
├── CyberCloud.slnx
├── CyberCloud.Core.slnf          # filter: core + tenancy + authorization + kubernetes  (fast load)
├── CyberCloud.Providers.slnf
└── CyberCloud.Hosts.slnf
```

`build/` is one partial per Nuke target, named after it, and the list above is therefore the list in
[23 § Build](23-build-ci-and-testing.md) — if the two ever disagree, 23 is right and a target has
gone missing here.

`references/` is excluded from every glob and never restored. It exists so "how did Cozystack wire
CloudNativePG" is a `grep` away rather than a browser tab away.

`openapi/` is the one directory of generated files that is **tracked**. It is not under `artifacts/`
precisely because [21 § OpenAPI](21-cli-and-sdks.md) makes the document *"a build artifact that is
**diffed**"* — and `artifacts/` is gitignored, so a file git cannot see is a file no diff can fail
on. `./build.sh Generate` writes it; the **Generated surfaces** and **OpenAPI compatibility** gates
in [23](23-build-ci-and-testing.md) read it.

## `src/` — the .NET tree

Naming: **folder name == assembly name == root namespace**, `CyberCloud.` prefix on everything. Tests
are siblings (ADR-018) and are omitted below for readability — assume `X.Tests` next to every `X`.

### Foundation

```
src/
├── CyberCloud.Core/                     # ids, Result<T>, error codes, clock, GrainKeys, ResourceId
├── CyberCloud.Core.Contracts/           # wire types shared by grains, gateway, SDK — [GenerateSerializer]
├── CyberCloud.Tenancy/                  # tenant/subscription/resource-group grains, the tenant directory
├── CyberCloud.Tenancy.Contracts/
├── CyberCloud.Authorization/            # ReBAC engine — schema, tuples, check, expand, list (07)
├── CyberCloud.Authorization.Contracts/
├── CyberCloud.Kubernetes/               # connections, command builder, informers, SSA (09)
├── CyberCloud.Kubernetes.Charts/        # helm rendering + HelmRelease-free direct apply
├── CyberCloud.ResourceManager/          # provider registry, LRO, reconcile scheduler, locks, tags (08)
├── CyberCloud.ResourceManager.Contracts/
├── CyberCloud.ResourceManager.Generator/ # ⚠ Exe, never deployed — ADR-012's generation step
├── CyberCloud.Metering/                 # usage events, aggregation, quota enforcement (22)
├── CyberCloud.Billing/                  # rating, ledger, invoices (22)
├── CyberCloud.Telemetry/                # our own OTel wiring + the ingest path for tenants (16)
├── CyberCloud.ServiceDefaults/          # AddServiceDefaults, health checks, Orleans host builders
└── CyberCloud.Analyzers/                # ⚠ netstandard2.0 — CC1001..CC1007, the compile-time half of 00
```

⚠ **`CyberCloud.Analyzers` is the one project in the repository that is not `net10.0`**, and it is a
documented exception to [02 § Platform baseline](02-technology-decisions.md)'s single-TFM rule rather
than drift. A Roslyn analyzer is loaded by the *compiler*, which may be running on .NET Framework
(Visual Studio's design-time build) or on .NET (`dotnet build`); `netstandard2.0` is the only target
both can load, and `Microsoft.CodeAnalysis.Analyzers`' `RS1041` enforces it. Re-checked against the
SDK in use rather than assumed: `Microsoft.CodeAnalysis.CSharp` 5.6.0 — the version of the compiler
SDK 10.0.302 ships — still publishes a `netstandard2.0` lib.

⚠ **Nothing *references* it in the ordinary sense.** The projects it polices name it with
`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`, so it never reaches their compile line,
their output directory or their assembly metadata — which is why
`AssemblyGraphTests.CoreReferencesNothingButTheSharedFramework` still passes with `CyberCloud.Core`
referencing it. **Adding that four-line `ItemGroup` is what puts a new assembly under the rules**;
there is no automatic wiring, deliberately, because a repository-wide analyzer injection in
`Directory.Build.targets` would also try to make the analyzer analyse itself.

⚠ **`CyberCloud.ResourceManager.Generator` is the one project under `src/` that is not part of the
product.** It is an `Exe` that `./build.sh Generate` runs: it loads the provider assemblies, runs each
`Describe`, and emits `openapi/`. It lives under `src/` rather than under `build/` because it has to
reference `CyberCloud.ResourceManager` for real — [08 § The provider registry](08-resource-manager.md)
requires the emitters to read *the same object* the write path validates against, which means running
a provider's code, which the Nuke build host deliberately never does (`build/ArchitectureFacts.cs`
reads metadata rather than loading assemblies, for exactly that reason). It ships nowhere.

`CyberCloud.ServiceDefaults` is the direct descendant of Survival's — `OrleansApplication.CreateServer`
/ `CreateClient`, health checks, Serilog, OTel. It is copied in shape, not in code, because the
membership and storage wiring differ (ADR-003, ADR-004).

**The `.Contracts` split is not ceremony.** Grain interfaces and wire types go in `*.Contracts`; the
gateway, the CLI and the tests reference only those. A provider implementation assembly is referenced
by exactly one host. This is what makes a rolling silo upgrade possible: contracts change under
`[Alias]` discipline, implementations change freely.

### Providers

One folder per resource provider namespace. Each is an ABP module (`[DependsOn]`), each registers its
resource types into `CyberCloud.ResourceManager`, each is independently testable against a
`TestCluster` plus a `k3s` container.

```
src/Providers/
├── CyberCloud.Providers.Platform/          # subscriptions, resource groups, tenants-as-resources
├── CyberCloud.Providers.Identity/          # users, groups, service principals, apps (11)
├── CyberCloud.Providers.ContainerService/  # managedClusters, agentPools (09, 13)
├── CyberCloud.Providers.Compute/           # virtualMachines, scaleSets, disks, images (13)
├── CyberCloud.Providers.ContainerInstance/ # containerGroups (13)
├── CyberCloud.Providers.ContainerRegistry/ # registries, artifact feeds (13)
├── CyberCloud.Providers.Network/           # vnets, subnets, dnsZones, loadBalancers, vpnGateways (14)
├── CyberCloud.Providers.Storage/           # accounts, blob (S3), fileShares (15)
├── CyberCloud.Providers.Data/              # postgres, valkey, mongo, clickhouse, opensearch, qdrant (12)
├── CyberCloud.Providers.Messaging/         # nats, kafka, rabbitmq (12)
├── CyberCloud.Providers.KeyVault/          # vaults, secrets, keys, certificates (18)
├── CyberCloud.Providers.Security/          # scanners, assessments (18)
├── CyberCloud.Providers.Monitor/           # workspaces, collectors, alerts, grafanas (16)
├── CyberCloud.Providers.Communication/     # sms, whatsapp, email-send, chat (17)
├── CyberCloud.Providers.Mail/              # domains, mailboxes — the managed mail server (17)
├── CyberCloud.Providers.Terminal/          # cloud shell consoles (19)
└── CyberCloud.Providers.DesktopVirtualization/  # virtual desktops (19)
```

Each provider folder has the same five projects, and the sameness is the point — a new provider is
`dotnet new cc-provider`:

```
CyberCloud.Providers.Data/
├── CyberCloud.Providers.Data.Contracts/   # grain interfaces, resource models, the JSON Schemas
├── CyberCloud.Providers.Data/             # grains, reconcilers, the chart bindings
├── CyberCloud.Providers.Data.Application/ # ABP application services the gateway routes to
├── CyberCloud.Providers.Data.Tests/       # TestCluster + k3s
└── CyberCloud.Providers.Data.Conformance/ # the shared provider conformance suite, parameterised
```

**The conformance suite is what makes the catalogue safe to grow.** It is one xUnit theory that every
provider must pass: create → 202 → poll → Succeeded → read back → tag → lock → delete → gone;
create with tenant B's ids → 404; delete while an operation is running → 409; reconcile after a
manual cluster mutation → drift corrected; kill the silo mid-create → resource still converges. A
provider is not registered in the platform bundle until it passes.

### Hosts

```
src/Hosts/
├── CyberCloud.Silo.Host/          # the Orleans silo — loads every provider module
├── CyberCloud.Gateway.Host/       # REST + SignalR; Orleans *client* (10)
├── CyberCloud.Identity.Host/      # OIDC endpoints, cookies, sign-in/sign-up pages (11)
├── CyberCloud.Portal.Host/        # Angular SSR node process is separate; this serves the API shim + static
├── CyberCloud.Ingest.Host/        # OTLP + metrics ingest — high volume, separate scaling (16)
├── CyberCloud.Worker.Host/        # reconcile workers, informer bridges, billing rollups
├── CyberCloud.Admin.Host/         # platform-admin UI backend (06 § Platform admin)
└── CyberCloud.AppHost/            # Aspire — local development only (ADR-014)
```

⚠ **Why the silo and the gateway are separate processes.** Survival co-hosts them (`CreateServer` for
the gateway) and that is right for a game where the gateway *is* the load. Here the gateway is
I/O-bound and scales with request rate; the silo is memory-bound and scales with resident grains; and
the ingest host scales with telemetry volume, which is two orders of magnitude larger than both.
Co-hosting means one of the three is always the wrong size. The gateway is therefore an Orleans
*client* (`CreateClient`), which also means a gateway deploy does not move grains.

⚠ **The ingest host is not an Orleans client at all.** It writes straight to NATS and ClickHouse.
Putting a million spans per second through a grain call is the one design mistake in this shape that
would be expensive to undo, so it is excluded by process boundary rather than by discipline.

## `charts/`

```
charts/
├── platform/               # Cyber Cloud itself: silo, gateway, identity, ingest, worker, portal
├── bundle/                 # what we install into a managed cluster: operators, CNI, CSI, monitoring
├── managed/                # one chart per managed service — the catalogue
│   ├── postgres/
│   │   ├── Chart.yaml
│   │   ├── values.yaml           # annotated (ADR-010) — the schema source
│   │   ├── values.schema.json    # GENERATED — checked in, diffed in CI
│   │   ├── templates/
│   │   ├── SOURCE                # upstream repo + commit, if forked
│   │   └── conformance.yaml      # what the conformance suite asserts for this type
│   ├── valkey/ … clickhouse/ … kafka/ … harbor/ … seaweedfs/ …
└── tenant-cluster/         # Cluster API + Kamaji + KubeVirt templates for an in-house cluster (09)
```

The annotated `values.yaml` is the **single description of a managed service's configuration surface**.
`Build.Charts` generates `values.schema.json` from it; `Build.Generate` turns that into the resource
type's OpenAPI body, the CLI flags, the SDK model and the portal form. A chart whose generated schema
differs from the checked-in one fails CI.

## `portal/`

A pnpm workspace, mirroring xUI's Nx conventions so the two feel like one codebase.

```
portal/
├── apps/portal/            # the tenant-facing portal — Angular 22, zoneless, SSR
├── apps/admin/             # platform admin — same stack, separate app, separate auth scope
├── libs/api/               # GENERATED TypeScript client from OpenAPI — never hand-edited
├── libs/resource-forms/    # the schema → xUI form renderer (ADR-012)
├── libs/resource-forms-overrides/  # hand-written forms that replace the generated one, by type+version
├── libs/shell/             # navigation, breadcrumbs, resource blades, the omnibar
└── libs/charts/            # metric/log views over @xui/echarts
```

## `deploy/`

```
deploy/
├── bootstrap/          # what you run on the FIRST cluster, by hand, once
├── platform/           # helmfile/kustomize for the platform chart per environment
└── managed-cluster/    # the bundle applied to a cluster the platform adopts or creates
```

The bootstrap directory is the answer to "the platform manages clusters, but who manages the
platform's cluster" — and the brief already settled it: the platform is installed on an existing
cluster by hand, manages a second one, and moves onto it once that is boring. `deploy/bootstrap/`
is that hand-installation, kept honest by being the same thing CI uses to stand up e2e.

## `test/`

```
test/
├── CyberCloud.E2E/             # drives the public REST API against a real deployment
├── CyberCloud.Conformance/     # the shared provider suite (parameterised, referenced by providers)
├── CyberCloud.Chaos/           # silo kills, Redis flush, cluster-connection loss, network partition
├── CyberCloud.Load/            # the 00 § quality-bar numbers, as a gate
└── CyberCloud.Isolation/       # the cross-tenant suite: every provider, every verb, wrong tenant → 404
```

`CyberCloud.Isolation` deserves its own project rather than living inside each provider's tests,
because it is the one suite that must be written by someone who is *trying to break in*, and mixing
it with a provider's happy-path tests dilutes that intent.

## Assembly graph rules

Enforced by `Build.Architecture`, failing the build on violation:

1. `CyberCloud.Core` references no Orleans hosting, no `KubernetesClient`, no ABP application layer.
2. No `Providers.*` assembly references another `Providers.*` assembly — not even `.Contracts`.
   Cross-provider references go through `CyberCloud.ResourceManager` by resource id.
3. No assembly above `CyberCloud.Kubernetes` references `k8s.Models`.
4. Nothing references a `*.Application` assembly except its own host.
5. The gateway references no provider *implementation* assembly, only `.Contracts` and `.Application`.
6. `portal/libs/api` has no hand-written files; the generator owns the directory.
