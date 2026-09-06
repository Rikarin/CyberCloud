# `charts/` — Helm charts we own or have forked

```
charts/
├── platform/         # Cyber Cloud itself: silo, gateway, identity, ingest, worker, portal
├── bundle/           # what we install into a managed cluster: operators, CNI, monitoring   ← exists
├── managed/          # one chart per managed service — the catalogue
└── tenant-cluster/   # ⚠ CORRECTED — see below; these live in managed/ instead
```

> ⚠ **CORRECTED 2026-08-13 by `CyberCloud.ContainerService/managedClusters`.** The Cluster API +
> Kamaji + KubeVirt templates are `charts/managed/kubernetes` and `charts/managed/kubernetes-agentpool`,
> not a `tenant-cluster/` of their own. `Build.Charts` requires `SOURCE`, `conformance.yaml` and both
> `cybercloud.io/*` annotations **only for a chart under `charts/managed/`** — so a chart outside it is
> a managed service that quietly owes no conformance manifest, which is
> [docs/plan/12 § The pattern, once](../docs/plan/12-managed-data-services.md)'s eighth piece dropped
> by a directory name. A managed Kubernetes cluster is a catalogue row like every other. `bundle/` is
> unaffected and is still what gets installed *into* a cluster once one exists.

> ⚠ **`bundle/` landed 2026-08-19 owing the tree comment above a word, and paid it on 2026-08-20 —
> with a correction to the sentence that named the debt.** The comment reads "operators, CNI, CSI,
> monitoring". `charts/bundle/openebs-localpv` is now what stands behind the third word, and **it is
> a StorageClass and a provisioner rather than a CSI driver** — `provisioner: openebs.io/local`, no
> `CSIDriver` object, no node plugin. It provides the **default** class, which is what eleven charts
> here need: they default `storage.class` to `""`, and an empty class means *the cluster's default*.
>
> ⚠ **This paragraph used to say "two managed charts offer the tenant a storage class by name", and
> it was wrong by nine.** Counted 2026-08-20: **eleven** charts name a storage class in a template —
> clickhouse, cloud-shell, ferretdb, harbor, mariadb, nats, opensearch, postgres, rabbitmq, seaweedfs,
> valkey — and eleven carry a `@widget storageclass` row, which is a *different* eleven (kafka has
> the widget and renders no claim; cloud-shell renders a claim with no widget). Until this landed,
> **no stateful managed service could converge on a real cluster**: the claim was created, nothing
> provisioned it, and there was no error anywhere for anyone to read.
>
> What is installed is single-replica and node-local — one copy, no DRBD, no kernel module — which is
> what makes the bundle installable on a cluster we did not build. The replicated stage is declared
> and off; `charts/bundle/openebs-localpv/component.yaml` § which stage is on says what turning it on
> costs, and ADR-011's footnote 1 records the decision behind it (no LINBIT contract; the platform
> runs LINSTOR and DRBD unsupported).

## `bundle/` — the operator layer, which is not a chart family

**Every chart under `managed/` renders a custom resource and installs no controller.** Three say so in
the template itself: *"It renders a custom resource; it does not install the operator. The operator is
`charts/bundle/`'s job."* [`charts/bundle/`](bundle/README.md) is that job — **nineteen components
serving twenty-one `group/version` pairs**: the sixteen this catalogue renders, plus five nothing here
renders and something here needs. Those five are the reason a bundle cannot be derived from the charts
alone — `cluster-api-provider-kubevirt` reconciles a Machine into a `kubevirt.io/v1` VirtualMachine, and
Cluster API's and Kamaji's webhooks mount a Secret only cert-manager creates. ⚠ **Nineteen components
and twenty-one pairs is not an arithmetic slip:** the nineteenth, `openebs-localpv`, installs no
CustomResourceDefinition at all and says so in `servesNoDefinitions:` rather than claiming a built-in
group to satisfy the check.

> ⚠ **This directory's absence had been misread twelve times.** Twelve provider agents each wrote some
> form of "the k3s the cluster suite starts has no `<X>` operator" and read it as a limitation of the
> test harness. It was not. `test/CyberCloud.Cluster.Conformance` says the same thing in its own
> remarks — *"the platform cluster installs its CRDs from `charts/bundle/` long before a tenant creates
> one"* — which is why that harness derives CRD stubs rather than installing real ones.

