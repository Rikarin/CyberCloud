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
its milestone, and the milestone is recorded rather than quietly changed. **`natsClusters` has since
landed in the same namespace and closed the gap — see § What the fourth provider measured.**

### What the third provider measured

> ⚠ **CORRECTED 2026-08-12 by the fourth provider, on the bullet this section leads with.** The
> three bullets below say a nested type is impossible here because *"this platform's id grammar has
> no parent instance"*. **That was true when it was written and it is not true now.**
> [12 § Child resources](../../docs/plan/12-managed-data-services.md) landed the same day: a child
> interleaves, `ResourceId.ParentNames` carries the ancestors, `ResourceId.Parent` is a pure function
> of the address, `ResourceManagerService.ResolveAsync` refuses a create whose parent is absent with
> the same `404` an unauthorized read gets, `ReBacResourceRelationWriter` points the `parent` edge at
> the parent resource, and `OpenApiEmitter.PathOf` emits the interleaved template — the refusal
> quoted below was **deleted** from that file. The gateway never counted segments to begin with.
>
> The fourth provider still declares no child, and its reasons are different and smaller. The fatal
> one is the **conformance harness**: `ProviderConformanceCase` is single-type, and both
> `ProviderTestCluster.Address` and `ClusterConformanceHarness.Address` construct a `ResourceId` with
> no `ParentNames` — so a depth-2 `Case.Type` **throws in the constructor** and every test in the
> suite fails before it runs. This document's own hard rule is that *"a provider is not registered in
> the platform bundle until it passes the conformance suite"*, so a child type cannot ship through
> that door at all. Two smaller ones stand from the last bullet below and are unchanged:
> `CliEmitter.Address` emits a hard-coded four-flag list, so `cyc` cannot say *which* parent; and
> `SdkEmitter.AppendCollection` emits no ancestor parameters, so the generated client holds a
> `PathTemplate` it cannot fill. See `charts/managed/nats/conformance.yaml § owed`, `child-types`.

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

### What the fourth provider measured

`CyberCloud.Messaging/natsClusters`, [12 § The catalogue](../../docs/plan/12-managed-data-services.md)'s
third and last **M1** row — which closes that milestone's data-services gap. ⚠ It is **not a fourth
provider namespace**: it is a second resource type inside `CyberCloud.Messaging`, which is a shape
nothing had exercised.

- **A second type in one namespace costs two case objects and four class declarations, and no new
  project.** docs/plan/03's five-project shape turns out to be per *namespace* rather than per
  *type*: one `.Application` module, one `.Conformance` and one `.Cluster.Conformance` serve both.
  Three providers had said the shape was cheap; this is the first evidence about what the shape is
  *of*.
- **The disambiguation ladders hold, and none of them is entered.** `SdkEmitter`'s first tier
  prefixes the provider namespace's last segment — which cannot separate two colliding types in the
  *same* namespace, because the prefix is identical — and falls through to the type path, then
  throws. `CliEmitter.CommandOf` kebabs the type path into a `JsonObject` whose indexer *replaces*.
  Neither ladder runs, because `Kafka cluster`/`NATS cluster` and `kafka`/`nats` are distinct. What
  `MessagingSdkTests` pins is the *outcome* — both types on both surfaces, under distinct names,
  neither swallowing the other — because that is the claim `rabbitmqClusters` can break, and it
  would break silently.
- **⚠ It is the first service in the catalogue with no operator, and docs/plan/12's whole pattern
  assumes one.** `nats-io/nats-operator` — the only project that ever served a `NatsCluster` CRD —
  was **archived on 2025-04-10** and its README says the Helm charts are the recommended way and that
  it *"is not recommended to be used for new deployments"*. `nats-io/nack` ships `Stream`,
  `Consumer`, `Account`, `KeyValue` and `ObjectStore` under `jetstream.nats.io/v1beta2`, but every
  one of them describes an object *inside* a running NATS system and connects to it over the wire.
  So this reconciler applies the **workload** — a `ConfigMap`, two `Service`s, a `StatefulSet` and a
  `PodMonitor` — where the three before it applied one or two custom resources. Two consequences the
  next operator-less service inherits: every default a controller would have supplied is now a
  decision (`podManagementPolicy: Parallel`, `publishNotReadyAddresses`, two probes asking different
  questions, sixty seconds of termination grace), and the **cluster-backed suite needs one CRD stub
  instead of two**, because four of the five kinds are core or `apps`. That is the exact reverse of
  what the Kafka row measured, and it is the same measurement from the other side.
