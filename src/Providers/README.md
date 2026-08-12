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

- **The chart-annotation emitter's output is predictable by hand — a fourth sighting.** This chart's
  `@param` block was written to match what `ChartAnnotationEmitter` would produce and came back
  **unchanged on the first `./build.sh Charts` run**, exactly as `charts/managed/valkey`'s,
  `charts/managed/nats`' and `charts/managed/seaweedfs`' did. Only `values.schema.json` had to be
  generated.

- **⚠ The structural statelessness check's blind spot, confirmed a fourth time.** A `readonly`
  `Dictionary` cache added to `OpenSearchServiceReconciler` left
  `ReconcilerConformance.CheckNoHiddenState` **green** and failed only
  `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem`. Both halves are in
  `OpenSearchReconcilerTests` and both were run red against the counter-example.

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

`CyberCloud.Providers.Analytics` — `CyberCloud.Analytics/clickhouseClusters` on the Altinity operator,
[12 § The catalogue](../../docs/plan/12-managed-data-services.md). **The seventh family, and the first that renders
two custom resources in two API groups**, and the first whose row the platform is already a customer
of.

### What the seventh provider measured

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
- **Four module edges, six projects, one `ProviderConformanceCase` — a seventh family with identical
  columns.** A type rendering two custom resources across two API groups, wiring one to the other,
  needed no fifth edge and no seventh project.
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
