# `src/Providers/` — one folder per resource provider namespace

Each provider is an ABP module (`[DependsOn]`), registers its resource types into
`CyberCloud.ResourceManager`, and is independently testable against a `TestCluster` plus a `k3s`
container.

## The five projects, and the sameness is the point

```
CyberCloud.Providers.Data/
├── CyberCloud.Providers.Data.Contracts/   # grain interfaces, resource models, the JSON Schemas
├── CyberCloud.Providers.Data/             # grains, reconcilers, the chart bindings
├── CyberCloud.Providers.Data.Application/ # ABP application services the gateway routes to
├── CyberCloud.Providers.Data.Tests/       # TestCluster + k3s
└── CyberCloud.Providers.Data.Conformance/ # the shared provider conformance suite, parameterised
```

A new provider should be `dotnet new cc-provider`, not a copy-paste.

## What exists

`CyberCloud.Providers.Sample` — `CyberCloud.Sample/widgets`, one resource type reconciled to a
ConfigMap. It is [24 § Phase 1](../../docs/plan/24-roadmap.md)'s exit criterion 1 and
[25 § R1](../../docs/plan/25-risks-and-open-questions.md)'s instrument, and it is **deliberately
trivial**: its whole job is to make the platform's friction visible. Anything clever added to it is
measurement lost.

`CyberCloud.Providers.DBforPostgreSQL` — `CyberCloud.DBforPostgreSQL/servers` on CloudNativePG,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md). **The first provider that is a
feature**, and therefore the first measurement of the platform that means anything: the sample was
built to be frictionless, so only a real one can report friction.

### What the second provider measured

Two things the sample's shape suggested and could not establish, now established:

- **It has no grain and no durable state either.** A managed PostgreSQL server with backup, pooling,
  extensions, a sizing preset and a secret-returning action still needs no grain of its own —
  `ResourceGrain` holds the desired state. The sample's absence of a grain could be read as a
  consequence of being trivial. This one cannot.
- **Its `.Application` project also holds the ABP module and nothing else.** Every operation on a
  server — PUT, GET, DELETE, POST `listKeys` — is a generic resource-manager verb the gateway routes
  from the registry (ADR-012). That is evidence for the registry-as-routing-source decision rather
  than a restatement of it.

Two things it found that the sample could not:

- **The chart↔registry pair has no build gate.** ADR-012's fifth surface is marked "Not built" and
  ADR-010 says the overlap "becomes 26 rows wide the moment the Postgres provider lands". It has;
  `Build.Charts` still never opens a registry. `ChartRegistryPairTests` compares the rows as a
  provider test instead, and should be deleted by whoever builds the emitter.

  > ⚠ **CORRECTED 2026-08-12, on both counts.** The gate exists: `Build.Charts` calls
  > `RunGenerator(write: true, charts: true)`, which drives `ChartSurfaces.Generate` and
  > `ChartAnnotationEmitter`, so it reaches the registry without the word `ResourceSchema` appearing
  > in `build/Build.Charts.cs` — the grep behind "still never opens a registry" was accurate and the
  > conclusion was not. The row-by-row comparisons in `ChartRegistryPairTests` have been deleted,
  > keeping only what generation does not reach. And the overlap is **25** rows, not 26:
  > `/properties/clusterId` is placement rather than configuration and is excluded — see
  > charts/README.md § What a chart cannot say.
- **Rule 2 of § Assembly graph rules had a `const` blind spot.** It read binding references, and a
  cross-provider dependency using only `const` members emits none. See that provider's
  `.Contracts.csproj` for the experiment.

  > ⚠ **CLOSED 2026-08-12.** Rules 2, 4, 5 and 7 now read the declared `ProjectReference` set as
  > well, so for an in-tree edge the reference is the violation whether or not a type crossed it.
  > The const variant of the experiment fails now. Rule 3 is deliberately left on binding references
  > alone — ADR-004 puts `KubernetesClient` in the restore closure, so a package-phrased rule there
  > would be unsatisfiable.

Each provider's `.Conformance` project is one `ProviderConformanceCase` and two class declarations.
The second provider renders two objects rather than one and still needed no change to
`test/CyberCloud.Conformance`, which is the claim that shape was making.

`CyberCloud.Providers.Cache` — `CyberCloud.Cache/redis` on `spotahome/redis-operator`, docs/plan/12's
second M1 row. **Valkey, not Redis** (ADR-011); the resource path is the Azure-parity one and every
name a human reads says Valkey.

### What the third provider measured

Where the second provider established things the sample's shape *suggested*, this one is the first to
report on the platform by **failing** against it. Three findings, in ascending order of how much they
cost the next eight services:

- **A provider is four module edges, whichever provider it is.** `module-layering.txt` now carries
  three families with identical columns — `Core`, `Kubernetes`, `ResourceManager`, `Tenancy` — across
  a trivial provider, one with backup and pooling and extensions, and one that does quantity
  arithmetic against a third operator. A fourth family arriving with a fifth edge is the signal.
- **The chart-annotation emitter's output is predictable by hand.** This chart's `@param` block was
  written to match what `ChartAnnotationEmitter` would produce and came back **unchanged on the first
  `./build.sh Charts` run**. That matters because the alternative — a format only the generator knows
  — would make every chart's first commit a two-step ritual.
- **The cluster-backed conformance suite could not host a real provider at all, and nobody had
  noticed.** Every service in docs/plan/12 renders a **custom resource**; the reference provider and
  the sample render a core-group `ConfigMap`; a bare `k3s` serves no REST path for a custom resource.
  So this provider's `.Cluster.Conformance` went **5 of 6 red on its first run**, and the failure did
  not say `404` anywhere — `KubeApiClient.ApplyAsync` maps a `409` and `IsTransport`'s set and nothing
  else, so the raw `k8s.Autorest.HttpOperationException` escaped the grain call and Orleans reported
  `CodecNotFoundException`. `ProviderConformanceCase.RequiredCrds` closes the first half; **the
  unmapped-4xx half is a `CyberCloud.Kubernetes` defect and is still open**, and it is the one to
  care about, because in production it turns an admission-webhook rejection or a missing CRD into a
  serialization error with no status code in it.

⚠ **This provider is also the first to have all six projects**, including the `*.Cluster.Conformance`
that `CyberCloud.Providers.DBforPostgreSQL` still owes. Adding one is two class declarations and an
`AssemblyInfo` line — plus, for a custom resource, the CRD that makes it addressable.

## Planned namespaces

`Platform`, `Identity`, `ContainerService`, `Compute`, `ContainerInstance`, `ContainerRegistry`,
`Network`, `Storage`, `Data`, `Messaging`, `KeyVault`, `Security`, `Monitor`, `Communication`,
`Mail`, `Terminal`, `DesktopVirtualization` — see
[docs/plan/03 § Providers](../../docs/plan/03-repository-layout.md).

## Hard rule

**No `Providers.*` assembly references another `Providers.*` assembly — not even `.Contracts`.**
Cross-provider references go through `CyberCloud.ResourceManager` by resource id, which is where
authorization, quota and audit sit. `Build.Architecture` fails the build on a violation — on the
`ProjectReference` as well as on the binding, so a `const`-only dependency does not slip through.

What a provider *may* reference is `module-layering.txt`, which is rule 7: `CyberCloud.Core`,
`CyberCloud.Kubernetes`, `CyberCloud.ResourceManager` and `CyberCloud.Tenancy`, and nothing else. A
line between two providers cannot be added there — rule 2 refuses what rule 7 would grant.

A provider is not registered in the platform bundle until it passes the conformance suite.