- **⚠ [12 § The pattern, once](../../docs/plan/12-managed-data-services.md)'s piece 6 second branch
  is discharged rather than owed, and the reason is a small proof rather than more effort.** That
  branch — *"hand-write one into the chart only when there is no operator to ask"* — was reached by
  Kafka and left owed, correctly: a hand-written scrape has to hard-code somebody else's pod labels,
  and the operator moves one in a minor release and the scrape goes quiet **without failing**. That
  hazard cannot arise here, because the labels the selector matches are written by this provider onto
  pods created by this provider. **Hand-writing it is safe exactly when there is no operator, which
  is the same condition that forces the branch** — the two halves of the correction agree, and
  nothing had a case to check it on.
- **⚠ Two notes on the Kafka registration turn out to be wrong, and both were checked rather than
  assumed.** The first is the nested-type one, corrected above. The second is `publicIps`: that note
  says it *"is derived from a flag, which no pointer can express however it is read"* — which is true
  of a pointer and false of `MeterDerivation.Of`, in one line. The real blocker is one layer down and
  nothing else in the tree states it: **`QuotaGrain.TryReserveAsync` refuses a non-positive amount**
  (*"A reservation must be positive; 0 is not"*), so a meter that is zero in the ordinary case would
  refuse every create that did *not* ask for external exposure — the default. A conditional meter is
  undeclarable whatever the derivation seam can express. `NatsQuotaTests.NoMeterEverDerivesZero`
  pins it; closing it is a *skip-when-zero* on `MeterRegistration`.
- **The chart-annotation emitter's output is predictable by hand — a second sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run**, exactly as `charts/managed/valkey`'s did.

`CyberCloud.Providers.Storage` — `CyberCloud.Storage/accounts` on SeaweedFS,
[15 § The three kinds](../../docs/plan/15-storage-blob-file.md). **The first provider whose row is not
in docs/plan/12 at all.**

### What the fifth provider measured

⚠ **docs/plan/12 is not the authority for this row and does not contain it.** That document's subject
is *"databases, caches, brokers, search"* and its catalogue has ten rows, none of them storage.
[15 § The three kinds](../../docs/plan/15-storage-blob-file.md) is the authority — *"Object ·
`CyberCloud.Storage/accounts` + `/buckets` · SeaweedFS + S3 gateway · HTTPS, S3 API"* — and
§ Object storage costs it at **M1 · 2.0 EM**. What docs/plan/12 *does* own is ADR-010 clause 1's
operator survey, which names SeaweedFS, and § The pattern, once's eight pieces, which this provider is
built to. **It closes an assumption three other documents were already making**: CloudNativePG's row
promises *"declarative backup to S3 (which we have — [15])"*, `charts/managed/postgres` renders a
destination of `s3://tenant-bucket/postgres`, and the OpenSearch row wants a *"snapshot repository
into the tenant's bucket"*. None of that had a provider.

- **The pattern survives a document boundary unchanged, which is the measurement.** Four edges in
  `module-layering.txt`, six projects, one `ProviderConformanceCase`, one chart, one
  `conformance.yaml` — the same shape docs/plan/12's four rows produced. A catalogue row from another
  document needing a fifth module edge or a sixth project would have been the finding; needing
  neither is the stronger one, because it says the shape is a property of *a managed service* rather
  than of *that table*.
- **⚠ Object count is not a measure of a service's size, and the range is now bracketed at both
  ends.** `CyberCloud.Messaging/natsClusters` renders **five** objects because there is no operator;
  this renders **one** — a `Seaweed` — that expands into masters, volume servers, a filer, an S3
  gateway, their Services and four `ServiceMonitor`s. The cluster-backed suite needs exactly one CRD
  stub, derived.
- **⚠ docs/plan/12 § The pattern, once, piece 6's FIRST branch is now the ordinary case.** The
  corrected piece 6 says *"ask the operator for the scrape object wherever the operator accepts the
  request"*. CloudNativePG answered with `spec.monitoring.enablePodMonitor`; this operator answers
  with `metricsPort` per component — `internal/controller/controller_s3.go`,
  `if m.Spec.S3.MetricsPort != nil { ensureS3ServiceMonitor(m) }`, with an `else` branch that deletes
  the object again. Two of five services ask rather than hand-write, and both of the two that
  hand-wrote had no operator at all.
- **⚠ The quota meters are the first that are a SUM OVER HETEROGENEOUS COMPONENTS**, which is a third
  shape after the two `MeterDerivation` already answers.
  `CyberCloud.DBforPostgreSQL/servers` found that an amount is a quantity *string* rather than a
  number; `CyberCloud.Messaging/natsClusters` added that it is a *product* of a replica count and one
  per-replica figure. Here it is `volumeServers × preset + (masters + 1 filer + gateway.replicas) ×
  250m` — two populations, only one of which the tenant sizes. A derivation copied from either
  earlier provider would be right about the volume servers and would miss **six pods** on the default
  body. `StorageQuotaTests.ChangingOnlyTheMasterCountStillMovesTheAmounts` is the one that fails on
  the copy.