**Nothing under `bundle/` has a `Chart.yaml`, and that is a decision rather than an omission.** The
`Build.Charts` pipeline above exists to describe *a resource type's configuration surface*; a bundle
component has no resource type and no tenant-facing surface. A component is one directory holding one
file, `component.yaml`, carrying the pin, the licence, the date it was resolved against its registry,
and the `group/version` pairs the pin's definitions serve.

**The `serves:` key is checked, not decorative.** `build/Build.Architecture.cs`'s **Bundle** gate reads
every `apiVersion:` out of every `managed/*/templates/` file, drops the Kubernetes built-in groups, and
requires each remaining pair to be served by exactly one component. It caught a live one while it was
being written: Strimzi 1.0.0 removed `kafka.strimzi.io/v1beta2`, which `managed/kafka` renders, so the
newest Strimzi would have failed every Kafka create at the API server. The pin is 0.51.0.

That check is also the enforceable half of the ordering rule
`managed/opensearch/conformance.yaml` § owed, `api-group-is-deprecated` states — *"a bundle bump …
must not be done in one commit"*. The direction is enforced on every run; the granularity is checked
over the tip commit.

## A managed-service chart

```
charts/managed/postgres/
├── Chart.yaml
├── values.yaml           # annotated (ADR-010) — the schema source
├── values.schema.json    # GENERATED — checked in, diffed in CI
├── templates/
├── SOURCE                # upstream repo + commit, if forked
└── conformance.yaml      # what the conformance suite asserts for this type
```

**The annotated `values.yaml` is the description of a managed service's configuration surface, and
most of its rows are themselves generated.** `Build.Charts` rewrites the non-`@internal` `@param`
block from the provider registry and then generates `values.schema.json` from the whole file. A chart
whose block or whose schema differs from the checked-in one fails CI. The `@internal` rows are
carried through as bytes — **at every depth**, which is a correction: see § What a chart cannot say.

Ten charts are paired today, and the split is worth reading as a ratio rather than as ten numbers:

| Chart | Rows | Generated | `@internal` |
|---|---|---|---|
| `managed/postgres` | 36 | 25 | 11 |
| `managed/valkey` | 27 | 15 | 12 |
| `managed/kafka` | 36 | 26 | 10 |
| `managed/nats` | 31 | 21 | 10 |
| `managed/seaweedfs` | 25 | 15 | 10 |
| `managed/clickhouse` | 25 | 13 | 12 |
| `managed/mariadb` | 27 | 14 | 13 |
| `managed/kubernetes` | 21 | 8 | 13 |
| `managed/kubernetes-agentpool` | 21 | 11 | 10 |
| `managed/harbor` | 22 | 11 | 11 |

⚠ **The `@internal` count barely moves and the generated count varies by a factor of two**, which is
the shape to expect for the rest: the hand-written tail is the platform's identity block (eight rows),
Helm plumbing and a credential reference, and it is the same for every managed service. What varies is
the configuration surface. So the marginal cost of a chart is its API rows, and the `@internal` rows
are a fixed cost the platform pays once per service and could stop paying if a renderer ever injected
them — see § What a chart cannot say for why they are not in any `ResourceSchema`.

> ⚠ **CORRECTED 2026-08-12.** This paragraph read "Two charts are paired today" and predicted a tail
> of ten for "the remaining eight". Both numbers were stale and the second was also the wrong shape:
> `charts/managed/seaweedfs` is **not** one of docs/plan/12's ten — it is
> [docs/plan/15 § The three kinds](../docs/plan/15-storage-blob-file.md)' object-storage row, and that
> document contributes a `fileShares` chart as well. The `@internal` prediction held on the nose at
> **ten** for the three charts that landed after it, which is the half worth keeping.

> ⚠ **The `@internal` prediction is off by two for the first time, at `managed/clickhouse`, and the
> reason is a shape rather than a slip.** Twelve, where five of the six before it are ten or eleven.
> Two of the extra rows are the second image — this is the first service that renders **two** custom
> resources, a `ClickHouseInstallation` and the `ClickHouseKeeperInstallation` it names, so there are
> two image escape hatches where every earlier chart has one. The third is `clusterName`, which is
> `@internal` because it reaches the **tenant's SQL** (`ON CLUSTER`, `Distributed()`) and so is a
> constant with consequences rather than a setting. The prediction's shape holds: the hand-written
> tail is still the identity block plus plumbing, and it grew by exactly the number of extra objects.

