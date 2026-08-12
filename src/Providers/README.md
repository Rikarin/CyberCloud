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
  `CodecNotFoundException`. The harness deriving each CRD from `Objects` closes the first half; **the
  unmapped-4xx half is a `CyberCloud.Kubernetes` defect and is still open**, and it is the one to
  care about, because in production it turns an admission-webhook rejection or a missing CRD into a
  serialization error with no status code in it.

⚠ **This provider is also the first to have all six projects**, including the `*.Cluster.Conformance`
that `CyberCloud.Providers.DBforPostgreSQL` still owes. Adding one is two class declarations and an
`AssemblyInfo` line — plus, for a custom resource, the CRD that makes it addressable.

`CyberCloud.Providers.Messaging` — `CyberCloud.Messaging/kafkaClusters` on Strimzi in KRaft mode,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md). **The first provider whose objects
are custom resources on both sides**, and the first with a `.Cluster.Conformance` sibling other than
the sample's.

⚠ **That row is `M2` in docs/plan/12, not `M1`.** The M1 rows of that table are PostgreSQL, Valkey
and NATS; the M1 event-streaming service is `CyberCloud.Messaging/natsClusters`. This landed ahead of
its milestone, and the milestone is recorded rather than quietly changed.

### What the third provider measured

**The nested resource type, which is the thing docs/plan/12 asks this service for and the thing it
did not get.** That document says Kafka is 1.2 EM rather than 2.5 because *"Strimzi's `KafkaTopic`
and `KafkaUser` CRDs map to resource types almost one to one"*. The mapping is one to one at the
**CRD**. It is not one to one at the **address**, and the difference is the whole finding:

- **The registry, the id, the labels and four of the five generated surfaces carry a nested type
  today, unchanged.** `IProviderBuilder.ResourceType` documents `servers/databases` by name;
  `ResourceTypeName` validates to `MaxDepth = 3`; `ResourceId.TryParsePath` parses a variable-length
  type run; `ProviderRegistry` keys on the full canonical name; the gateway delegates to the parser
  rather than counting segments; `KubeLabels.ResourceTypeValue` maps `/` to `_` and
  `KubeLabelTests` already pins `cybercloud.dbforpostgresql_servers_databases` against Kubernetes'
  own regex. `.ResourceType("kafkaClusters/topics")` would compile, start and serve.
- **⚠ But this platform's id grammar has no parent *instance*, and that is a decision rather than a
  gap.** Azure spells a child `/kafkaClusters/{cluster}/topics/{topic}`; `ResourceId` spells it
  `/kafkaClusters/topics/{name}` — one name, at the end, with the type path whole in the middle.
  `OpenApiEmitter`'s own remarks refuse Azure's shape outright, because emitting it *"would produce
  URLs that `ResourceId.TryParsePath` rejects — a generated surface disagreeing with the runtime,
  which is the one failure ADR-012 exists to make impossible."*
- **So a `topics` type here would be a *sibling* of its cluster, not a child.** Its parent would be a
  body property (the shape `RequiresCluster` already uses); topic names would be unique per
  **resource group**, not per cluster; nothing would check the parent exists at create — the write
  path's `ResolveAsync` validates tenant, subscription, type and api-version and never a parent; and
  ReBAC's `parent` edge is always resource → resourceGroup, so **the owner of a cluster would not own
  its topics**. That is a different product from the one docs/plan/12 describes.
- **Two surfaces would also need work before a nested type shipped**, neither of which is this
  provider's to do. `CliEmitter.CommandOf` flattens `a/b` to one kebab-cased command, which
  contradicts [21 § Grammar](../../docs/plan/21-cli-and-sdks.md)'s `cyc <group> <subgroup...> <verb>`
  — and `CliEmitter`'s `JsonObject` indexer *replaces*, so a flat `kafkaClustersTopics` and a nested
  `kafkaClusters/topics` kebab to the same key and one type silently vanishes from the CLI, which
  `DerivedSurfaces.CliProblems` does not check. `SdkEmitter` resolves a display-name collision by
  prefixing the provider namespace's last segment, which does not disambiguate two colliding types in
  the **same** namespace — exactly the nesting-shaped case.

**So one solid type landed and the sub-resources did not**, with the reason written at
`KafkaClusters` and in `charts/managed/kafka/conformance.yaml` § owed rather than left to be
rediscovered.

**Three other things it measured**, each a second or third sighting that turns an anecdote into a
finding:

- **The quota meters are undeclarable for a second reason as well as the first.**
  `CyberCloud.DBforPostgreSQL/servers` found that `Meter(meter, pointer)` reads a *number* and every
  Kubernetes amount is a string. This type adds two: every amount here is a **product** — one node's
  quantity times the node count — and `Meter` multiplies by nothing; and `publicIps` is derived from
  a **boolean**, which no pointer expresses however it is read. `MessagingProvider` carries the
  arithmetic it would have declared.
- **⚠ ADR-012's fifth surface refuses `@pattern` on an array while emitting `@enum` there.** The
  registry applies a `Pattern` per element on an array — `SchemaProperty.ElementKind` says so — and
  `./build.sh Charts` fails with *"it refines a string, and JSON Schema ignores it on any other
  type"*, which the same emitter contradicts one directive over: `@enum` on an array becomes
  `items: {type: string, enum: […]}`, which is the per-element shape `@pattern` needs. So
  `/properties/external/allowedCidrs` carries **no** shape constraint and a malformed CIDR is
  accepted by the API and refused by the API server after the caller was told `202`. Closing it is
  one case in `ChartAnnotationEmitter` and one in `Build.Charts`' `Validate`.
- **A bare k3s serves no REST path for a custom resource, and the failure names nothing.** The
  sample's cluster-backed suite renders a core-group `ConfigMap`, whose path the API server serves
  without being told; a Strimzi `Kafka`'s does not exist, and the client cannot discover the group,
  so it throws `k8s.Autorest.HttpOperationException` **with no status code**. Five of the six tests
  in `CyberCloud.Providers.Messaging.Cluster.Conformance` failed that way. The harness now installs a
  minimal definition per custom kind and waits for `Established` — **derived from the case's own
  `Objects`**, which already carry group, version, kind and plural, rather than declared as a member
  a provider author could get wrong by omission and have the omission reported by the worst error
  message in the suite.

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
