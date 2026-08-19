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
  `  parsed and both provider families were listed. The `.slnf` is JSON and was **not** well-formed;
  nothing in the gate set reads it. Both are resolved as the union, which is what the working tree
  already contained.

`CyberCloud.Providers.Search` — `CyberCloud.Search/services` on the OpenSearch operator,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md). **The first `M3` row**, and the
first provider whose engine choice and operator choice come from two different ADRs.

### What the sixth provider measured

⚠ **The milestone is a scheduling fact and not a shape.** Four module edges, six projects, one
`ProviderConformanceCase`, one chart, one `conformance.yaml` — the same shape `M1` and `M2` rows
produced, and the same shape docs/plan/15's row produced. Five families had shown the columns were
identical; this is the first evidence that they are identical *across a milestone boundary* as well.

- **⚠ The hazard this row was expected to have is refuted, and the refutation is the finding.**
  docs/plan/12 warns that a managed service reachable with a weak password is the most common cloud
  breach, and OpenSearch ships the security plugin on by default — so the expectation, from
  `charts/managed/seaweedfs`, was an engine that reads "no credentials configured" as "authenticate
  nobody". It does not. `opensearch-operator/pkg/helpers/helpers.go`'s
  `EnsureAdminCredentialsSecret` returns the tenant's secret when
  `spec.security.config.adminCredentialsSecret.Name != ""` and otherwise does
  `randomPassword := GenerateSecurePassword()` into a generated Secret. So the catalogue now has
  **three** credential outcomes without piece 5, not two: a working service whose password the
  platform cannot hand out (`CyberCloud.DBforPostgreSQL/servers`, and now this one), a service that
  visibly never converges (`CyberCloud.Storage/accounts`), and a service that comes up open
  (`CyberCloud.Messaging/natsClusters`). **They are indistinguishable from the resource's status**,
  which is why each provider states which one it is.

  > ⚠ **CORRECTED 2026-08-13: there is now a fourth outcome, and it is the one the others should
  > reach for.** `CyberCloud.Storage/accounts` moved off this list — it mints its own credential into
  > the tenant's vault at create time through `ISecretWriter`, renders it into the object the data
  > plane mounts, and hands it back through a `listKeys` handler. The other three are unchanged and
  > the reason is per-engine work rather than a missing platform seam: the seam exists now, and what
  > each of them needs is a decision about whose password wins when the operator also generates one.

  > ⚠ The operator's own **documentation contradicts its own code** — `docs/userguide/main.md` says
  > *"By default the operator will use the included demo securityconfig with default users"* and
  > names `admin / admin`. Both can be true at once (demo roles, generated password) and which one a
  > release does is the difference between a cluster nobody can log into and one everybody can.
  > Recorded at `charts/managed/opensearch/conformance.yaml § owed`, `demo-securityconfig`, as the
  > one item that should be checked against a running operator before the row is called done.

- **⚠ What the operator will *not* do unasked is the thing that breaks silently.**
  `pkg/reconcilers/tls.go` returns immediately when `spec.security.tls` is nil — *"No security
  specified. Not doing anything"* — generating no certificates at all, and OpenSearch's security
  plugin needs transport TLS to form a cluster. The symptom is a set of pods that all pass their
  readiness probes and never discover each other. It is written unconditionally and is not a values
  key, because "turn off TLS between the nodes of your search cluster" is not a setting.

- **⚠ Containment-not-equality is settled by the CRD, and the conformance suite is *blind* to it.**
  `api/v1/opensearch_types.go` puts `+kubebuilder:default=true` **and**
  `+kubebuilder:validation:Required` on `ConfMgmt.SmartScaler`, so a real API server writes a field
  back on every apply, on the first create. `ClusterConformanceHarness` derives a CRD stub with an
  *open* schema, which has no defaults — so the read-back there echoes the apply and an equality
  comparison passes. **Measured rather than argued**: the equality mistake was run against both, and
  `CyberCloud.Providers.Search.Conformance` was **27 of 27 green** while
  `OpenSearchMatchesTests.AnObjectCarryingTheCrdsOwnDefaultsStillMatches` was the only red thing in
  the tree. Every provider whose operator's CRD carries defaults has this hole.

- **⚠ The quota meters are a sum over heterogeneous components — second sighting — and they add a
  distinction the first one could not.** `CyberCloud.Storage/accounts` established the shape;
  `charts/managed/nats/conformance.yaml` established that `QuotaGrain.TryReserveAsync` refuses a
  non-positive amount, so a *conditional meter* is undeclarable. That conclusion is about a whole
  **meter** and not about a **term**, and nothing had tested the difference because no earlier type
  had an optional population. `/properties/coordinatingNodes` defaults to `0`, so that term derives
  nothing on every ordinary create while the total keeps a floor. **A term may be zero; a meter may
  not.**
  <br>⚠ This type also needs *three* derivations rather than one parameterised one, because the
  populations split differently per meter: a coordinating node is sized like a **data** node for CPU
  and memory and like a **cluster-manager** node for disk.

- **⚠ The shared conformance harness has a budget nobody had spent.** It creates against **one**
  subscription and nothing releases the committed amounts between assertions, so a provider's
  assertion count is `QuotaGrain.Defaults[MemoryGb] / its own memory draw`. This type's schema
  default draws **30 GiB** — twice `CyberCloud.Storage/accounts`' 15 — and four assertions failed
  with *"300 committed + 90 reserved + 30 requested > 400"*. ⚠ **The failure names quota and not the
  provider**, so an author meeting it has no reason to connect it to their own sizing table. The
  case now uses the smallest legal service; the diagnostic is owed at
  `charts/managed/opensearch/conformance.yaml § owed`, `conformance-quota-is-a-budget-per-provider`.

`CyberCloud.Providers.DocumentDB` — `CyberCloud.DocumentDB/accounts`, FerretDB over CloudNativePG,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md)'s *"MongoDB-compatible · M2 ·
1.2 EM"*. **The first provider that is two workloads**, and the first to render another provider's
operator's CRD.

### What the seventh provider measured

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
  both wrote `spec.postgresql.parameters.shared_preload_libraries`, and CloudNativePG declares that
  key as a **sibling** of `parameters` (`api/v1/cluster_types.go`,
  `AdditionalLibraries []string json:"shared_preload_libraries"`), lists it in
  `FixedConfigurationParameters` (`pkg/postgres/configuration.go`), and refuses it inside
  `parameters` from its validating webhook (`internal/webhook/v1/cluster_webhook.go`, *"Can't set
  fixed configuration parameter"*). **Every Postgres server created with an extension was rejected at
  admission after the caller was told `202`.** The default body asks for none, which is why nothing
  had noticed. Fixed in that provider on 2026-08-12 — both spellings, plus one test each, because
  nothing in `./build.sh` compares a Helm template to a registry — and recorded at
  `charts/managed/postgres/conformance.yaml § owed`. **The lesson survives the fix**: the duplication
  § Hard rule forces is not only a cost, it is a second opinion, and it is the only reader either
  rendering has ever had.
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
- **⚠ The structural statelessness check's blind spot, confirmed a FOURTH and FIFTH time.** A
  `readonly` `Dictionary` cache added to `DocumentDbAccountReconciler` — and, independently, one
  added to `OpenSearchServiceReconciler` — left `ReconcilerConformance.CheckNoHiddenState` **green**
  and failed only `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem`. ⚠ Both halves were run
  red against the counter-example, in two provider families that were built in parallel and could
  not have copied each other, which is as close to independent replication as this repository gets.
  Five sightings is well past the point at which every future provider pays two tests for one
  platform gap; closing it is `CheckNoHiddenState` learning that a `readonly` field of a mutable
  collection type is state.
- **The chart-annotation emitter's output is predictable by hand — a fourth sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run**, exactly as `charts/managed/valkey`'s,
  `charts/managed/nats`' and `charts/managed/seaweedfs`' did. Only `values.schema.json` had to be
  generated.

- **⚠ `CyberCloud.Search/vectorStores` is not declared, and the reason is one line of docs/plan/12
  that does not hold.** That row says *"Qdrant's operator model is simple enough that this is the
  cheapest M3 item"*. `github.com/qdrant/qdrant-operator` answers **404** — the operator that exists
  is the one Qdrant Managed, Hybrid and Private Cloud run, and is not distributed. ⚠ ADR-010
  clause 1's survey turns out to agree once that is known: it names an *operator* for most rows and
  for this one names only *"Qdrant"*. So the type is the **operator-less** shape,
  `CyberCloud.Messaging/natsClusters`' and its second sighting — four objects over
  `qdrant/qdrant-helm` (Apache-2.0) rather than one custom resource, and **no** CRD stub in the
  cluster-backed suite where this type needs one. Its blocker is credentials: Qdrant's chart leaves
  `service.api_key` unset by default and a Qdrant with no API key serves every request
  unauthenticated, which is the SeaweedFS hazard reached through a chart default. The full account
  is at `SearchProvider`'s remarks.

- **Four module edges, the same four, for the seventh family in a row** — and `DocumentDB` renders
  four objects across four API groups and reaches two upstream projects. A fifth edge remains the
  signal.

  > ⚠ **And the eighth family is the first to ask for one.** `CyberCloud.Analytics` wants an S3 cold
  > tier, which needs `CyberCloud.Storage/accounts`, and `ReconcileContext` has no resolver for
  > another provider's resource. `module-layering.txt` now records **two** families wanting the same
  > fifth line, which is the signal this bullet has been waiting for since the third provider.

`CyberCloud.Providers.Analytics` — `CyberCloud.Analytics/clickhouseClusters` on the Altinity operator,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md). **The eighth family, and the first
that renders two custom resources in two API groups**, and the first whose row the platform is
already a customer of.

### What the eighth provider measured

- **⚠ The platform runs ClickHouse and the platform's ClickHouse is NOT this resource type, and that
  was the first question this row had to answer.** docs/plan/12 says *"We run ClickHouse for telemetry
  and metering"* and cites [16](../../docs/plan/16-observability.md) and
  [22](../../docs/plan/22-billing-metering-and-quota.md) — both of which describe a **per-region,
  database-per-tenant, platform-schema** store on the ingest write path
  ([05 § Every store](../../docs/plan/05-state-and-storage.md) gives it one row). This type is a
  **single-tenant cluster in a tenant namespace whose schema the tenant owns**. Four things separate
  them and only the fourth is about parity: the tenancy shape is opposite; the schema owner is opposite
  and is this row's own scope boundary (*"the resource does not manage tables"*); making the telemetry
  store a resource of this type would be a **dependency cycle**, since `CyberCloud.Ingest.Host` is
  deliberately not an Orleans client precisely so telemetry never runs through the control plane; and
  the tenant-facing observability resource already exists and is `CyberCloud.Monitor/workspaces`. What
  docs/plan/12's sentence actually claims is narrower and is true — *"the operational knowledge is not
  incremental"*, i.e. the **engine** gets built either way, not the **resource type**.
- **⚠ docs/plan/12's *"ZooKeeper/ClickHouse Keeper managed by the operator"* is half true, and the
  other half is an object.** The Altinity operator does serve a Keeper CRD — but a
  `ClickHouseInstallation` does not create one for itself, it **names** one, by Service, in
  `spec.configuration.zookeeper.nodes`. Upstream's own `docs/chk-examples/01-chi-simple-with-keeper.yaml`
  is two documents, and the comment on the host line reads *"This is a service name of chk/simple-1"*.
  So this type renders two objects in **two API groups** — `clickhouse.altinity.com` and
  `clickhouse-keeper.altinity.com`, one operator binary — and the string that binds them is a Service
  **neither object creates**. Nothing in an apply, a read-back or an admission check would notice it
  being wrong; a tenant's first `ReplicatedMergeTree` would.
- **⚠ One `Matches` over two kinds forces a rendered document to name its own kind, and that is new.**
  The five providers before this accept `null or "<TheirKind>"`, which is right for a type that owns one
  kind and is a guess for a type that owns two. `KubeCommandBuilder` injects `kind` on the apply path,
  so the renders write it themselves — from the **same** `GroupVersionKind` constant the builder is
  handed, so the two cannot disagree, and `EachRenderedBodyNamesTheSameKindTheCommandTargets` is what
  says so rather than the comment.
- **⚠ The quota meters are a PRODUCT and a SUM at once — a fourth shape — and the product has two
  factors the tenant sets separately.** `CyberCloud.DBforPostgreSQL/servers` found that an amount is a
  quantity *string*; `CyberCloud.Messaging/natsClusters` that it is a *product* of a replica count and
  one figure; `CyberCloud.Storage/accounts` that it is a *sum* over heterogeneous components. Here it
  is `shards × replicas × preset + keeperNodes × 250m`. **A derivation copied from `natsClusters` is
  exactly right on the default body — one shard — and reserves a third of a three-shard cluster**;
  `ClickHouseQuotaTests.ShardsAndReplicasBothMoveTheAmounts` is the one that fails on the copy, and it
  was run red against it.
- **⚠ `Matches` is containment, and NOT for the reason three of the five before it give.** Their
  argument is structural defaulting. Checked in the CRDs rather than in a README: **neither Altinity
  CRD declares a single `default:`**, and this operator ships **no admission webhook** — third sighting
  of `KafkaClusters.Matches`' finding, and it makes the usual argument *false* here. The reasons that
  are real are two: `spec.templating.policy: auto` merges a `ClickHouseInstallationTemplate` into this
  spec at the request of a cluster operator who is not this platform (`status.usedTemplates` exists to
  record it), and half of what this provider writes lands under
  `x-kubernetes-preserve-unknown-fields: true`, which the API server does not prune.
- **⚠ docs/plan/12 § The pattern, once, piece 6 reaches an answer NEITHER of its two branches
  describes.** That piece says *"ask the operator … and hand-write one into the chart only when there
  is no operator to ask."* There **is** an operator here and it does not accept the request: Altinity
  exports metrics for every installation through **one cluster-wide exporter on its own pod**, and the
  CHI carries no per-installation `ServiceMonitor` switch at all — so the first branch has nothing to
  ask for and the second branch's precondition is false. What the CHI accepts is
  `configuration.settings` turning on **ClickHouse's own** Prometheus endpoint, which this chart writes;
  the object that scrapes it is owed, because the selector upstream pairs with those settings is the
  *operator's* pod label, which is the hazard piece 6's correction names.
- **⚠ Piece 5's absence produces the best failure mode in the catalogue, and that is worth as much as
  the gap.** `charts/managed/seaweedfs` found it producing an **anonymous administrator**;
  `charts/managed/nats`, servers that accept every connection in the namespace;
  `charts/managed/postgres`, a working database whose password cannot be handed out. The operator's own
  hardening guide says a CHI with no `configuration.users` gets `default` with an empty password behind
  a `host_regexp` **and** an explicit pod-IP allow-list covering *this cluster's own pods*. So the
  cluster comes up **secure and unreachable**. Four services in, the answer to *"is it usable without
  piece 5"* is a property of the **engine**, not of the platform, and the spread now runs from
  administrable-by-anyone to reachable-by-nobody.
- **⚠ The first row whose missing feature is blocked by the CROSS-PROVIDER seam.** docs/plan/12's third
  bullet is *"S3-backed disks for cold storage tiers"*, whose endpoint and access-key pair belong to a
  `CyberCloud.Storage/accounts` resource. Rule 2 forbids the assembly edge and the sanctioned route — a
  resource id through `CyberCloud.ResourceManager` — has **no reader a reconciler can call**:
  `ReconcileContext` carries a cluster connection and an `ISecretResolver` and nothing that resolves
  another resource. `module-layering.txt` now records **two** families wanting the same fifth line for
  two different features, which is what turns "a provider might reach for this" into evidence.
- **⚠ The structural statelessness check's blind spot, confirmed a fourth time.** A `readonly`
  `Dictionary` cache added to `ClickHouseClusterReconciler` left `ReconcilerConformance.CheckNoHiddenState`
  **green** and failed only `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem` (and the
  Keeper-name test). Both halves are in `ClickHouseReconcilerTests` and both were run red against the
  counter-example.
- **⚠ The first row whose child types are refused by the CATALOGUE rather than by the platform.** Every
  blocker the four earlier providers recorded is closed and `CyberCloud.Storage/accounts/buckets` ships
  through that door. The obvious children here are databases and tables, and docs/plan/12 forbids them
  in as many words. That is a different kind of absence from the other four and is recorded as one.
- **The chart-annotation emitter's output is predictable by hand — a fourth sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run** — *"unchanged, 0 problem(s)"* — exactly as
  `charts/managed/valkey`'s, `charts/managed/nats`' and `charts/managed/seaweedfs`' did. Only
  `values.schema.json` had to be generated.
- **Four module edges, six projects, one `ProviderConformanceCase` — an eighth family with identical
  columns.** A type rendering two custom resources across two API groups, wiring one to the other,
  needed no fifth edge and no seventh project. ⚠ It *wants* a fifth edge for the S3 cold tier and
  did not take one; that is the open request recorded above, not a satisfied dependency.

`CyberCloud.Providers.ContainerService` — `CyberCloud.ContainerService/managedClusters` and its
`agentPools` child, on Cluster API + Kamaji + KubeVirt.
[13 § Managed Kubernetes](../../docs/plan/13-compute-vm-containers.md), **M1 · 4.0 EM**, with
[09 § Kubernetes in Kubernetes](../../docs/plan/09-kubernetes-fabric.md) as the substrate and ADR-009
as the decision. **The first provider whose product is a Kubernetes API server.**

### What the tenth provider measured

- **⚠ THE RESOURCE'S PRODUCT IS NOT THE OBJECTS IT APPLIES, AND THAT IS THE FIRST TIME.** Nine
  families render objects into a cluster the platform owns and the tenant never sees; what they sell
  is a workload, and "the operator will get there" makes spec containment a fair proxy for it. This
  one renders three Cluster API objects whose *effect* is a second cluster. A `Cluster` whose spec
  reads back perfectly can be a tenant with no API server, no nodes and no kubeconfig — docs/plan/09
  budgets **six to nine minutes** between those two states. So `ManagedClusterReconciler` is the only
  reconciler in the tree whose `Converged` reads a `status`, and it reports Cluster API's own
  condition messages as progress, which is what docs/plan/08 means by *"what turns a four-minute
  cluster creation from a spinner into a story"*.
  <br>⚠ **Its third answer is a hole and it is named rather than hidden.** An object with no `status`
  at all **converges**. The platform cannot distinguish "Cluster API has not looked yet" from
  "Cluster API is not running", and — decisively — **neither conformance harness can produce anything
  else**: `FakeKubeCluster` echoes an apply back with no status and the k3s harness installs a
  schema-less CRD stub with no controller behind it. A reconciler that refused would never converge
  in either suite, and *"a provider is not registered until it passes"* would make the row
  unshippable. What keeps the hole small is that a management cluster with no Cluster API **fails at
  the apply, by name**. `charts/managed/kubernetes/conformance.yaml § owed`,
  `converged-is-not-ready`.
- **⚠ The first type to draw `QuotaMeter.Clusters`, and the shared conformance harness could not host
  it — which is the second sighting of a budget nobody had spent and the first that is fatal rather
  than tuneable.** That meter has existed since `QuotaGrain` was written (`Defaults` gives it **5**)
  and `MeterCatalog` already bills `BillingMeter.ClusterHours` off it as *"Managed Kubernetes clusters
  × hours"*; nine families shipped without declaring it because none of them is a cluster. The suite
  creates against **one** subscription twenty-eight times and nothing releases in between, so the
  sixth assertion onwards failed with a quota error naming neither the provider nor the harness.
  `CyberCloud.Providers.Search` met the same wall and could tune its way out by choosing a smaller
  service; **a cluster cannot ask for less than one cluster.** `ProviderTestCluster.LiftQuotaAsync`
  and its twin in `ClusterConformanceHarness` close it for every provider, which discharges
  `charts/managed/opensearch/conformance.yaml § owed`'s
  `conformance-quota-is-a-budget-per-provider` rather than diagnosing it.
- **⚠ The first child type that CHANGES its parent, and therefore the first whose quota is not a
  create-time constant.** `CyberCloud.Storage/accounts/buckets` declares `meters: []` and argues —
  correctly — that a bucket is a ceiling inside capacity its account already reserved. An agent pool
  is the opposite: a managed cluster with no pools has **no worker nodes at all**, so every worker
  VM's vCPU, memory and disk is reserved on the child or nowhere. And with an autoscaler on, the
  number of machines is moved by a controller the platform does not observe — so `EffectiveCount`
  reserves **`maxCount` rather than `count`**, which is a meter whose input is a *ceiling* rather than
  a *size* and is a shape nothing in the catalogue had. ⚠ A reservation is not a bill: docs/plan/22's
  usage pipeline samples what ran, and the two disagreeing here is not a bug in either.
- **⚠ Also the first child that is not structurally smaller than its parent.** Three objects against
  its parent's three, three derived meters against its parent's two. *"A child is a smaller thing
  than its parent"* turns out to have been a fact about object storage.
- **⚠ docs/plan/13 and docs/plan/09 disagree about whether a BYO cluster is this type, and three
  platform facts decide it rather than a preference.** docs/plan/13 puts both flavours behind one
  type with a `kind` discriminator; docs/plan/09 § Cluster connections spells them as two paths.
  `ResourceSchema` has no conditional, so a discriminated body makes `network.podCidr` required for
  one flavour and meaningless for the other; every derived meter would be **zero** for a `Connected`
  cluster, which `QuotaGrain.TryReserveAsync`'s positive-amount refusal makes undeclarable (**fourth
  sighting**); and *"returns `null` for node-pool operations"* has nowhere to live, because a node
  pool is a child resource **type** and a PUT to one either creates a resource or answers `404`.
- **⚠ docs/plan/13's "the API enforces [version skew] with a clear error" is REFUTED, and the reason
  is the seam `charts/managed/seaweedfs-bucket` already recorded.** A node pool's version lives in a
  *different resource* from its cluster's and `ResourceSchema` validates one body against constants.
  Second sighting of `bucket-cluster-may-differ-from-its-accounts`, and the first where the
  consequence is a broken cluster rather than an unreconciled object. `SkewIsLegal` and
  `UpgradeIsLegal` exist, are tested, and are called by **nothing** on the write path — deliberately,
  so closing the seam is wiring rather than writing. ⚠ docs/plan/13's *"at most one minor"* is also
  about a different rule than it looks: that is the control plane's own **step size**; the kubelet
  rule is **three** minors, and has been since Kubernetes 1.28.
- **⚠ ADR-009's "a dedicated etcd per tenant" is not honoured, and the blocker is a SCOPE rather than
  a cost.** Kamaji's `DataStore` is **cluster-scoped** (`kamaji.clastix.io_datastores.yaml`,
  `scope: Cluster`). Every object this platform applies is namespaced and lives inside
  `{subscriptionId:N}-{resourceGroup}`; a cluster-scoped object has no namespace to be isolated by,
  so two tenants would compete for one name in one flat space. What is given up is stated rather than
  glossed: tenant control planes share an etcd, which is a **blast-radius** boundary rather than a
  data-visibility one. ⚠ The same shape blocks the KubeVirt instancetype catalogue, which is also
  cluster-scoped and also named rather than created — two independent features wanting the same
  missing mechanism, which is what turns it into a finding.
- **⚠ The cluster this provider creates is NOT reachable afterwards, and that is the M1 exit story's
  fourth step.** docs/plan/24: *"create an in-house Kubernetes cluster … → create a VPC and a Postgres
  server **in it**"*. `IClusterConnectionGrain.AttachAsync` exists, `ClusterConnectionKind.InHouse`
  exists, and `IKubeApiClientFactory` has a case for it — and **nothing in the shipping tree calls
  `AttachAsync`**, grepped rather than assumed. It is blocked by a missing *write*, not by a missing
  module edge, and `module-layering.txt` records why the edge is refused and what the alternative is.
- **⚠ The API server is deliberately unreachable from outside, and it is the second sighting of an
  upstream `ServiceSpec` with nowhere to put a firewall.** docs/plan/12 § Cross-cutting decisions
  requires an explicit CIDR allow-list on any exposure. The KubeVirt provider's `ServiceSpecTemplate`
  has exactly **one** field, `type`; Kamaji's `NetworkComponent` has `serviceAnnotations` and no
  `loadBalancerSourceRanges`. So `serviceType: ClusterIP` is rendered — **overriding a CRD default of
  `LoadBalancer`**, which is the one line standing between this row and a publicly reachable Kubernetes
  API server per tenant. `charts/managed/seaweedfs` found the first sighting and declared the whole
  exposure axis absent over it; this one declares no exposure property at all, because a property that
  renders nothing is worse than the absence.
- **⚠ `Matches` is containment for a reason that is a WEBHOOK on one type and a CRD MARKER on the
  other, and a reader who checked only for markers would get the pool wrong.** The
  `KamajiControlPlane` CRD carries five `+kubebuilder:default`s on its top-level spec; **neither
  `MachineDeploymentSpec` nor `MachineSpec` carries a single one**, and Cluster API's *mutating
  webhook* writes `replicas`, the whole rollout strategy, two labels into the selector *and* the
  template, and a `v` prefix onto the version instead. ⚠ **Measured**: an equality comparison was run
  against both, and `ManagedClusterMatchesTests.AnObjectCarryingTheCrdsOwnDefaultsStillMatches` was
  the only red thing in the tree while the shared suite stayed **58 of 58 green** — an exact
  reproduction of what `CyberCloud.Providers.Search` measured, on a different provider and a different
  defaulting mechanism.
- **⚠ The structural statelessness check's blind spot, confirmed a SIXTH time — and this sighting
  differs from the five before it.** A `readonly Dictionary` cache added to
  `ManagedClusterReconciler` left `ReconcilerConformance.CheckNoHiddenState` **green**; it failed
  `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem` *and*, unlike every earlier sighting, one
  shared-suite assertion (`APutWithADifferentBodyReachesTheClusterAsWell`) — because this cache keyed
  on a resource name the suite reuses. That is luck rather than coverage, and the cross-tenant test is
  still the one that is guaranteed to catch it.
- **⚠ The `object-matches-desired-cannot-see-an-address` limit, second sighting, and this time it was
  DEMONSTRATED.** Flattening `AgentPools.ObjectNameOf` so two clusters' pools collide on one
  `MachineDeployment` left the shared suite **58 of 58 green** and failed only two hand-written tests.
  On a bucket that mistake produces two buckets fighting over one object; here it moves every worker
  VM in a resource group between two tenants' clusters on each pass.
- **Four module edges, six projects, one `ProviderConformanceCase` per type — a tenth family with
  identical columns**, for a resource that renders three custom resources across three API groups from
  **three separate upstream projects** and whose child renders three more across three more. ⚠ It was
  expected to need a fifth edge — a cluster's kubeconfig is what would let the platform reach it — and
  it does not; the refusal and the alternative are in `module-layering.txt`.
- **The chart-annotation emitter's output is predictable by hand — a fifth and sixth sighting.** Both
  charts' `@param` blocks were written to match what `ChartAnnotationEmitter` would produce and came
  back **unchanged on the first `./build.sh Charts` run**. Only `values.schema.json` had to be
  generated.
`CyberCloud.Providers.Network` — `CyberCloud.Network/virtualNetworks` and
`virtualNetworks/subnets` on Kube-OVN, [14 § Virtual networks](../../docs/plan/14-networking.md).
**The first family whose objects are cluster-scoped**, and the first whose resources other resources
are meant to sit inside.

### What the eleventh provider measured

⚠ **It is the first family in ten to need a change to `test/CyberCloud.Cluster.Conformance`, and the
axis was one nine families had silently agreed on.** Every object the nine providers before it render
is **namespaced**, so `ReconcileDriver.NamespaceFor` — `{subscriptionId:N}-{resourceGroup}` — has kept
two tenants' identically-named resources apart for all of them without any provider thinking about
it. Kube-OVN's `Vpc` and `Subnet` are `+kubebuilder:resource:scope="Cluster"`, checked in
`pkg/apis/kubeovn/v1/vpc.go` rather than in a README, and **all 25 of the project's `kubeovn.io/v1`
kinds are cluster-scoped except `VpcEgressGateway`**. That broke the harness in **four** places, each
of which had to be found separately and each of which failed with a message pointing somewhere else:

- `EnsureCustomResourceDefinitionsAsync` hard-coded `Scope = "Namespaced"` on the derived CRD stub,
  so the definition served `/apis/…/namespaces/{ns}/vpcs` while the provider applied to
  `/apis/…/vpcs` — **404 on every cluster-facing assertion**;
- `ClusterConformanceTests`' own `ReadFromClusterAsync`, `RivalApplyAsync` and
  `DeleteFromClusterAsync`, and `SiloKillConformanceTests.ReadAsync`, all called the **namespaced**
  custom-object overloads, which answer a bare `404 page not found` with no Kubernetes `Status` in
  it — so the silo-kill suite reported *"the operation says Succeeded and `Vpc/…` is not in the real
  cluster"*, which reads as a **platform durability failure** and is a 404 from the wrong path;
- the `ListBackedClusterObjectInventory` call site passed the harness namespace unconditionally,
  which surfaced as the **discovery** error — *"does not serve kubeovn.io/v1 Vpc (as vpcs) … install
  or upgrade the operator that provides it"* — pointing at a missing CRD.

  > ⚠ **`KubeApiClient` was right the whole time and only the harness was wrong**, which is the useful
  > half. `ObjectRef.IsClusterScoped` is `Namespace.Length == 0`, and `ApplyAsync`, `GetAsync`,
  > `DeleteAsync` and `ListAsync` have all branched on it since before any provider needed it. Every
  > fix above is the harness learning what the shipping client already knew, and the scope is now
  > **derived from the case's own `ObjectRef`** exactly as group, version, kind and plural are —
  > so there is no member a provider author can get wrong by omission. **Nothing changes for the nine
  > earlier families**: every `ObjectRef` they render carries a namespace.

- ⚠ **`Matches` is containment, and for a THIRD mechanism after "the CRD defaults it" and "a mutating
  webhook rewrites it".** Checked in the CRD YAML and the Go types: across `Vpc`, `Subnet`,
  `SecurityGroup`, `IptablesEIP` and `OvnEip` there is exactly **one** `+kubebuilder:default`
  (`Vpc.spec.bfdPort.enabled=false`), and **no `MutatingWebhookConfiguration` anywhere in the
  project** — the only webhook is a validating one that is **off in a default install**. So the usual
  argument is false here, as it was for `ClickHouseClusters`. What forces containment is that the
  **Kube-OVN controller writes back to `.spec`**: `formatSubnet` fills `provider`, `vpc`,
  `gatewayType` and `enableLb`, derives `gateway`, **appends to and sorts `excludeIps`**, recomputes
  `protocol` unconditionally, and **canonicalizes `cidrBlock`** through `net.ParseCIDR` — so a tenant
  who sends `10.20.1.7/24` has `10.20.1.0/24` stored. ⚠ **A string comparison there reports drift on a
  perfectly converged subnet forever**, and `NetworkMatchesTests` runs that mistake red.
  ⚠ A trap worth naming: `grep default:` on the `Subnet` schema **hits**, and every hit is a property
  *named* `default` (`SubnetSpec.Default bool`).
- ⚠ **docs/plan/14's resource tree asks for a type this substrate cannot carry, and this is a
  refutation rather than a deferral.** It draws `routeTables/{name}` as a child of a virtual network.
  **Kube-OVN has no route-table object** — a "route table" there is a bare *string name* referenced
  from `Vpc.spec.staticRoutes[].routeTable`, with no lifecycle and nothing to observe. So the type
  would have to write into its parent's `Vpc.spec.staticRoutes`, and that array carries **no
  `x-kubernetes-list-type`**, so it is **atomic under server-side apply**: two route tables in one
  network would each converge by *erasing* the other. ⚠ And a static route is
  `{cidr, nextHop, policy}` — an **array of objects** — which `SchemaProperty.ElementKind` refuses
  outright, so the body shape has nowhere to put one either. **The same refusal blocks
  `securityGroups`' rule list**, which is why that type is owed rather than shipped, and forces
  `showIsolation`'s limits table to be flattened to prose on the way out. Three sightings in one
  family makes it the most load-bearing limit of `ResourceSchema` for this problem domain, because
  networking configuration is lists of rules almost everywhere.
- ⚠ **Where a rule `ResourceSchema` cannot express has to live was established rather than assumed,
  and the answer is "nowhere good".** docs/plan/14 requires the **API** to validate a tenant's address
  space against a per-region reserved list and *"reject with the conflicting range named"*.
  `SchemaProperty` carries `AllowedValues`, `Pattern`, `Format`, bounds and lengths — every one of
  which compares **one value against a constant** — and a CIDR-overlap check compares one value
  against a *list*, selected by *another property*, using a *relation* that is not equality. **And
  there is no provider-supplied predicate anywhere on `ResourceManagerService`'s write path**:
  `IResourceTypeBuilder` declares ten things and none of them is a validator, and `IPolicyEvaluator`
  is a *platform* singleton a provider cannot reach. So the check runs in the reconciler, after the
  `202` — the same defect class the Postgres row shipped — and it is named as a defect at
  `charts/managed/kube-ovn-vpc/conformance.yaml § owed`, `address-space-is-validated-after-202`, with
  the one seam that would close it. **This is the fourth family to record a cross-property rule it
  could not express and the first where the rule is the *subject* of the resource type.**
- ⚠ **What DID stay at the API is a consequence of not copying docs/plan/14's own spelling.** That
  document draws `addressSpace: [10.20.0.0/16]` — an array — and ADR-012's fifth surface **refuses
  `@pattern` on an array**, the gap `charts/managed/kafka` records as `cidr-shape-is-unenforced`.
  Two typed properties (`v4`, `v6`) instead of one list keeps the pattern enforceable, so a malformed
  prefix is refused with a `400` and a JSON Pointer *before* the `202` — and it models
  docs/plan/14 § IPv6's *"a v4 prefix, a v6 prefix, or both"* exactly, which a bag cannot.
- ⚠ **One interaction bit three times in one family, and it is worth writing down once.**
  `SchemaProperty.Incoherences` runs a declared `DefaultJson` through the property's **own**
  constraints, at **class initialisation** — so an optional patterned string whose default is `""` is
  not a validation that never fires, it is a `TypeInitializationException` that takes down the silo.
  And `ChartAnnotationEmitter` writes the chart's YAML literal from `DefaultJson`, so a **required**
  patterned string with no default emits `value: ""` and `./build.sh Charts` refuses the chart because
  `helm lint` runs it against its own defaults. The two fixes are opposite: the optional half needs a
  pattern that admits empty (`Cidr.OptionalV6Pattern`), the required half needs a **default on a
  required property**, which looks contradictory and is the only lintable answer.
- ⚠ **The first type in the tree whose only quota meter is `Resources`, and that is a shape rather
  than an under-declaration.** Every provider before this one draws `vcpu`, `memoryGb` and
  `storageGb` because every one provisions **pods**. A `Vpc` is a logical router in OVN's southbound
  database: no pod, no disk, nothing attributable. ⚠ And the tempting wrong answer on the subnet is an
  **address count** — a /16 holds 65 534 — which would consume a tenant's whole allowance for
  something that is not scarce. What *is* scarce is public IPv4, and docs/plan/14 puts that on
  `publicIpAddresses`, which is owed.
- ⚠ **The isolation claim is data, on the `MariaDbServers.CompatibilityClaim` model, because
  docs/plan/14 makes overclaiming the named risk of this row.** `VirtualNetworks.IsolationClaim` is
  one sentence — *"network-layer tenant separation … on shared hardware"* — every registered summary
  derives from it rather than restating it, and `NetworkDeclarationTests` walks a forbidden-word list
  (`isolated`, `encrypted`, `dedicated hardware`, `guaranteed`, …) against all of them.
  `IsolationLimits` is the four-row table of what is **not** claimed, each row naming what to ask for
  instead. ⚠ `POST …/showIsolation` returns it, which makes this the only action in the catalogue
  whose **content** is ready and whose **plumbing** is not — every other one waits on a Vault or a
  usage pipeline.
- ⚠ **The structural statelessness check's blind spot, confirmed a SIXTH time**, in a sixth family.
  Both halves are in `NetworkReconcilerTests` and both were run red against the counter-example. ⚠ The
  cross-tenant test ends differently here than in every family before it: those assert the two objects
  landed in different **namespaces**, and these land in **no namespace at all**, so what has to differ
  is the **name**.
- **Four module edges, six projects, one `ProviderConformanceCase` per type** — a tenth family with
  identical columns, over five schemas, two types, a child, and four Kube-OVN kinds surveyed. ⚠ It is
  also **the first family other families will want a line TO** rather than from: docs/plan/13's VMs
  and containers have to name a subnet. `module-layering.txt` records the refusal and the sanctioned
  route **before the first consumer exists**.

**What landed: `virtualNetworks` and `virtualNetworks/subnets`.** `routeTables` is **refused** with
the argument above; `securityGroups`, `publicIpAddresses`, `dnsZones`, `loadBalancers` and
`vpnGateways` are **owed**, each with what was learned about it at `NetworkProvider`'s remarks rather
than left as a bare gap.

### What the eleventh provider's second pass measured

⚠ **`virtualNetworks/securityGroups` landed, and the blocker it was owed for was solved by reshaping
rather than disputed.** The blocker was real and is unchanged: `SchemaProperty.ElementKind` still
refuses an array of objects, and Kube-OVN's `SecurityGroupRule` is one. **What changed is the
question.** docs/plan/14 asks for a security group, not for a JSON transcription of that struct, and a
security group is expressible in scalars once you take the substrate's own composition model
seriously: **a group is one coherent allow-set and a port carries several** — a port's
`…kubernetes.io/security_groups` annotation is a *comma-separated list of group names*, so the arity a
single group needs is **one**. The remote is then a v4 slot and a v6 slot (`addressSpace`'s shape, so
`Cidr.V4Pattern` stays declarable) and the ports are one patterned string per protocol.

- ⚠ **The reshape validates MORE at the API, not less, and that is the part worth generalising.** The
  reflex is that a string is a weaker type than an array of numbers. It is the opposite here:
  `Minimum`/`Maximum` are **property** constraints on `SchemaProperty` and there is **no per-element
  bounds member**, so an `Array` of `WholeNumber` could carry no range check at all — and ADR-012's
  fifth surface refuses `@pattern` on an array outright. `PortRange.OptionalListPattern` is an exact
  1–65535 grammar, six alternation branches with **disjoint leading digits** so it stays linear, and
  it refuses `0`, `65536` and `99999` with a `400` and a JSON Pointer **before the write path
  answers**. ⚠ It is deliberately *unlike* `Cidr.V4Pattern`, which is shape-only — a complete IPv6
  grammar in one expression is a catastrophic-backtracking hazard on a request path and a bounded
  decimal integer is not. **So this family now enforces exactly as much as each property can bear,
  rather than one rule for all of them.** What is left after the 202 is one relation, `min <= max`.
- ⚠ **Failure class (b) was answered from the substrate and the answer is the good one.** Read in
  `pkg/ovs/ovn-nb-acl.go`: `CreateSgDenyAllACL` installs `outport == @{pg} && ip` and
  `inport == @{pg} && ip` with action **drop** at `SecurityGroupDropPriority` (2003), `CreateSgBaseACL`
  adds ARP/ICMPv6/DHCP/VRRP at 2005, every rule this type writes lands at 2004, and
  `pkg/controller/security_group.go` has **no special case for an empty rule list**. So an empty
  security group **permits nothing** and a tenant cannot reach "allow everything" by omission. The
  schema therefore has no `defaultPolicy` property — there is one policy and the substrate chose it —
  and the only field that grants unwritten traffic, `allowSameGroupTraffic`, defaults off and is sent
  **explicitly**, because "the substrate's zero value happens to be safe" is a fact about a version of
  Go source rather than a property of the resource.
- ⚠ **The first object in the family whose spec the controller does NOT rewrite, and containment is
  still right.** Every write `pkg/controller/security_group.go` makes is `patchSgStatus`, a merge patch
  against the `"status"` subresource; there is no spec update anywhere in the file. Three files in this
  family argue containment from "the controller writes back to `.spec`" and that argument does not
  hold here. Containment applies anyway for the general reason, which is the more durable one.
- ⚠ **Atomic-under-SSA is fatal for `routeTables` and harmless here, and the difference is the
  ownership rather than the marker.** Neither `ingressRules` nor `egressRules` carries an
  `x-kubernetes-list-type`. Two `routeTables` resources would have shared **one** `Vpc`'s array; one
  security group owns its whole object, so there is no second writer for atomicity to hurt. **"No
  list-type marker" is not by itself a refusal** — it is a refusal only when two resources would write
  one array, which is worth separating because the first family to meet it wrote them down together.
- ⚠ **THREE DECLARED ACTIONS IN THIS TREE WERE ANSWERING `500`, AND TWO OF THEM WERE THIS FAMILY'S.**
  When these types shipped, no provider had an action handler and there was nowhere to put one. The
  seam exists now, and `ActionDispatcher` **refuses a synchronous action whose `HandlerType` is
  null**, by name, as an `InternalError`. So `showIsolation` — recorded at the time as *"the only
  action in the catalogue whose content is ready and whose plumbing is not"* — was publishing a `500`
  for the platform's own statement of what it does **not** protect a tenant from. All three now have
  handlers, and `NetworkDeclarationTests` asserts every declared action names one **and** that the
  instance reports its own type and action, which `ProviderBuilder.Action` cannot check because it
  holds a `Type`.
- ⚠ **"Nothing in the manager reads `SoftDeleteDays`" is FALSE and eleven files say it.**
  `ResourceManagerService.DeleteAsync` branches on `SoftDeleteDays > 0` and calls
  `IResourceIndexGrain.SoftDeleteAsync` instead of `ReleaseAsync`; `OperationSpec.SoftDelete` carries
  it forward and `OperationGrain` **withholds the committed quota until a purge**. ⚠ **What is still
  missing is the half a tenant needs: `RestoreAsync` and `PurgeAsync` have no HTTP route.** So a window
  declared today parks the name *and holds the quota* for its whole length with no way to recover the
  resource or release it early. That is now the stated reason no type here declares one — a live
  refutation of the sentence every provider in the tree copies, and the decision it changes is
  `publicIpAddresses`', where the meter being withheld is the platform's scarcest.
- ⚠ **A short-name list that nobody noticed was out of date proved nothing.** `NetworkDeclarationTests`
  checked two short names against **ten** group keys and twelve existing aliases; `containerservice`,
  `aks` and `nodepool` had been in the tree the whole time and were never typed in. Nothing collided,
  so nothing broke — the check was luck. All three are in now, and the list's own remarks already
  predicted exactly this.
- ⚠ **THE SECOND HARNESS CHANGE THIS FAMILY HAS NEEDED, AND THE SHAPE IS IDENTICAL TO THE FIRST.**
  `ProviderTestCluster.Handlers()` and `ClusterConformanceHarness.Handlers()` each built a **bare**
  `ServiceCollection` holding nothing but the handler types. That worked for exactly as long as every
  handler had a parameterless constructor — one did, for one provider. The first handler to take an
  `IClock` failed with *"Unable to resolve service for type 'CyberCloud.Core.Time.IClock' while
  attempting to activate '…'"*, thrown from `ActionDispatcher`'s `GetService`, which names the
  **handler** and reads as a provider bug. ⚠ **It is a harness bug, and the proof is that production
  works**: `AddCyberCloudResourceManager` registers `IClock`, `IClusterConnectionFactory` and
  `ISecretResolver`, and `AddCyberCloudProvider` puts the handler into *that same* container — so a
  separate container with strictly less in it made the suite ask less than the platform provides.
  Both harnesses now register their own doubles, so a handler reads the vault its case's reconciler
  minted into and sees the world its reconciler applied to. ⚠ Both were fixed together on purpose: one
  alone would have left the Docker-backed run failing later for a cause the Docker-free run had
  already solved. **The precedent is `ClusterConformanceHarness`'s hard-coded `Scope = "Namespaced"`,
  and the lesson generalises: the harness quietly agrees with every provider so far about whatever no
  provider has yet had to think about.**
- ⚠ **`QuotaMeter.PublicIps` is a FLAT meter and the "first expressible" claim is now confirmed at the
  code.** `ResourceManagerService.AmountFor` answers `meter.Fallback ?? 1m` for a meter with an empty
  `AmountPointer`, so `.Meters(QuotaMeter.PublicIps, …)` needs no pointer, no fallback and no
  `MeterDerivation`. There is nothing left to solve on the quota side for `publicIpAddresses`.
  ⚠ **And its soft-delete question is decided in advance, because it looks obvious and is backwards.**
  "Releasing a scarce address is what a recovery window is for" — except what `SupportsSoftDelete`
  does today is park the name **and withhold the committed quota until a purge**, and there is no
  purge route. A window would hold a tenant's `PublicIps` allowance against addresses they deleted,
  for its whole length, unrecoverably. It becomes right the day a purge route exists.
- **`showEffectiveRules` is the reshape's other half rather than a third action for its own sake.** The
  cost of six scalars is that the mapping to rules is a cross product a tenant has to do in their head;
  the action publishes the expansion, in the order the fabric gets it, from the stored body. It is a
  pure function and reaches no cluster, which is what lets it be synchronous.

**What landed on this pass: `virtualNetworks/securityGroups`, plus handlers for all three of the
family's actions.** `publicIpAddresses`, `dnsZones`, `loadBalancers` and `vpnGateways` remain
**owed**; `routeTables` remains **refused**.

### What the eleventh provider's third pass measured

⚠ **`publicIpAddresses` landed, and it is the first type in the platform that draws
`QuotaMeter.PublicIps`.** That meter has been in `QuotaGrain`'s defaults — 20 per subscription — since
before this family existed and **nothing had ever drawn it**. The reason was recorded on the previous
pass and held: every provider that reached for it wanted a *conditional* draw (an address only when a
body asked for external exposure) and `QuotaGrain.TryReserveAsync` refuses a non-positive amount by
name. An address resource draws exactly one, unconditionally, and `ResourceManagerService.AmountFor`
answers `meter.Fallback ?? 1m` for an empty `AmountPointer`, so `.Meters(PublicIps, Resources)` is
**flat** — no pointer, no fallback, no `MeterDerivation`.

- ⚠ **What keeps the draw unconditional is an ABSENCE, and it is the finding that generalises.** The
  obvious property to add is `ipVersion`, and it must not be added — twice over. First, for
  `NetworkSubnets`' `protocol` reason: the families an EIP gets are decided by the **external pool**
  it comes from (`acquireIPAddress` returns whatever that subnet carries), so the control would be one
  the substrate ignores. Second, and harder: **a tenant who could ask for an IPv6-only address would
  be asking for a resource that draws zero scarce addresses**, and a zero draw is the exact thing
  `TryReserveAsync` refuses. So "a meter that may draw zero" is the seam an IPv6-only address wants,
  and it is `CyberCloud.Tenancy`'s rather than a provider's. **The absence of a property is what makes
  a meter expressible** — recorded because the reflex is to add the property and discover the refusal
  from a 500.
- ⚠ **THE FIRST TYPE IN THIS FAMILY THAT IS NOT A CHILD, AND THE SUBSTRATE DECIDED IT.** Three of the
  four types here are a network and two things inside one, and a fourth reads as though it should be
  too. **An `OvnEip` names no VPC**: it is allocated from the *operator's* external subnet and attached
  to a tenant's routing domain later, by a separate `OvnFip`/`OvnDnatRule`/`OvnSnatRule` object that
  names it. docs/plan/14 § Load balancing spells the type `CyberCloud.Network/publicIpAddresses`, with
  no network segment, and the substrate agrees. ⚠ The consequence is the safety answer: **an unattached
  address is inert**, structurally rather than by a default, which is the second time in this family
  that failure class (b) has come back safe.
- ⚠ **AN EMPTY STRING IS NOT AN ABSENT KEY, ON A SCALAR, AND HERE IT IS A DEADLOCK.**
  `CyberCloud.Terminal/consoles` found this on lists — every optional list on a built-in object is
  `omitempty`, so an empty list applied comes back with no key. This is the same shape one level down
  and with a worse symptom: `createOrUpdateOvnEipCR` writes the **allocated** address into
  `spec.v4Ip` through a full `OvnEips().Update(...)`, taking field-manager ownership of it, so an
  apply carrying `v4Ip: ""` claims the same field at a different value and **every later apply answers
  `ApplyResult.Conflict`** — the resource sits in `InProgress` forever on an address that was
  allocated correctly the first time. The renderer emits `v4Ip`/`v6Ip` **only when the body asked for
  a particular address**, in both the C# and the Helm halves, and `NetworkPublicIpTests` pins both.
- ⚠ **`Matches` met the family's canonicalisation trap in its SECOND shape, and the two are worth
  distinguishing.** `NetworkSubnets.Matches` compares parsed networks because the controller
  **rewrites what it was sent**. Here the controller **fills in what it was not**: a body that asked
  for no particular address will always disagree with the object on `spec.v4Ip`, and a comparison that
  insisted would report drift on an address allocated exactly as asked, forever. So an address is
  compared **only when one was requested**. Both directions are run in `NetworkPublicIpTests` against
  a hand-written controller-shaped read-back, because there is no Kube-OVN in any harness here.
- ⚠ **THE FAMILY'S "SEND IT EXPLICITLY" RULE INVERTED ONCE, AND THE DIFFERENCE IS WHOSE DEFAULT IT
  IS.** `NetworkSubnets.SubnetJson` sends `spec.vpc` because letting it default binds the subnet to
  the *platform's own* VPC. This chart deliberately does **not** send `spec.externalSubnet`, because
  `handleAddOvnEip` falls back to `c.config.ExternalGatewaySwitch` — the operator's
  `--external-gateway-switch`, default `external` — and that name is a property of the **deployment**
  which this repository cannot know. **There the substrate's default is wrong; here it is the only
  right answer available.** ⚠ **The hazard that argument had to survive was checked rather than waved
  at**: both delete paths call `ReleaseAddressByPod(eip.Name, eip.Spec.ExternalSubnet)` with the field
  left empty, which reads like an address that never returns to the pool — and `pkg/ipam/ipam.go`
  releases from **every** subnet when the name is empty, so it is a superset rather than a no-op.
  ⚠ **And the field is not written back** on an object this platform applied: only the controller's
  *create* branch sets it, so the pool it chose is observable on the `ovn.kubernetes.io/subnet` label
  instead — a different label namespace from ADR-013's seven, so the two do not fight.
  `charts/managed/kube-ovn-eip/conformance.yaml § owed`,
  `the-external-pool-is-the-operators-default` — the same shape as `reserved-list-is-compiled-in`.
- ⚠ **THE FIRST TYPE IN THE TREE WITH NO MUTABLE PROPERTY, AND THE SHARED SUITE CANNOT SAY SO.** Read
  firsthand in `pkg/controller/ovn_eip.go` at `v1.16.2`: `handleUpdateOvnEip` refuses a changed
  `v4Ip`, `v6Ip`, `macAddress` and `type` — four errors, one per field, every one beginning *"not
  support change"* — and `handleAddOvnEip` returns early once `status.macAddress` is set. **An
  `OvnEip` is wholly immutable once ready.** `ProviderConformanceCase.ChangedBody` is `required` and
  must "change something the reconciler applies", so the case varies the requested address: that
  proves the renderer reaches the cluster and **cannot** prove the update takes effect. ⚠ It compounds
  with a platform gap that is already written down — `SchemaProperty.Immutable` is *a declaration with
  no enforcement*, by its own remarks — so the platform accepts the PUT and reports `Succeeded` while
  the fabric logs a refusal nobody sees. Named as a defect at
  `charts/managed/kube-ovn-eip/conformance.yaml § owed`, `an-allocated-address-cannot-be-changed`.
  **This is the first case in the tree where a provider needed the suite to admit a type with no
  update axis, and the suite was left alone rather than changed under four other agents.**
- ⚠ **A SECOND HYPHENATED PLURAL, WHICH KILLS THE RULE THE FIRST ONE SUGGESTED.**
  `+kubebuilder:resource:...path="ovn-eips",singular="ovn-eip"`, read firsthand and confirmed in the
  CRD Kube-OVN's own chart installs. Two of the four kinds this family renders hyphenate and two do
  not, so *"Kube-OVN pluralises by lower-casing the kind"* is wrong half the time — and
  `ClusterConformanceHarness` derives its CRD stub's path from `GroupVersionKind.Plural`, so a guess
  installs a definition at a path the apply never reaches and the symptom is a **discovery error
  naming a missing operator**.
- ⚠ **THE SHORT-NAME LIST WAS OUT OF DATE AGAIN, AND FOR THE SECOND CONSECUTIVE PASS NOTHING COLLIDED
  BY LUCK.** The previous pass added `containerservice`, `aks` and `nodepool` and wrote down that a
  list nobody notices is stale proves nothing. Three more group keys (`monitor`, `terminal`,
  `containerregistry`) and three more short names (`workspace`, `shell`, `registry`) had landed since,
  so `vnet`, `subnet` and `secgroup` were being checked against **eleven of fourteen**. ⚠ Two of the
  three missing short names are declared through a `const string ShortName` rather than a literal at
  the call site, **so a `grep 'shortName: "'` misses them** — which is exactly how the list went stale
  twice. `publicip` is checked against all fourteen keys and all seventeen names, as literals.
  **⚠ CLOSED 2026-08-19, and the lists are gone from all ten suites that held them.** `CliTokens`
  derives the group key, the command name and the short name from what is registered, and
  `ProviderRegistry.Build` refuses a collision at silo start with a message naming **both** ends —
  which is what `IResourceTypeBuilder.Display`'s remarks had promised all along and nothing checked.
  `CliEmitter.Emit` refuses it at generation and `DerivedSurfaces.CliProblems` reports it against the
  checked-in tree; `cli/cyc.Tests` asks the same question of the embedded verb tree, which is the only
  place in `dotnet test` that sees every provider without breaking § Hard rule.
  **⚠ AND THE LISTS WERE ANSWERING THE WRONG QUESTION, WHICH IS WHY NOTHING THEY MISSED EVER
  COLLIDED.** Measured against `System.CommandLine` 2.0.10 — the pinned version, in a throwaway
  program: the token dictionary is **per parent command**, not one per tree. `cyc monitor network`
  parses cleanly while `network` is a top-level group, so a short name equal to *another* group's key
  cannot collide at all; of the fourteen keys each list checked, thirteen were unfalsifiable. What can
  collide is a short name equal to its **own** group's key, a **sibling's** command name, or a
  **sibling's** short name — and no list checked any of the three. The reserved-group assertions in
  three suites were wrong for the same reason: an alias sits under a group, never at the root beside
  `cyc login`.
- ⚠ **The only action in the catalogue that returns the resource's own reason for existing.** Every
  other declared action reports a refinement — how full a subnet is, what a rule set expands to, what
  an isolation claim does not cover. `POST …/showAllocation` returns **the address**, which is not in
  the body (the body is what was *asked* for), is derivable from nothing, and lives on
  `OvnEip.status.v4Ip` and nowhere else. It carries `ready` alongside it, because the controller
  writes the address as soon as IPAM allocates and `ready` only after a separate
  `patchOvnEipStatus(key, true)`, and an address without the flag is one a tenant points DNS at a few
  seconds too early.

**What landed on this pass: `publicIpAddresses`.** `dnsZones`, `loadBalancers` and `vpnGateways`
remain **owed** — each with what was learned about it at `NetworkProvider`'s remarks — and
`routeTables` remains **refused**. ⚠ **`dnsZones` is owed for a reason that is not software**:
docs/plan/14 is explicit that *"the provider is 1.5 EM; the operations are the cost"* and that **the
decision whether the platform runs authoritative DNS or fronts a wholesale provider has not been
taken**. A resource type declared before that decision would take it by accident, and the row that
would take it is `zoneType: public`.

### What the twelfth provider measured

`CyberCloud.Providers.Terminal` — `CyberCloud.Terminal/consoles`,
[docs/plan/19 § `CyberCloud.Terminal/consoles`](../../docs/plan/19-cloud-terminal-and-virtual-desktop.md),
M1 · 1.5 EM, and step 6 of [docs/plan/24](../../docs/plan/24-roadmap.md)'s M1 exit story. **The first
row in the catalogue whose product is an interactive session rather than a converged object**, and
that single sentence is what every finding below is downstream of.

- ⚠ **`Converged` had to be redefined before anything could be built, and the pod is not in it.** The
  reconciler applies three durable objects — a `PersistentVolumeClaim`, a `ServiceAccount` and a
  `NetworkPolicy` — and **never a `Pod`**. The shell is applied by the `connect` action and deleted by
  the idle reclaim, because a reconciler that applied it would re-create it on the next reminder after
  every reclaim and the drift scanner would repair a resource working exactly as designed. So
  `Converged` means *"the console can be attached to"*, `ObserveAsync` reports the pod in
  `Summary` and nowhere else, and `Exists` is decided by the durable three alone.
- ⚠ **The obvious readiness gate is unimplementable on the substrate this platform ships, which is
  what kept this row out of `ManagedClusterReconciler`'s hole.** That reconciler is the only one whose
  `Converged` reads a `status`, and doing so named `ClusterReadinessKind.NotReported` — an object no
  controller has written a status onto converges, because neither harness can produce anything else.
  A console's equivalent would be `status.phase == "Bound"` on the claim, and it **deadlocks**: k3s'
  default StorageClass binds `WaitForFirstConsumer`, a console deliberately has no pod until somebody
  attaches, and `connect` refuses a console that has not converged. Converge would wait for the pod
  and the pod would wait for converge. So this row reads **no status at all** and pays for it with a
  `Converged` that promises less — `charts/managed/cloud-shell/conformance.yaml § owed`,
  `converged-is-not-attachable`, is the other resolution of the same shape.
- ⚠ **The unsafe default here is a Kubernetes default, not a product's, and there were five of
  them.** Failure class (c) has three earlier sightings — SeaweedFS' anonymous admin, Qdrant's unset
  `api_key`, MariaDB's root password — and every one was a field an upstream product left blank. This
  row's are the API's own: a pod with no `serviceAccountName` mounts the namespace's `default` token
  (in an image containing `kubectl`); `allowPrivilegeEscalation` **defaults to true**; an omitted
  `readOnlyRootFilesystem` is a writable root; an omitted `capabilities.drop` is the runtime's set,
  including `NET_RAW`; an omitted `seccompProfile` is `Unconfined`. `ConsolePodTests` reads each off
  a body that asks for nothing and names the default it closes.
- ⚠ **`docs/plan/19` asks for a NetworkPolicy that cannot be written, and the correction is the
  finding.** *"a `NetworkPolicy` denying access to the platform's own namespaces"* has no spelling: an
  egress rule is an allow-list, there is no `deny`, and `ipBlock` may not be combined with a
  `namespaceSelector` in one peer — so a rule allowing `0.0.0.0/0` allows the platform's pods too,
  because their addresses are in the cluster CIDR. The requirement is met the only way it can be: the
  tenant's namespaces are allowed **positively** and every private range is excised from the public
  rule, so the platform is excluded by construction rather than by exception.
- ⚠ **And the tenant-wide half of that rule matches nothing, because nothing in this repository
  labels a namespace.** `ReconcileDriver.NamespaceFor` derives a name and every reconciler assumes it
  exists; no component owns namespace creation. So a shell today reaches its own resource group and no
  further, which means **docs/plan/24's M1 exit story does not work across resource groups** —
  `psql` into a Postgres server in another group is refused by this policy. It fails closed, and the
  rule is rendered now so that the day namespaces are labelled every existing console gains the reach
  with no api-version change.
- ⚠ **The first family whose product is cross-provider reach, and it still needed only four module
  edges.** Rule 2 should have broken here — a terminal exists to reach the tenant's other resources —
  and it did not, for a reason that is not the usual "it would have been a shortcut": what the
  terminal renders is a policy over **label selectors**, and a label is a string. Reach expressed as a
  selector needs no reference to the thing selected. That is cheaper than the sanctioned
  resource-id route rather than more expensive, and `module-layering.txt` records it.
- ⚠ **The first type for which declaring a meter would be WRONG rather than impossible.** Three
  earlier rows report undeclarable meters — a string where a number was wanted, a conditional that
  derives zero, a counter that only exists once traffic has flowed — and all three are registry gaps.
  This one is not: a state-based `vcpu`/`memoryGb` reservation derived from the body would hold 2 vCPU
  against a subscription for a terminal that was closed a week ago, which is the exact failure
  docs/plan/19 calls the design constraint. `StorageGb` (the home volume, allocated whether anybody is
  attached or not) and `Resources` are declared; the other two are a usage event the session grain
  owes.
- ⚠ **The first family that renders only core-group objects, which inverts what its cluster suite
  proves.** Every earlier provider renders a custom resource, so `ClusterConformanceHarness` had to
  derive a CRD stub before a single assertion could address anything. Nothing here needs one — so a
  green run is genuine evidence the objects are real and **no** evidence the derivation works. It is
  also the row where green proves least about the product: a `NetworkPolicy` applies and reads back
  identically in a cluster that enforces it and in one that does not.
- ⚠ **The structural statelessness check's blind spot, confirmed a SEVENTH time.** Both halves are in
  `ConsoleReconcilerTests` and both were run red against the counter-example. ⚠ On this family the
  field a cache would hold is a **security control**: the rendered `NetworkPolicy` carries the
  tenant's own GUID in a selector, so a reconciler that remembered one would give tenant B a policy
  naming tenant A. The cross-tenant test asserts the selector, not only the sizing.
- **Four module edges, six projects, one `ProviderConformanceCase`** — a twelfth family with identical
  columns, over a type the shared suite cannot see the product of. The suite was not touched.

**What landed:** the resource, its four rendered objects, the `connect` and `terminate` actions, the
chart and the auditing surface. **What is owed** is led by `the-session-grain-does-not-exist`:
docs/plan/19's exec stream, resize channel, ring buffer and idle timer are not built, `TerminalHub`
still refuses by name, and **nothing in this repository builds the shell image** —
`build/Build.Images.cs` publishes .NET hosts and its header forbids a Dockerfile, and this is the
first image in the tree that is not a .NET application. All of it is at
`charts/managed/cloud-shell/conformance.yaml § owed`.
`CyberCloud.Providers.Monitor` — `CyberCloud.Monitor/workspaces` over the platform's own
VictoriaMetrics and ClickHouse, [16 § `CyberCloud.Monitor/workspaces`](../../docs/plan/16-observability.md),
**M1 · 2.5 EM**, and step 7 of [24](../../docs/plan/24-roadmap.md)'s M1 exit story — *"see metrics and
logs"*. **The first family whose product is not a workload at all**, and the first to declare
`SupportsSoftDelete` — then to withdraw it, for a reason it had to measure to find, and then to
declare it again once the platform closed what the measurement found.

### What the twelfth provider measured

- **⚠ A WORKSPACE IS A TENANCY IN A STORE THE PLATFORM ALREADY RUNS, NOT A DEPLOYMENT — and this row
  is the other half of the sentence `CyberCloud.Providers.Analytics` started.** That family
  established that the platform's ClickHouse is *not* `CyberCloud.Analytics/clickhouseClusters`. This
  one establishes what it *is*: the platform's ClickHouse and VictoriaMetrics are reached **through**
  `CyberCloud.Monitor/workspaces`, and this type provisions neither of them. Five facts decide it and
  the fourth is the one that makes it a correctness question rather than a modelling preference:
  docs/plan/16 § Ingest routes to *"VictoriaMetrics (accountID) | ClickHouse (per-tenant database)"* —
  coordinates inside one store; docs/plan/05 § Every store gives each store **one row per region**;
  the deployment-shaped reading is a product the catalogue already sells under another name;
  ⚠ **docs/plan/16 puts PLATFORM telemetry *"under a platform workspace. No separate stack"*, so the
  platform workspace IS a resource of this type** — and if a resource of this type provisioned the
  store, reconciling the platform's own workspace would provision the store that every reconcile,
  including that one, emits into. `CyberCloud.Ingest.Host` is deliberately not an Orleans client to
  keep that cycle broken; **both halves have to be true at once — the platform workspace is one of
  these, and the platform's stores are not — and only the shared-store reading makes them
  compatible.** And soft delete decides the same way: a soft-deleted tenancy costs disk that was
  already reserved, where a soft-deleted *deployment* would mean keeping a cluster running for a week.

- **⚠ WHAT A TYPE THAT PROVISIONS NOTHING CONVERGES IS FORCED BY THE SAME CONSTRAINT, FROM THE OTHER
  END.** The store exists before any workspace; what a workspace must make true is that the **data
  plane** knows the tenancy. docs/plan/16 calls that *"a cached map"* and does not say how it is
  filled — and `CyberCloud.Ingest.Host` not being an Orleans client means **the control plane cannot
  tell it anything by grain call**. The only store the ingest host can read without the control plane
  and that the platform already operates is Kubernetes. So the three objects are a credential, a
  routing rule and a **row**, and this is the first family in the catalogue whose applied objects are
  *configuration for a data plane* rather than a description of a workload. It needed no fifth module
  edge and no seventh project.

- **⚠⚠ docs/plan/16'S PRICED PER-SIGNAL METRICS RETENTION IS NOT A SETTING OPEN-SOURCE
  VICTORIAMETRICS HAS, AND THIS IS A REFUTATION RATHER THAN A DEFERRAL.** Checked in upstream's
  source and its own enterprise page rather than a blog: `app/vmstorage/main.go` declares
  `-retentionPeriod` **once, per vmstorage node**, and the per-tenant form — `-retentionFilter`, with
  its `{vm_account_id=~"…"}` selectors — is on `docs.victoriametrics.com/enterprise/`'s feature list,
  as is `-downsampling.period`. **An open-source VictoriaMetrics cluster cannot give two accountIDs
  two retention periods.** ADR-016 chose the engine for *"native multi-tenancy"*, which is real and is
  about isolation, not retention. Upstream's own open-source answer — *"separate logic groups of
  storages … with individual `-retentionPeriod` settings"* — is what this row takes: **one vmstorage
  group per tier, and a workspace's retention tier decides which group it is routed to.** Which is
  also why the body carries a tier *name*: a day count would have had nowhere to go.
  <br>⚠ The same review found per-tenant **cardinality and rate limits** are enterprise too
  (vmgateway), which *confirms* docs/plan/16's ingest design rather than refuting it — those caps have
  to be enforced in `CyberCloud.Ingest.Host` and cannot be delegated to the store.

- **⚠ A DISCRETE NUMERIC TIER SET IS INEXPRESSIBLE IN `ResourceSchema`, AND HERE THE INEXPRESSIBLE
  CONSTRAINT IS THE PRICE.** `SchemaProperty.AllowedValues` is legal on `SchemaKind.Text` and nowhere
  else, and its own remarks say a numeric enumeration *"is expressible as `Minimum`/`Maximum` or is a
  modelling mistake"* — but a range accepts 399 days, which no price list has a row for and which the
  storage meter would then reserve against. **Sixth family to record a rule `ResourceSchema` cannot
  state**, and the first where what it cannot state is what the tenant is charged. What it forces —
  a tier name over a platform-owned table — turns out to be better than what it forbids, and is the
  same shape the whole catalogue already uses for sizing.

- **⚠ THE QUOTA METER IS A FIFTH SHAPE, AND THE NEW PART IS THAT ONE FACTOR IS NOT IN THE BODY.**
  `CyberCloud.DBforPostgreSQL/servers` found an amount is a quantity *string*; `natsClusters`, a
  *product* of a replica count and one figure; `CyberCloud.Storage/accounts`, a *sum* over
  heterogeneous components; `clickhouseClusters`, a product **and** a sum. Here it is a sum over three
  signals of `(retention tier's days) × (that signal's GiB/day)` — and the derivation reads
  `/properties/retention/logs` and gets back the string `"standard"`, with the number 30 coming from a
  platform table. **A derivation that read the pointer and expected a number would derive nothing.**
  This is also the line where docs/plan/16 § Cost and retention honesty's *"retention is a paid
  property"* stops being a sentence: `MonitorQuotaTests.MovingOnlyTheRetentionTierMovesTheStorageAmount`
  is what fails if somebody simplifies the meter to GiB/day alone.

- **⚠ THE FIRST TYPE IN THE TREE TO DECLARE `SupportsSoftDelete`.** Eleven families declined with the
  same stated reason and docs/plan/08 § Soft delete endorsed the instinct, ending *"the declaration is
  the last step, not the first"*. The manager honours a window now, so what was left is the provider's
  own question — *does the data this type carries deserve one* — and on this type it is the least
  ambiguous case in the catalogue: **a workspace is the tenant's only copy of their logs.** A database
  has a backup and an object store has versioning; telemetry has neither, because the source of truth
  was a process that has since exited. Seven days, purge behind its own permission, purge protection
  as a declared boolean.

  > ⚠ **AND DECLARING ONE IS THE SECOND FAMILY IN TWELVE TO NEED A CHANGE TO
  > `test/CyberCloud.Cluster.Conformance`, ON AN AXIS ELEVEN FAMILIES HAD SILENTLY AGREED ON.**
  > `ClusterConformanceTests.TheLifecycleRunsAgainstARealApiServer` asserted **unconditionally** that
  > every rendered object is gone after a converged teardown, and this provider went **1 of 6 red
  > against k3s while the Docker-free suite was 27 of 27 green** — the shape that usually says the
  > *suite* is wrong rather than the provider. It was read that way, a `recoverable` branch was added
  > to both halves, and **that reading was itself wrong**: what the failure reported was a real
  > defect, and the branch encoded it as the contract. Both branches are gone again. The objects are
  > asserted gone for every type, and the recoverable arm asserts what a window actually keeps.

  > ⚠⚠ **THE DECLARATION WAS WITHDRAWN AND IS BACK, AND THE WITHDRAWAL IS THE FINDING.** Two drafts
  > of that argument were wrong before the third was measured. `IResourceReconciler.DeleteAsync` was
  > **never called** for a type declaring a window — `OperationGrain.DriveAsync` returned early and
  > ran no pass — `ParkAsync` states that *"its quota stays committed until it is purged"*, and the
  > object left standing was the **`VMUser`**, which is the one thing on this row that enforces
  > anything, because vmauth resolves it the moment it is applied. So a soft-deleted workspace was an
  > **authenticated, billed, open write path into a store the tenant believed was gone**: a collector
  > nobody reconfigured kept writing, the retention kept accruing, and the only way to stop it was a
  > purge behind a permission the tenant may not hold. ⚠ On a database or an object store a recovery
  > window merely holds disk; **here it held an open ingest endpoint**, which docs/plan/08 did not
  > anticipate because nothing before this had declared a window. **A delete that does not delete is
  > worse than no recovery window**, so it was withdrawn — the same conclusion
  > `CyberCloud.ContainerRegistry/registries` reached from its own measurement, for a reason that is
  > worse here rather than merely similar: fifteen idle Harbor objects cost money, and this costs
  > money *and* silently ingests data.
  >
  > ⚠ **What did NOT reproduce is what located the fix, and it is why a failed replication is worth
  > as much as a successful one.** That row measured a soft-deleted resource *reconciling its whole
  > data plane back*; on this type that path was not reachable, checked rather than assumed. Driving
  > the completed delete operation again returned nothing, and disabling `OperationGrain`'s
  > soft-delete branch made the pass run with `tearingDown` **true** — `OperationSpec.Kind` is
  > `Delete` — so it **destroyed** the objects instead. ⚠ **There was no re-apply anywhere.** The
  > other row's evidence was a conformance assertion reporting an **end state**, and an end state
  > cannot tell *never torn down* from *torn down and re-applied* — different bugs, different files.
  > Recording the discrepancy rather than inheriting the answer is what made it findable.
  >
  > ⚠ **And re-declaring was one line, because everything the declaration needs was kept rather than
  > deleted**: `SoftDeleteDays`, the purge-protection property the builder refuses the type without,
  > `MonitorDeclarationTests`' coverage of both, and the conformance experiment, which **skipped
  > itself loudly** instead of being removed and started asserting again on the same run.
  > `conformance.yaml § owed`, `soft-delete-was-withdrawn-and-is-declared-again`.

- **⚠ A RETENTION A TENANT CAN SHORTEN IS AN IRREVERSIBLE DATA-LOSS PATH AUTHORISED BY A REQUEST THE
  PLATFORM ALREADY ANSWERED `202` TO — fifth sighting of the missing write-path predicate, and the
  first where the consequence is destruction rather than a broken object.** docs/plan/16 prices
  retention, so it must be settable; ClickHouse expires shortened TTLs at the next merge and schedules
  an off-schedule merge when it detects expired data. The API cannot refuse it: `ResourceSchema`
  validates one body against constants with no access to the previous body, and `IResourceTypeBuilder`
  declares no validator. So `MonitorWorkspaceReconciler` reads the existing row **before it applies
  anything** and fails the pass with both day counts in the message; a refused shrink changes nothing
  and a `PUT` of the old tier reverses it. `MonitorRetentionTests.ShorteningARetentionIsExpressibleInABodyTheSchemaAccepts`
  pins *why* the check is where it is, so that whoever closes the seam finds it and can delete it.

- **⚠ `Matches` IS CONTAINMENT FOR A FOURTH MECHANISM — the API server itself — AND IT IS THE FIRST
  ONE THE CONFORMANCE HARNESS IS NOT BLIND TO.** The three known mechanisms are CRD defaulting, a
  mutating webhook, and a controller writing back into `.spec`. Two of this type's three objects are
  **core kinds**: no CRD, no operator, no webhook. What forces containment is `metadata.uid`,
  `resourceVersion`, `creationTimestamp`, `managedFields` and the seven labels `KubeCommandBuilder`
  injects. ⚠ Measured on both halves: the equality mistake fails the cluster-backed suite *and* the
  Docker-free one, because the builder has already added the labels by the time `FakeKubeCluster`
  echoes the apply back. **The hole `CyberCloud.Providers.Search` records — every family whose
  operator's CRD carries defaults — does not exist here.**

- **⚠ `ProviderConformanceCase.ObjectMatchesDesired` CARRYING NO ADDRESS IS A STRUCTURAL GAP ON THIS
  TYPE RATHER THAN AN INCONVENIENCE, AND IT WAS MEASURED.** `StorageBuckets` recorded the limit and
  `AgentPools` demonstrated it; here **identity is not one field of the output, it is the output** —
  the accountID in the `VMUser`'s path suffix, the database name, all three object names. The first
  version of the case closed over a fixed address and ran **5 of 29 red**. Handing it a made-up
  address makes the suite green and the comparison meaningless, so the case calls a second,
  deliberately weaker `MonitorWorkspaces.MatchesShape` that checks the address-independent half and
  says so in its name. ⚠ **What that leaves uncovered is the worst bug this type can have** — every
  workspace on one accountID, which is every tenant reading every other tenant's metrics — and
  `MonitorReconcilerTests.TwoWorkspacesInTwoTenantsGetTwoAccountIdsAndTwoDatabases` is the hand-written
  test that covers it.

- **⚠ THE `accountID` IS FOLDED RATHER THAN ALLOCATED, WHICH IS WHY THIS TYPE NEEDS NO GRAIN AND IS A
  NAMED LIMIT RATHER THAN A SAFE DERIVATION.** VictoriaMetrics' accountID is a 32-bit integer in a URL
  path, so the resource GUID folded to 32 bits is stable, recomputable anywhere and needs nothing
  remembered — which is the twelfth family to report no grain and the first where the temptation was
  a real allocator. A fold is not a bijection: the birthday bound is a coin-flip around 77 000
  workspaces, inside target scale, and a collision is two tenants sharing metrics. `accountID:projectID`
  is accepted by VictoriaMetrics and makes the space 64 bits, which closes it without durable state.
  `charts/managed/monitor-workspace/conformance.yaml § owed`, `accountid-is-folded-not-allocated`.

- **⚠ THE FIRST PROVIDER-SUPPLIED LABEL IN THE TREE.** ADR-013's seven identify a *resource*; the
  ingest host needs to select every workspace's **row** across every namespace in a region, and
  `cybercloud.io/resource-type` is on the `Secret` and the `VMUser` too. Encoding "the one whose kind
  is ConfigMap" would make the data plane depend on this provider's object list, so the row carries
  `cybercloud.io/telemetry-row: workspace`. Nothing above a provider validates such a label, which is
  why `MonitorOpenApiCasingTests` runs it through `LabelSyntax` — an illegal one is refused at apply
  time, per object, rather than at build time.

- **⚠ THE MIXED-CASE UPSTREAM FIELD THAT NOTHING WOULD CATCH.** `VMUserSpec`'s JSON tags are camelCase
  (`targetRefs`, `passwordRef`) and a `TargetRef`'s are snake_case (`target_path_suffix`,
  `query_args`) — checked in `api/operator/v1beta1/vmuser_types.go`. The operator's own **prose** calls
  the last one *"targetPathSuffix"*. A document written from the prose applies cleanly, reads back
  cleanly, converges, and is **ignored by vmauth** — which means the workspace's writes land on
  whatever tenant the `url_prefix` defaulted to. The cluster-backed harness installs an *open* CRD
  stub, so it cannot catch it either; `MonitorReconcilerTests.TheTargetPathSuffixIsSpelledTheWayTheGoTagSpellsIt`
  is a unit test against a literal, and it is the only thing in the tree that does.

- **⚠ THE `Secret` IS RENDERED IN C# ONLY, WHICH IS `charts/managed/seaweedfs`' OMISSION ON ITS SECOND
  SIGHTING.** Its content is a credential the reconciler reads out of the vault inside one pass, and
  the body has no property carrying it. A Helm template for it would either publish an empty key that
  `helm lint` accepts and vmauth refuses, or put a live credential in a values file. Second action
  handler in the tree, and the first where the synchronous-with-handler shape is the *only* one that
  works: `longRunning` would answer `202` and re-run the reconciler.

- **⚠ THE STRUCTURAL STATELESSNESS CHECK'S BLIND SPOT, CONFIRMED A SEVENTH TIME**, in a seventh
  family. Both halves are in `MonitorReconcilerTests` and both were run red against the
  counter-example.

- **The chart-annotation emitter's output is predictable by hand — a seventh sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run** — *"unchanged, 0 problem(s)"*. Only
  `values.schema.json` had to be generated.

- **Four module edges, six projects, one `ProviderConformanceCase`** — a twelfth family with identical
  columns, over a resource that provisions nothing. ⚠ It is also the family most obviously *owed* a
  fifth line and taking none, and `module-layering.txt` records why **both** directions are refused:
  nothing may reach this family to emit telemetry (that is the dependency cycle), and this family
  reaches nothing to bill (that is a `MeterDerivation` the manager already reads).

**What landed: `workspaces`.** `collectors` and `alertRules` are M2 and out of scope;
`workspaces/ingestKeys` is **owed** with the reason — docs/plan/16 wants rotation *"with a grace
period"*, which is two live credentials at once, and `ISecretWriter` mints once. ⚠ **The largest
single gap is that nothing consumes the ingest row**, because `CyberCloud.Ingest.Host` does not exist;
the `VMUser` half is enforced by vmauth the moment it is applied and everything else is a promise with
a schema. Every gap is at `charts/managed/monitor-workspace/conformance.yaml § owed`.

`CyberCloud.Providers.ContainerRegistry` — `CyberCloud.ContainerRegistry/registries` on Harbor,
[13 § Container Registry](../../docs/plan/13-compute-vm-containers.md), **M1 · 1.5 EM**. **The family
whose operator turned out not to exist**, and the one that declared the first recovery window in the
tree, measured it against a real API server, and took it back.

### What the thirteenth provider measured

- **⚠ ADR-010 CLAUSE 1'S SURVEY NAMES A THIRD OPERATOR THAT CANNOT BE USED, AND THREE SIGHTINGS MAKE
  IT A FINDING ABOUT THE CLAUSE RATHER THAN ABOUT THREE SERVICES.** `goharbor/harbor-operator`
  answers the GitHub API with `"archived": true` and
  `"description": "[DEPRECATED] Kubernetes operator for Harbor service components"`; its README opens
  *"Due to low activity in maintanance this sub-project we are archiving it"*, its last **stable**
  release is **v1.3.0, 2022-07-02**, and the only newer tag is a release candidate that never
  shipped. Checked against the API on 2026-08-18. `charts/managed/nats` found `nats-operator`
  archived; `SearchProvider` found `qdrant/qdrant-operator` answering `404`; `DocumentDbAccounts`
  found the FerretDB organisation holds no operator at all. **Clause 1 calls itself *"the operator
  selection per managed service"* and is, four rows in, a survey of *software choices* that is only
  sometimes a survey of operators.** The correction belongs in ADR-010 and is reported rather than
  made here.
- **⚠ SO THE OPERATOR-LESS SHAPE IS TESTED AT THREE TIMES ITS PREVIOUS WIDTH, AND THE SHAPE HOLDS.**
  `CyberCloud.Messaging/natsClusters` established it at five objects; this renders **fifteen** — one
  `Secret`, one `ConfigMap`, six `Service`s, three `StatefulSet`s, three `Deployment`s and a
  `PodMonitor` — and needed **four module edges, six projects and one `ProviderConformanceCase`**,
  the same columns as the eleven before it. `test/CyberCloud.Conformance` was not touched. ⚠ The
  catalogue's running claim that *"object count is not a measure of a service's size"* is now
  bracketed at **1 and 15**, and the top of the range is a row docs/plan/13 costs at **1.5 EM**,
  less than the three-object managed-Kubernetes row's 4.0.
- **⚠ THE SHARPEST SIGHTING OF THE UNSAFE-DEFAULT CLASS IN THE CATALOGUE, AND IT IS NOT AN ABSENCE —
  IT IS A PUBLISHED CONSTANT.** The three earlier sightings are things that are *unset*: SeaweedFS
  with no identities file serves anonymous **admin**, Qdrant's chart leaves `service.api_key` unset,
  MariaDB's operator generates a root password. `goharbor/harbor-helm`'s `values.yaml` ships
  `harborAdminPassword: "Harbor12345"` and `templates/core/core-secret.yaml` consumes it with **no
  generation fallback** — while, in the same file, `secret`, `CSRF_KEY`, `JOBSERVICE_SECRET` and
  `REGISTRY_HTTP_SECRET` all end in `| default (randAlphaNum 16)`. The administrator's password is
  the one credential in that chart that is not randomised.
  <br>⚠ **Reading only the chart gets it wrong in the other direction as well, and all three layers
  had to be read.** Harbor's own core gives `HARBOR_ADMIN_PASSWORD` a `DefaultValue: ""`
  (`src/lib/config/metadata/metadatalist.go`) and seeds `('admin', '', …)`
  (`make/migrations/postgresql/0001_initial_schema.up.sql`); the 87 occurrences of `Harbor12345` in
  that repository are tests, tooling and `make/harbor.yml.tmpl`, and **none is in Go source**. And
  `src/core/main.go` applies the environment value **only when the stored salt is empty**, with no
  non-empty guard — so an unset variable seeds the administrator with the hash of the empty string,
  once, permanently, and a *later* mint would not take effect at all. That last fact is why
  `ContainerRegistryListCredentialsHandler` reads and never mints, and it is a stronger reason than
  the one `StorageAccountListKeysHandler` gives.
- **⚠ THIS ROW DECLARED THE FIRST `SupportsSoftDelete` IN THE TREE, DECLARING IT IS WHAT FOUND THAT A
  SOFT DELETE TORE NOTHING DOWN, AND THE ROW THEN WROTE THE FINDING UP AS THE WRONG MECHANISM.**
  Eleven families declined the declaration for one shared reason — the manager did not read
  `SoftDeleteDays` — and docs/plan/08 § Soft delete is built, so the question each type owes is its
  own: *can the deleted thing genuinely be handed back?* Here the answer is yes, and the mechanism is
  **Kubernetes' rather than this provider's**: a claim created by a `volumeClaimTemplate` is not
  removed by deleting the `StatefulSet`, which is why all three volume-owning components are
  `StatefulSet`s rather than `Deployment`s with claims beside them. So the declaration went in.
  <br>⚠ **Measured, not argued.** `ClusterConformanceTests.TheLifecycleRunsAgainstARealApiServer`
  failed with *"is still in the real cluster after a converged teardown"*; reordering the case's
  `Objects` showed it was not one object but **every** object, the core `Deployment` included.
  Removing that one call and changing nothing else made the same test pass, and putting it back made
  it fail again.
  <br>⚠⚠ **And the row recorded that as a soft-deleted resource REBUILDING its data plane — an active
  re-apply — which it was not.** That assertion reports an **end state**: an object is present. An end
  state cannot distinguish *never torn down* from *torn down and re-applied*, and the two are
  different bugs in different code. It was the first: `OperationGrain.DriveAsync` returned before
  running any pass for a soft delete, so nothing was ever asked to come down.
  `CyberCloud.Monitor/workspaces` declared a window the same day, could not reproduce a re-apply on
  its own row, checked three ways, and **recorded the discrepancy rather than inheriting this row's
  answer** — which is what made the disagreement findable at all. ⚠ **The transferable lesson is about
  evidence: a result cannot name a mechanism, and a write-up that names one anyway sends the next
  reader to the wrong file.**
  <br>⚠ **The defect was real either way, and it is worse than it sounds.** A tenant deletes a
  registry; the API answers, the operation converges, the resource stops being addressable — and the
  workload keeps running, sampled by docs/plan/22's usage pipeline, holding its quota, invisible to
  the tenant who would delete it again. **A delete that does not delete is worse than no recovery
  window**, so the declaration was withdrawn.
  <br>⚠ **It is declared again, because it is closed.** A soft delete now runs the reconciler's
  `DeleteAsync` exactly as a hard delete does, and the four things that make it soft happen after that
  pass reads back: the name is held, the committed quota is kept, the resource grain keeps its desired
  state, and the ReBAC parent edge moves to the subscription. The fifteen objects come down and the
  **disks stay**, which is what makes this row's window honest and what a restore re-attaches.
  `charts/managed/harbor/conformance.yaml § owed`,
  `a-soft-deleted-resource-was-never-torn-down`.
  <br>⚠ **Two smaller findings stand whatever happened to that one, and one of them grew.** Nothing
  removes a `PersistentVolumeClaim` on a purge — and now that the objects come down at the *delete*,
  a purge reaches the provider with nothing to do at all, so a purged registry returns its quota and
  leaves its disks: the largest remaining gap in this row's window. And
  `ResourceManagerService.RestoreAsync` and `PurgeAsync` are reachable from **no gateway stage at
  all**, grepped rather than assumed, so a tenant cannot exercise the window a `DELETE` now gives
  them.
- **⚠ `Matches` IS CONTAINMENT FOR A REASON THAT IS NOT MERELY FALSE HERE BUT UNAVAILABLE.** Five
  families argue it from a CRD's `+kubebuilder:default` markers or from an operator's mutating
  webhook; `KafkaClusters` and `ClickHouseClusters` found CRDs that declared none and could at least
  look. There is no CRD to look at. What forces containment is that **five of the six kinds are
  built-in**, which `NatsClusters` found first and which no family had met at this width. ⚠ Recorded
  for whoever reaches for the operator later: it carries **both** mechanisms and carries them hard —
  298 `+kubebuilder:default` markers across `apis/`, and a `MutatingWebhookConfiguration` whose
  `HarborCluster` defaulter *nils out* every non-selected variant of `spec.cache`, `spec.database`
  and `spec.storage`.
- **⚠ A RENDERED BODY CARRIES NO `kind` AND SIX KINDS ARRIVE AT ONE `Matches`, WHICH IS A SHAPE
  NOTHING HAD.** `KubeCommandBuilder` injects `kind` on the apply path, so every provider before this
  one wrote `null or "TheirKind"` and meant it. Six kinds make that case mean six things at once, so
  the fallback reads the document's own **shape** — `type` beside `data` is the `Secret`,
  `podMetricsEndpoints` is the `PodMonitor`, `volumeClaimTemplates` separates the two workload kinds
  — and a document matching none of them is `false`. ⚠ **Found by a test rather than by review**: the
  first version routed the `null` case to the workload comparison, and
  `ACredentialsSecretMissingOneFieldDoesNotMatch` went red on the *positive* half.
- **⚠ THE THIRD FAMILY TO WANT THE SAME MISSING FIFTH MODULE EDGE, AND THE FIRST WHOSE PLAN DOCUMENT
  ASKS FOR IT IN AS MANY WORDS.** docs/plan/13: *"Storage backend is the tenant's SeaweedFS bucket, so
  registry storage is billed like any other blob."* That bucket is a `CyberCloud.Storage/accounts`
  resource and `ReconcileContext` still has no resolver for another provider's. `CyberCloud.Storage`
  wants a Postgres for its filer, `CyberCloud.Analytics` wants an S3 cold tier, and **two of the three
  want to reach the same provider**. The registry keeps its images on a `PersistentVolumeClaim`
  instead, which means docs/plan/13's sentence about billing is not true of what ships — and the
  *same* seam blocks the metadata database being highly available, which is the third instance of it
  in one row.
- **⚠ THE QUOTA METERS ARE A SUM OVER HETEROGENEOUS COMPONENTS — THIRD SIGHTING — AND THE FIRST WHERE
  ONE POPULATION IS MULTIPLIED BY A TENANT-SET REPLICA COUNT AND TWO ARE FIXED.** It is
  `1 registry × preset + 3 × replicas × 250m + 2 × 250m`: three stateless components share the
  tenant's replica count, and the database, Redis and the registry each run one replica because each
  owns a `ReadWriteOnce` claim. A derivation copied from `natsClusters` is right about nothing; one
  copied from `StorageAccounts` misses the ×3.
  `ContainerRegistryQuotaTests.ChangingOnlyTheReplicaCountMovesThreeComponentsAndNotFive` is the one
  that fails on either, and it was run red against both. ⚠ The **storage** meter deliberately does
  not read `replicas` at all, which is the same fact from the other side.
- **⚠ FOURTEEN OF FIFTEEN OBJECTS NEED NO CRD STUB, WHICH IS THE SECOND SIGHTING OF A SHAPE
  `CyberCloud.Providers.Terminal` REACHED FIRST AND FROM THE OTHER END.** That family renders *only*
  core-group objects and records that its green is therefore no evidence at all that the CRD
  derivation works; this one renders fourteen built-in kinds and **one** custom kind, so it is the
  first family whose suite exercises both a real schema-validating API server and the derived stub in
  the same run.**
  `ClusterConformanceHarness` derives a definition per *custom* kind; here the only one is
  `monitoring.coreos.com/v1 PodMonitor`. So almost everything this family applies is checked by a
  **real, schema-validating** API server rather than by an open-schema stub — a `Deployment` whose
  selector does not match its own pod template is rejected there. ⚠ It still proves nothing about
  Harbor: no image is pulled, no migration runs, and **the exhaustive correctness of the environment
  set in `templates/control-plane.yaml` is checked by nothing.** That is the largest untested surface
  in this family and it is named at `conformance.yaml § owed`, `converged-is-not-serving`.
- **⚠ THE ONE GAP THIS PLATFORM CREATES RATHER THAN INHERITS, AND IT IS WORTH MORE WRITTEN DOWN THAN
  WORKED AROUND.** `goharbor/harbor-helm` renders `auth: htpasswd` into the registry's `config.yml`
  and computes the file with Sprig's `htpasswd`, which is **bcrypt**; `distribution`'s htpasswd
  backend accepts bcrypt and nothing else. .NET ships none and this repository references no package
  that does, so a reconcile pass cannot produce the hash — and a chart that rendered one the
  reconciler could not would be the two halves of the ADR-012 pair disagreeing. So no `auth:` block
  is rendered. What bounds it is the namespace: the registry's `Service` is `ClusterIP` in
  `{subscriptionId:N}-{resourceGroup}`, which holds one tenant's resources, so the exposure is to the
  tenant's own workloads rather than across tenants. What is lost is defence in depth.
  `conformance.yaml § owed` names both closures.
- **⚠ THE THIRD ROW TO DECLARE NO EXPOSURE AXIS, AND THE FIRST WHOSE BLOCKER IS NOT A MISSING UPSTREAM
  FIELD.** `charts/managed/seaweedfs` and `charts/managed/kubernetes` both found a `ServiceSpec` with
  nowhere to put a CIDR allow-list. A Kubernetes `Service` **has** `loadBalancerSourceRanges`, so the
  list docs/plan/12 § Cross-cutting decisions requires would render fine. What stops it is that the
  thing exposed would be an OCI registry over plain HTTP with the auth gap above — so the three rows
  reach the same absence by three different routes, and only this one could have been closed by a
  provider.
- **⚠ THE FIRST TEMPLATE DUPLICATION THAT IS NOT A SIZING TABLE.** Every family's `_helpers.tpl`
  carries a second copy of its presets, whose drift shows up as a pod of the wrong size. This one
  also carries the **pinned patch per Harbor minor**, and its drift renders an image tag Harbor does
  not publish — `goharbor/harbor-core:v2.15` resolves to nothing — as an `ImagePullBackOff` per pod
  after the caller was told `202`, with nothing naming the tag.
  `ContainerRegistryChartTests.ThePinnedPatchTableInTheChartIsTheSameTableAsTheRegistrys` diffs them.
- **The chart-annotation emitter's output is predictable by hand — a seventh sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run** — *"unchanged, 0 problem(s)"*. Only
  `values.schema.json` had to be generated. Seven charts in, the alternative — a format only the
  generator knows — is comprehensively refuted.

**What landed: `registries`.** `feeds` is docs/plan/13's **M2** sibling in the same namespace and is
**not declared**, because that document says in as many words that *"Harbor does OCI only"* and that
the three artifact protocols are a .NET service — declaring the type would publish an API nothing
serves. Vulnerability scanning, replication, retention policies and robot accounts are docs/plan/13's
four bullets under this row and **none of them ships**; each is at
`charts/managed/harbor/conformance.yaml § owed` with what blocks it, and three of the four are blocked
by the same thing: they are configured over **Harbor's own API**, after the resource exists, by a
caller that would have to authenticate to the thing it just created — which no reconciler in the tree
does and which `ReconcileContext` gives no way to do.

## Comparing an object you read back: containment, never equality

An object read out of a cluster is never byte-equal to the object you applied, and it differs in
both directions. Write every comparison — in a reconciler's `ObserveAsync`, in a contract's
`Matches`, in a conformance case's `ObjectMatchesDesired` — as *containment*: the object must carry
at least what you asked for.

**The server adds.** A CRD's `+kubebuilder:default` fills in fields nobody applied, and so do
`status`, `managedFields`, `creationTimestamp` and a defaulted `protocol` on every port. An
equality comparison fails against a real cluster and passes everywhere else, because the Docker-free
harness derives its CRD stub from `ProviderConformanceCase.Objects` and **a derived stub has no
defaults**. An OpenSearch bug of exactly this shape left that suite 27 of 27 green and was caught
only by a hand-written unit test. Nothing in the Docker-free half can catch it; `KubeJson.Contains`
is the shape that survives it, and `CyberCloud.Cluster.Conformance` is what proves it.

**The server removes — from built-in objects.** A field tagged `omitempty` on the Go type it
deserialises into is dropped when it is empty, which is *every* optional list and map on *every*
built-in Kubernetes object. `NetworkPolicySpec.Ingress` is one: the empty list that spells "deny all
ingress" comes back with **no key at all**. `CyberCloud.Terminal/consoles` converged in the fake and
hung forever against k3s on precisely that.

So on a built-in object, never write `is JsonArray { Count: 0 }`. Write
`KubeJson.IsAbsentOrEmpty(node)`, which accepts absent-or-empty and still refuses a list that grew
an entry. `FakeKubeCluster` drops empty collections from built-in objects on apply, so the strict
spelling goes red in your own suite rather than in production.

**A custom resource is the opposite case, and this was settled by measurement.** A CRD has no
`omitempty`: its stored JSON keeps what was applied, so absent and present-but-empty are genuinely
different — and three families depend on that. Strimzi's `spec.cruiseControl = {}` means "run Cruise
Control"; Cluster API's `bridge = {}` and kube-ovn's `pod = {}` are the same idiom. The first
version of the harness strip applied to every kind, on the theory that an empty collection never
carries meaning; nine tests in each of Messaging, ContainerService and Network went red for a reason
that existed only inside the fake. Do not use `IsAbsentOrEmpty` on a presence flag, and expect the
harness to echo your custom resource exactly.

**What none of this models.** `omitempty` drops an empty string, a zero and a `false` as readily as
an empty list, and which fields carry the tag lives in Go struct tags this repository does not have.
Read the built-in type's Go definition before asserting that a zero-valued scalar survives a round
trip.

## Planned namespaces

`Platform`, `Identity`, `Compute`, `ContainerInstance`, `ContainerRegistry`,
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