> ⚠ **`managed/harbor`'s `@internal` tail is eleven and its API surface is twelve, which holds the
> prediction over the widest chart in the tree.** That chart renders **fourteen objects** — there is
> no Harbor operator, so the workload is the chart — and its hand-written tail is still the identity
> block plus plumbing plus two escape hatches: the image registry, for an air-gapped mirror, and the
> credentials-Secret **name**. ⚠ That second one is worth reading: it is a name and never a value,
> because `goharbor/harbor-helm` ships `harborAdminPassword: "Harbor12345"` as a live default while
> randomising every other credential in the same template, and a values key here would be one edit
> away from reproducing it. So object count moves the tail by nothing at all — what moves it is how
> many *escape hatches* a service needs, which is the sharper form of the prediction.

> ⚠ **`managed/kubernetes` is the first chart whose API surface is SMALLER than its `@internal`
> tail, and the reason is the row rather than the shape.** Eight API rows against thirteen
> hand-written ones. Three of the eight rows a reader would expect are deliberately absent —
> there is no exposure setting (nowhere upstream to attach a CIDR allow-list), no `addons` (the
> platform cannot reach the cluster it creates) and no `subnetId` (another provider's resource) —
> and three of the thirteen are constants with consequences rather than plumbing: the shared
> `dataStoreName`, the frozen `serviceDomain`, and the control-plane sizing the quota meters reserve
> against. The prediction's shape still holds at ten for its child chart, which has a real
> configuration surface and nothing unusual in its tail.

> ⚠ **CORRECTED 2026-08-12.** This paragraph read: "`Build.Generate` turns that into the resource
> type's OpenAPI body, the CLI flags, the SDK model and the portal form" — the chart authoring the
> API. [ADR-010 § Which end authors the schema](../docs/plan/02-technology-decisions.md) decided the
> opposite on 2026-08-11 and ADR-012 always said the registry was the one source. Nothing had caught
> it because no resource type has both a chart and a registry declaration, so the two files had never
> been compared by anything. **The direction is registry → chart.**

`charts/managed/postgres/` is the worked example. Everything below is implemented in
`build/Build.Charts.cs` and exercised by that chart.

## The annotation format

Cozystack's is `## @param {type} name - description` ([ADR-010](../docs/plan/02-technology-decisions.md)
takes the *pipeline*, not the code). Ours is:

```yaml
## @param <name> {<type>} <description>
## @required
## @secret
## @immutable
## @internal <reason>
## @enum <a> | <b> | <c>
## @range <min>..<max>
## @length <min>..<max>
## @pattern <regex>
## @format <name>
## @example <json>
## @widget <name>
<name>: <default>
```

The `@param` line comes first; the modifiers follow it; the key is the very next line. The block is
the run of `## @` lines **directly above** the key — a blank line or an ordinary `#` comment between
them fails the build, so that moving a key moves its annotation with it.

| Directive | Applies to | Becomes |
|---|---|---|
| `@param <name> {<type>} <description>` | every key, no exceptions | `type`, `description` |
| `@required` | any | the parent's `required` array |
| `@secret` | `string` | `writeOnly`, `format: password`, `x-cybercloud-secret` |
| `@immutable` | any | `x-cybercloud-immutable` |
| `@internal <reason>` | any; inherited by members | `x-cybercloud-api: false`, `x-cybercloud-internal-reason` |
| `@enum a \| b \| c` | `string`, `integer`, `number`, `array` | `enum`, or `items.enum` on an array |
| `@range <min>..<max>` | `integer`, `number` | `minimum`, `maximum` |
| `@length <min>..<max>` | `string` | `minLength`, `maxLength` |
| `@pattern <regex>` | `string` | `pattern`, **anchored** — see below |
| `@format <name>` | `string` | `format` |
| `@example <json>` | any | `examples` |
| `@widget <name>` | scalars | `x-cybercloud-widget` — ADR-012's picker hint |

The last four landed 2026-08-12, when `CyberCloud.DBforPostgreSQL/servers` became the vocabulary's
first real user and `./build.sh Charts` went red with thirteen refusals. Four things about them:

**`@pattern` is written bare and emitted anchored.** `SchemaProperty.Pattern` is a whole-value match —
`ResourceSchema` tests it as `^(?:…)$` — and JSON Schema's `pattern` keyword is a *search*. A bare
`\d+Gi` in a `values.schema.json` accepts `xxx20Gixxx`, which the API refuses, so the chart would be
strictly more permissive than the surface it was generated from. `PropertyNode` wraps it, in the same
three characters and for the same reason as `OpenApiEmitter`. The annotation stays bare so the line in
`values.yaml` and the `Pattern` in C# are the same string.