- **⚠ `publicIps` is undeclared for a reason that is NOT the one the two providers before it record,
  and the two look identical from the meter.** Their blocker is `QuotaGrain.TryReserveAsync` refusing
  a non-positive amount, so a conditional external listener derives zero on the default path. This
  type has no external listener to condition on: the operator's `ServiceSpec` is a four-field subset
  — `type`, `annotations`, `loadBalancerIP`, `clusterIP` — with **no `loadBalancerSourceRanges`**, and
  docs/plan/12 § Cross-cutting decisions requires an explicit CIDR list on any exposure. Closing this
  one is an upstream field, not a *skip-when-zero* on `MeterRegistration`.
- **⚠ The first row whose child type is not blocked by anything, and the first whose absence is
  therefore scope rather than a finding.** `charts/managed/kafka` could not address one;
  `charts/managed/nats` found that a NATS account is not a CRD anybody offers. The seaweedfs-operator
  ships `Bucket`, `S3Identity`, `S3Credentials`, `S3Policy`, `S3PolicyBinding` and
  `BucketLifecyclePolicy`, and `BucketSpec` is docs/plan/15's *"globally-unique-per-account name,
  quota, versioning, lifecycle"* almost line for line.

  > ⚠ **CORRECTED on the day it was written.** This bullet said the remaining blocker was
  > `ProviderConformanceCase` being single-type — *"third sighting, and the first where nothing
  > upstream is in the way"*. **The blocker was closed the same day**, by *"A child type ships end to
  > end, and the harness that could not address one now can"*: `IProviderCaseSource` gained a
  > `static virtual Ancestors`, `ProviderTestCluster` refuses a depth/count mismatch by name, and both
  > `CliEmitter` and `SdkEmitter` carry ancestors. So the second, third and fourth sightings of that
  > blocker are all discharged, and `accounts/buckets` is now a body shape, a reconciler and a
  > seventh resource type that this provider did not build.
- **⚠ Piece 5's absence is worse here than anywhere else in the catalogue, and the answer to "is the
  service usable anyway" flips.** `CyberCloud.DBforPostgreSQL/servers` has a working database because
  CloudNativePG generates its own password. An S3 endpoint is reachable *only* with an access-key
  pair. And SeaweedFS with no identities file does not merely skip authentication —
  `weed/s3api/auth_credentials.go` sets `isAuthEnabled = len(identities) > 0` and
  `AuthenticateRequest` then returns an **admin** identity — so the reference is rendered against a
  `Secret` nothing writes and the account visibly does not finish. Checked in that file rather than
  in a README. `charts/managed/seaweedfs/conformance.yaml § owed` says exactly what closes it.
- **⚠ The structural statelessness check's blind spot, confirmed a third time.** A `readonly`
  `Dictionary` cache added to `StorageAccountReconciler` left
  `ReconcilerConformance.CheckNoHiddenState` **green** and failed only
  `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem`. Both halves are in
  `StorageReconcilerTests` and both were run red against the counter-example.
- **The chart-annotation emitter's output is predictable by hand — a third sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run**, exactly as `charts/managed/valkey`'s and
  `charts/managed/nats`' did. Only `values.schema.json` had to be generated.
- **⚠ `CyberCloud.slnx` and `CyberCloud.Providers.slnf` carried unresolved merge-conflict markers and
  every gate was green over them.** In the `.slnx` the markers happen to be well-formed XML —
  `<<<<<<< HEAD` sat inside a comment and `=======` / `>>>>>>> …` as character data — so the solution
  parsed and both provider families were listed. The `.slnf` is JSON and was **not** well-formed;
  nothing in the gate set reads it. Both are resolved as the union, which is what the working tree
  already contained.

`CyberCloud.Providers.DocumentDB` — `CyberCloud.DocumentDB/accounts`, FerretDB over CloudNativePG,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md)'s *"MongoDB-compatible · M2 ·
1.2 EM"*. **The first provider that is two workloads**, and the first to render another provider's
operator's CRD.

### What the sixth provider measured

- **⚠ ADR-010 clause 1's survey names an operator that does not exist, for the second time.** That
  clause lists *"FerretDB"* in a sentence about *"the operator selection per managed service"*.
  Checked against the GitHub API on 2026-08-12 rather than a README — `GET /orgs/FerretDB/repos` —
  the organisation holds `FerretDB`, `documentdb`, `dance`, `deps`, language examples and marketplace
  forks, and **no operator, no CRD and no Helm chart**; upstream's documented Kubernetes install is a
  `Deployment` and a `Service` applied with `kubectl`. `charts/managed/nats` found the same about
  `nats-operator` (archived 2025-04-10). Two of six rows in, clause 1 is a survey of *software
  choices* that is only sometimes a survey of *operators*, and that belongs in ADR-010 rather than in
  a provider.
