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
- **Rule 2 of § Assembly graph rules has a `const` blind spot.** It reads binding references, and a
  cross-provider dependency using only `const` members emits none. See that provider's
  `.Contracts.csproj` for the experiment.

Each provider's `.Conformance` project is one `ProviderConformanceCase` and two class declarations.
The second provider renders two objects rather than one and still needed no change to
`test/CyberCloud.Conformance`, which is the claim that shape was making.

## Planned namespaces

`Platform`, `Identity`, `ContainerService`, `Compute`, `ContainerInstance`, `ContainerRegistry`,
`Network`, `Storage`, `Data`, `Messaging`, `KeyVault`, `Security`, `Monitor`, `Communication`,
`Mail`, `Terminal`, `DesktopVirtualization` — see
[docs/plan/03 § Providers](../../docs/plan/03-repository-layout.md).

## Hard rule

**No `Providers.*` assembly references another `Providers.*` assembly — not even `.Contracts`.**
Cross-provider references go through `CyberCloud.ResourceManager` by resource id, which is where
authorization, quota and audit sit. `Build.Architecture` fails the build on a violation.

A provider is not registered in the platform bundle until it passes the conformance suite.