**A `@pattern` may contain anything printable, and may not contain edge whitespace.** `|`, `#`, `:`,
quotes and braces are all legal — the line is a comment, and `@pattern` is the one directive that takes
the rest of the line verbatim, so nothing splits on them. But the reader trims the line, the directive
body and the argument, so a pattern with a leading or trailing space would come back as a *different*
pattern; the emitter refuses to write one, and refuses control characters, rather than let it mangle.

**`@length` takes an open end and `@range` does not.** `1..`, `..63` and `1..63` are all legal
`@length` arguments, because "at least one character" is the ordinary shape of a string constraint.
`@range` still requires both ends — that is the older published pattern, and widening it is a change
to a surface that already has authors. Both are inclusive; both refuse two open ends.

**`@example` is JSON, on one line.** The emitter re-serialises `ExampleJson` compactly, so the value
rather than its spelling determines the bytes, and every control character is escaped by construction.
It becomes `examples` (plural, an array) because this document is JSON Schema 2020-12;
`OpenApiEmitter` writes `example` because OpenAPI 3.0 is a different specification.

The six `@format` names are `uuid`, `date-time`, `uri`, `email`, `cybercloud-region` and
`cybercloud-resource-id` — one per `SchemaFormat` member that is not `None`. The list is closed, like
`{type}`. `@format` and `@secret` on one key is a build failure: `@secret` already means
`format: password`, so the two would write the same keyword and one would be lost.

The six types are `string`, `integer`, `number`, `boolean`, `object`, `array` — one per `SchemaKind`
member in `src/CyberCloud.ResourceManager.Contracts`. There is no seventh, and a seventh word fails
the build.

There is no `@default` directive: the default **is** the YAML value on the annotated line. There is
no `@readonly` either, and that is a gap rather than an omission — see § What a chart cannot say.

### Where it differs from Cozystack's, and why

**1. The name is checked, not used.** Cozystack repeats the dotted path in the annotation
(`## @param resources.limits.cpu`). We write the leaf name only, take the path from the file's
indentation, and *compare* the annotation's name against the key on the next line. An annotation
cannot name a key that does not exist, and a key cannot quietly acquire somebody else's description
during a merge.

**2. `{type}` is mandatory, closed, and cross-checked against the literal.** Cozystack's type is
prose its generator largely ignores; here it is what the API validates on. Every declared type is
checked against the value written on the same line, so `## @param version {string}` over a bare `17`
is a build failure with a line number rather than an API that takes a number where the SDK sends a
string. Inference cannot do this job — it cannot tell `integer` from `number` for `5`, and it has no
opinion to disagree with.

**3. No ` - ` separator.** The description is everything after `{type}`. Cozystack's separator makes
a description containing " - " ambiguous, and descriptions become CLI help, so they contain dashes.

**4. Every key is annotated, and `@internal` is how a key is excluded.** Cozystack has `@skip`, which
takes no reason. Ours takes a written one and fails without it — the same discipline as
`[DurableStateRationale]` in [docs/plan/05 § The two tiers](../docs/plan/05-state-and-storage.md).
A key that is neither annotated nor `@internal` fails the build with its line number. **It does not
appear untyped, and it does not silently vanish**: a schema that omits half the configuration surface
is worse than no schema, because the omission is invisible in the file that claims to be complete.

**5. Four directives Cozystack has no equivalent for**, each because a surface downstream needs it:
`@secret` (a generated form must mask the value and a read must drop it — ⚠ *must*, not *does*: the
write path does not yet substitute a `SecretRef`, see `SchemaProperty`'s remarks and
[docs/plan/02 § ADR-010](../docs/plan/02-technology-decisions.md)), `@immutable`, `@range`, and
`@widget` — which ADR-012 names directly, "with `x-cybercloud-*` hints for widgets (a `storageclass`
picker, a region picker)".

**6. `@internal` is inherited, may be written once, and is emitted on every descendant.** The two
rules point opposite ways on purpose. In `values.yaml` a repeated `@internal` is a second place to
change, so it is a build failure. In the generated file, a consumer projecting the API surface reads
one property at a time, and a filter that has to walk back up the pointer to discover its parent was
excluded is a filter that will eventually leak platform plumbing into a public API.

### The generated file covers *everything*, including the excluded keys