- **⚠ docs/plan/12's *"already built for the row above"* is false at the code level, and the
  duplication it forced found a live defect in the row above.** That row is
  `CyberCloud.DBforPostgreSQL/servers` and § Hard rule below forbids the reference, so this provider
  renders the CloudNativePG `Cluster` CRD independently. Writing the second rendering surfaced the
  first one's: `PostgresServers.ClusterJson` and `charts/managed/postgres/templates/cluster.yaml`
  both write `spec.postgresql.parameters.shared_preload_libraries`, and CloudNativePG declares that
  key as a **sibling** of `parameters` (`api/v1/cluster_types.go`,
  `AdditionalLibraries []string json:"shared_preload_libraries"`), lists it in
  `FixedConfigurationParameters` (`pkg/postgres/configuration.go`), and refuses it inside
  `parameters` from its validating webhook (`internal/webhook/v1/cluster_webhook.go`, *"Can't set
  fixed configuration parameter"*). **Every Postgres server created with an extension is rejected at
  admission after the caller was told `202`.** The default body asks for none, which is why nothing
  had noticed. Not fixed here — that provider is not this one's — and recorded at
  `charts/managed/ferretdb/conformance.yaml § owed`.
- **⚠ Piece 6 takes BOTH of its branches on one resource, which nothing had done.** CloudNativePG
  answers the first branch (`spec.monitoring.enablePodMonitor`) for the PostgreSQL half; there is no
  operator to ask for the FerretDB half, so the chart hand-writes a `PodMonitor`. The second
  branch's hazard — a hand-written scrape hard-coding somebody else's pod labels — cannot arise, for
  the reason `charts/managed/nats` proved: the labels the selector matches are written by this
  provider onto pods created by this provider. One `monitoring.enabled` flag, two mechanisms.
- **⚠ The `s1` sizing family means two different machines depending on which product you read, and
  docs/plan/12 says there is one table.** § Sizing vocabulary opens *"One table, defined once, used
  by every service and every VM"*. `PostgresServers.Presets` spells `s1.small` as `(500m, 2Gi)`;
  `StorageAccounts.Presets` spells it `(1, 4Gi)` — the same ratio one rung apart. And
  `PostgresServers.Presets["s1.nano"]` is `(100m, 512Mi)`, which is **5** GiB per core rather than
  the 4 the family name declares, on that rung and no other. This provider is the third copy, takes
  the ratio-correct rungs, and pins every one of them.
- **⚠ Piece 5's absence is REPRODUCED rather than made worse, which is the first time.**
  `CyberCloud.Cache/redis` does not come up; `CyberCloud.Storage/accounts` comes up serving every
  anonymous caller as an administrator. Here CloudNativePG generates the credential and FerretDB
  neither stores nor invents one — `website/docs/security/authentication.md`: *"FerretDB does not
  store authentication information … it relies entirely on PostgreSQL's authentication
  mechanisms"*, and an anonymous client *"may still connect … but they cannot access or perform
  actions on the database"*. So the service works, an unauthenticated caller gets nothing, and
  `listKeys` merely has nowhere to read the password back from.
- **⚠ The Labels architecture gate is a gate over `KubeCommand`, not over a provider, and this was
  measured rather than assumed.** Rendering a `metadata.labels` block carrying
  `cybercloud.io/tenant-id: not-a-tenant` on this provider's `Deployment` left
  `EveryAppliedObjectCarriesTheSevenMandatoryLabelsAndBothAnnotations` **green** — the builder
  overwrites it. So the sixth suite this gate counts adds no coverage of the seven. What a provider
  *can* get wrong is the `app.kubernetes.io/*` set, which is written into three places that must
  agree (a `Deployment`'s immutable `spec.selector`, its pod template, and a `PodMonitor`'s
  selector) and is injected by nothing; `DocumentDbReconcilerTests.TheSelectorTheDeploymentTheTemplateAndThePodMonitorAllAgree`
  is that test and it was run red against a drifted `PodMonitor` selector.
- **⚠ The structural statelessness check's blind spot, confirmed a FOURTH time.** A `readonly`
  `Dictionary` cache added to `DocumentDbAccountReconciler` left
  `ReconcilerConformance.CheckNoHiddenState` **green** and failed only
  `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem`. Four sightings is the point at which
  every future provider pays two tests for one platform gap; closing it is `CheckNoHiddenState`
  learning that a `readonly` field of a mutable collection type is state.
- **The chart-annotation emitter's output is predictable by hand — a fourth sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run**, exactly as `charts/managed/valkey`'s,
  `charts/managed/nats`' and `charts/managed/seaweedfs`' did. Only `values.schema.json` had to be
  generated.
- **Four module edges, the same four, for the sixth family in a row** — and this one renders four
  objects across four API groups and reaches two upstream projects. A fifth edge remains the signal.

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