`values.schema.json` is Helm's contract as well as ours. Helm validates the whole of `values.yaml`
against it, and with `additionalProperties: false` a key the schema dropped would fail `helm lint`.
So the resource body is a **projection** of the document — the members without
`x-cybercloud-api: false` — rather than the whole of it. A chart's `values.yaml` legitimately holds
two disjoint things: the tenant's configuration surface, and identity the reconciler injects
(ADR-013's seven `cybercloud.io/*` labels). Only the first is an API.

## The values subset

`values.yaml` is read by a small reader in `build/Build.Charts.cs` rather than by a YAML library,
because a general parser hands back a document with no line numbers attached to the values and
"a malformed annotation fails with the line number" is the requirement. The subset is:

* block mappings, **two spaces** per level, no tabs;
* one-line scalars — `true`/`false`, integers, numbers, bare strings, `"double"` or `'single'` quoted;
* flow sequences on the key's line: `[]`, `[pgvector, postgis]`. **Block sequences fail**, so that
  every default sits on the line its annotation is attached to;
* `{}` for a free-form map — an `{object}` with no members is a free-form map and anything else is a
  key whose members were forgotten;
* **no null values.** A key with no value has no default, and `helm lint` would reject `null` against
  the type its own generated schema declares. Write `""`, `[]` or `{}`;
* **no inline comments** after a value. Put the prose in the `@param` description, where it reaches
  the CLI help and the portal instead of only the file.

Everything outside the subset is a build failure naming its line. Because the subset is a subset of
YAML, `helm` reads the same file with a real parser and would disagree loudly if it were not.

## What `Build.Charts` does

`./build.sh Charts`, in this order:

1. build the provider registry and, for every type that names a chart, **rewrite that chart's
   non-`@internal` `@param` block in place and fail** if it differs — ADR-012's fifth surface. The
   `@internal` rows are copied through as bytes and never regenerated;
2. then, per chart: read `Chart.yaml` — the name must match the directory;
3. parse `values.yaml` and emit `values.schema.json`; **rewrite it in place and fail** if it differs
   from the checked-in file, the same way `Build.Generate` treats a drifted OpenAPI document;
4. for a chart under `charts/managed/`, require `SOURCE`, `conformance.yaml`, and the
   `cybercloud.io/resource-type` and `cybercloud.io/api-version` annotations;
5. `helm lint --strict` — which also validates `values.yaml` against the schema just generated, using
   Helm's JSON Schema implementation rather than ours;
6. `helm package` into `artifacts/charts/`, only once every chart has passed.

**Step 1 before step 3 is the round trip and is not an accident.** The block is written and then
parsed by the reader below, with line numbers, on the same run — so an emitter that produced anything
outside the values subset fails the build on its own output rather than a fortnight later.

⚠ **Step 1 is why this target now depends on `Compile`.** Building the registry means running each
provider's `Describe`. Before ADR-012's fifth surface, `Charts` needed only `helm`.

Two vacuous states, each a warning rather than a silent pass — the `Vacuous` convention from
`Build.Architecture`, ○ rather than ✔:

* **no chart at all** — "inspected 0 chart(s)";
* **no registry-to-chart pair** — "N managed chart(s), M registry type(s) naming a chart, 0 pair(s)
  compared", with every unclaimed chart and every type naming a missing chart listed by name. A target
  that inspected nothing is green because it found nothing, not because the tree is clean.

  > ⚠ **CORRECTED 2026-08-12.** This bullet read "This is the state the tree is in today:
  > `charts/managed/postgres` declares `CyberCloud.DBforPostgreSQL/servers`, no C# provider declares
  > that type". The provider landed, so the tree reports **one pair compared** and the vacuous branch
  > is now the warning it was written to be rather than a description of the present.
  >
  > ⚠ **Two pairs the same day**, once `CyberCloud.Cache/redis` landed with `charts/managed/valkey`.
  > The number is worth watching rather than updating: the vacuous branch fires on *zero* pairs, so it
  > has stopped being reachable by accident, and what would reach it now is somebody adding a chart
  > with no provider or a provider naming a chart that is not in the tree. Both of those are listed by
  > name in `Unpaired` — which is the half of this warning that keeps working after the count is
  > non-zero.

The generated file is deterministic: keys sorted ordinally, `InvariantCulture` throughout, LF
newlines, no timestamps, no paths, no machine names. Declaration order is not lost to the sort — it
is written down as `x-cybercloud-order`, because a generated form needs its fields in the order the
author wrote them.

## `SOURCE` is required, including when nothing was forked

[docs/plan/03 § charts/](../docs/plan/03-repository-layout.md) says "if forked". `Build.Charts`
requires the file for **every** chart under `charts/managed/`, with `vendored:` and `upstream:` keys.
ADR-010's rule is unenforceable otherwise: "there is no SOURCE file" would be a legal state, and it
is indistinguishable from "somebody forked a chart and forgot". `vendored: none` is an answer to
"where did this come from"; a missing file is not.

## What a chart cannot say

The generated schema is shaped like a `ResourceSchema` — every property carries an RFC 6901
`x-cybercloud-pointer`, and `type`/`description`/`required`/secret line up one-to-one with
`SchemaProperty`. Since 2026-08-12 the two are wired together: a chart's root key `version` is the
registry's `/properties/version`, and `ChartAnnotationEmitter` writes the block from the schema.

> ⚠ **CORRECTED 2026-08-12.** This section claimed "`ResourceSchema` cannot carry `enum`,
> `minimum`/`maximum`, `default`, `items`, or any `x-cybercloud-*` hint" and that `SchemaProperty` is
> "`(pointer, kind, required, readOnly, secret, description)`". **All of that is refuted by the
> type.** `SchemaProperty` carries `AllowedValues`, `Minimum`/`Maximum`, `DefaultJson`, `ElementKind`
> and `Widget`, alongside `Format`, `Pattern`, `MinLength`/`MaxLength`, `ExampleJson`, `Nullable` and
> `Immutable`. The six positional parameters are the six every property has an opinion about; the
> rest are init-only and were added later. Read the type before repeating a list of its members.

The gap runs the other way, and it is where a fact can be lost:

* **`ReadOnly` is excluded rather than lost.** A values key is by construction something the chart's
  caller sets, whereas `SchemaProperty.ReadOnly` describes server-owned state that never appears in a
  values file at all. The emitter drops those properties, exactly as the generated CLI drops
  `--provisioning-state`.
* **Placement is excluded too, and it is the *second* exclusion rather than a quirk of one provider.**
  The property `RequiresCluster` names — `/properties/clusterId` by default — says which cluster the
  resource is placed **into**. A chart is rendered into a cluster and has no opinion about which one:
  the id picks the API server the apply runs against, which is settled before Helm is handed anything,
  and `templates/` never reads `.Values.clusterId`. `ChartAnnotationEmitter.Emit` is **told** the
  pointer (`ResourceTypeRegistration.ClusterIdPointer`, from the registration) rather than guessing
  from a member name or a `format`, because a schema on its own cannot tell a placement uuid from a
  tenant-chosen one.

  > ⚠ **DECIDED 2026-08-12, and the alternative was a chart whose default fails its own schema.**
  > While it was generated, the row was `clusterId: ""` under `## @required` and `## @format uuid`,
  > and `""` is not a uuid. `helm lint --strict` passed it only because JSON Schema 2020-12 treats
  > `format` as an *annotation* rather than an assertion — under any validator with an assertive
  > format vocabulary the chart's own default fails the chart's own `values.schema.json`. The emitter
  > was not at fault and was **not** weakened: every values key must carry a literal (the reader
  > refuses `null` and so does `helm`), `""` is the only "unset" a string has, and the property
  > declares no `DefaultJson` because there is no cluster a tenant gets without choosing one. Giving
  > it the nil uuid would have been worse — a real-looking id nobody chose, which is exactly what
  > this list's *"a number with no `DefaultJson` is refused"* rule exists to prevent. The row was
  > never configuration, so it left the chart instead.
* **Two members have no annotation syntax, and generation *fails* rather than dropping them.**
  `Nullable` and a non-text `ElementKind`. A schema declaring one produces a build failure naming the
  property and the directive that would close it — `@nullable`, `@element`. Neither has a user, and
  both are harder than the five that closed: a nullable values key collides head-on with § The values
  subset's "no null values" rule, so `@nullable` is a question about the subset before it is a
  directive; and `@element` has to decide what a non-text element means for the hard-coded
  `items: {type: string}` that `@enum` already emits. **Leaving them open is a decision, not an
  oversight** — the refusal names the fact and the build is red, so nothing is lost silently.

  > ⚠ **CORRECTED 2026-08-12. This list said seven, and said closing one takes "four edits: the
  > `Directives` table in `build/Build.Charts.cs`, its emission in `PropertyNode`, a row in the table
  > above, and a case in `ChartAnnotationEmitter`". Both numbers were wrong.** Five closed at once
  > when the Postgres provider made them a red gate, and closing them took **nine sites in four
  > files**. The prediction missed the three that carry the risk: the **parse case in
  > `TakeAnnotation`** and its field on `ValueAnnotation` — the `Directives` table only decides that a
  > verb is *spelled* right, so a directive listed there with no case is read and thrown away, and the
  > build stays green while the constraint never reaches `values.schema.json`; the **cross-check in
  > `Validate`** — a `@pattern` on a `{boolean}` validates nothing, and a default outside its own
  > constraint is something `helm lint` rejects two steps later; and a case in **`CheckUnspellable`**
  > for arguments the directive's transport cannot carry. The `Subset` checker in
  > `ChartAnnotationTests` is a fourth copy of the table. It was a guess written before any directive
  > had been added, which is what made it worth writing down and worth checking.
* **A text element kind is the one array shape the vocabulary reaches**, because `@enum` on an array
  becomes `items: {type: string, enum: […]}` and that `string` is hard-coded.
* **A number or a boolean with no `DefaultJson` is refused.** Every values key carries a value — a
  `null` is refused by the reader and by `helm` — and there is no empty spelling of a number, so the
  emitter will not invent a `0` that may also sit outside the property's own `@range`.
* **`@range` takes both ends.** `1..` and `..5` are *malformed directives*, not open ranges — the
  pattern is `<min>..<max>` and the reader refuses anything else. A `SchemaProperty` with a `Minimum`
  and no `Maximum` is therefore refused, naming the regex that would have to grow. Both bounds are
  **inclusive**.
* **`@enum` members are split on `|` and trimmed**, so an allowed value carrying a pipe or leading
  space is refused rather than emitted as two members or as a different string.
* **`@secret` is a string's directive and `@widget` renders one scalar field.** `SchemaProperty` does
  not enforce either — `Incoherences` permits a `WidgetHint` on an array — so the emitter refuses
  what the chart reader would.
* **`@pattern`, `@length` and `@format` refine a *string*, and an ARRAY OF TEXT is the one place the
  registry disagrees.** `Build.Charts` refuses all three on any `@param` whose type is not
  `{string}`, for the reason its own comment gives: they are JSON Schema keywords that are *silently
  ignored* on every other type, so the directive reads as a constraint and validates nothing.
  `SchemaProperty.Incoherences` refuses them too — but only after collapsing an array to its
  `ElementKind` (`var value = Kind is SchemaKind.Array ? ElementKind : Kind`), so **an array of text
  carrying a `Pattern`, a length bound or a `SchemaFormat` is a perfectly coherent registration**
  which `ResourceSchema.Validate` enforces per element. Until **2026-09-06 (#84)** the emitter wrote
  it anyway — `## @param x {array}` followed by `## @pattern …` — and `./build.sh Charts` then failed
  on the file the emitter had just written, pointing at a generated `values.yaml` line rather than at
  the registration. `CheckUnspellable` now refuses it, naming the property, the `SchemaProperty`
  member and the directive.

  > ⚠ **That is the refusal, not the closing of the gap, and the gap has two real users waiting.**
  > `KafkaClusters` and `NatsClusters` both want `Pattern = CidrPattern` on
  > `/properties/external/allowedCidrs` and withhold it *only* because this surface refuses it —
  > `charts/managed/kafka/conformance.yaml` and `charts/managed/nats/conformance.yaml` carry the cost
  > as `cidr-shape-is-unenforced`: a body may send `999.0.0.1/99`, be accepted, reach
  > `loadBalancerSourceRanges` and fail at the API server **after** the caller was told `202`. Closing
  > it for real means emitting `items.pattern` / `items.minLength` / `items.maxLength` /
  > `items.format` for a text element kind — the same per-element shape the `@enum`-on-an-array bullet
  > above already has — and it is the *same* nine-sites-in-four-files shape as the five that closed on
  > 2026-08-12. #84 deliberately did not attempt it; it made the two ends agree about what is refused
  > so that the complaint lands where the mistake is.
  >
  > ⚠ **And #84's own claim that "no registered array-of-text property declares one today" is wrong,
  > which is worth knowing before the next reader trusts it.**
  > `CyberCloud.Sample/widgets`' `/properties/allowedCidrs` declares
  > `Pattern = @"\d{1,3}(\.\d{1,3}){3}/\d{1,2}"` on an `ElementKind = SchemaKind.Text`. Counted
  > 2026-09-06: `src/` outside `*.Tests/` holds **eight** `SchemaKind.Array` property declarations and
  > `SampleWidgets.cs` is the **only** one carrying a string refinement — the two at
  > `/properties/external/allowedCidrs` are *comments* saying the `Pattern` was withheld. (Stale the
  > moment a ninth array property is added, or one is built through a helper rather than a literal
  > `SchemaKind.Array,` argument.) Nothing is red because the Sample provider names **no** chart —
  > 21 of the registry's 22 resource types name one, and its type is the 22nd — so the emitter never
  > sees the property; and if it ever did, that same property's `Widget = WidgetHint.Cidr` on an array
  > was already refused by the bullet above. The conclusion held; the premise did not.

* **A nested `@internal` row is preserved too, and until 2026-08-12 it was not.** `Rewrite` walked
  root keys only, so `bootstrap.password` — `@internal`, inside the *generated* `bootstrap:` object —
  was deleted on every run while the build printed "The `@internal` rows were not touched". The merge
  is recursive now, and the drift message reports the line count it actually carried rather than
  asserting one. Every `@internal` key must still sit after every generated one **at its own level**,
  so that each level's generated keys are one contiguous run.

**What the first managed service needed.** `CyberCloud.DBforPostgreSQL/servers` declares `Pattern` on
seven rows (three Kubernetes quantities, a Postgres identifier pair, a WAL volume and an `s3://`
destination), `MinLength`/`MaxLength` on two, `ExampleJson` on three and `SchemaFormat.Uuid` on one —
thirteen refusals, and the only red gate in the tree. They are declared in C#, where they are
enforced, and since 2026-08-12 the chart carries them as well. The two that remain open have no user
at all.

> ⚠ **`@format` has no user in any chart, and that changed on the same day.** The one
> `SchemaFormat.Uuid` in that count is `/properties/clusterId`, which is placement and is now excluded
> from the chart — see the bullet above. So the directive is written, read, cross-checked and
> exercised by `ChartAnnotationTests`, and no checked-in `values.yaml` contains one. (The one
> `"format"` left in `charts/managed/postgres/values.schema.json` is `password`, which `@secret`
> writes — the two directives may not share a key, which is why they are refused together.) That is worth
> knowing before reading the count as "thirteen facts the chart carries": it carries twelve, and the
> thirteenth was never a chart row. The directive is not dead — the next `SchemaFormat` a provider
> declares on a tenant-facing property emits it — but nothing in the tree proves the round trip
> through `build/Build.Charts.cs` end to end today.
>
> ⚠ **The second managed service did not change that, and it is the stronger version of the same
> report.** `CyberCloud.Cache/redis` declares two `SchemaFormat` values — `Uuid` on its cluster id and
> `Region` on `/location` — and neither reaches a chart: the first is placement and is excluded, the
> second is root-level and was never a values key. So two providers have now declared a format apiece
> and `@format` still has no user in any checked-in `values.yaml`. That is evidence about *where*
> formats live rather than about the directive: a format refines an **envelope** property — a region,
> a resource id, a cluster id — and an envelope property is by construction not something a chart
> renders. The directive stays; the honest expectation is that it waits for a service with a
> tenant-set `uri` or `email` in its configuration surface rather than for the next provider.

## Forking discipline

Upstream charts are a starting point, not a dependency. Where a chart is close, fork it here with
the upstream repo and commit recorded in a `SOURCE` file. A drifting vendored chart with no
provenance is how a platform ends up unable to upgrade Postgres.

## Licences are a build gate, not a footnote

`Build.Licence` scans the chart set and the container images in the platform bundle and fails on any
SSPL/BUSL/AGPL image outside an allow-list with a written reason — ADR-011. Valkey not Redis,
OpenBao not Vault, FerretDB not MongoDB, OpenSearch not Elasticsearch.

> ⚠ **CORRECTED 2026-08-19. `Build.Licence` is `NotImplementedYet` and the paragraph above describes
> what it will do.** What exists is narrower and is labelled as such: the **Bundle** gate checks each
> `charts/bundle/*/component.yaml`'s **declared** SPDX identifier against an allow-list of four —
> Apache-2.0, BSD-3-Clause, MIT, MPL-2.0. That catches a component added under SSPL or BUSL by an
> author who wrote its licence down honestly. It reads no `LICENSE` file and opens no image, so the
> distance between it and the sentence above is the distance between an attestation and a scan.
> AGPL-3.0 is deliberately off the list even though ADR-011 marks Grafana offerable, because that row
> carries a condition — *we distribute, we do not modify* — and a gate that turns a conditional into an
> unconditional yes is a gate that retires the condition.

See [docs/plan/03 § charts](../docs/plan/03-repository-layout.md) and
[docs/plan/12](../docs/plan/12-managed-data-services.md).
