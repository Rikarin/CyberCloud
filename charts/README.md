# `charts/` — Helm charts we own or have forked

```
charts/
├── platform/         # Cyber Cloud itself: silo, gateway, identity, ingest, worker, portal
├── bundle/           # what we install into a managed cluster: operators, CNI, CSI, monitoring
├── managed/          # one chart per managed service — the catalogue
└── tenant-cluster/   # Cluster API + Kamaji + KubeVirt templates for an in-house cluster
```

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
26 of its 36 rows are themselves generated.** `Build.Charts` rewrites the non-`@internal` `@param`
block from the provider registry and then generates `values.schema.json` from the whole file. A chart
whose block or whose schema differs from the checked-in one fails CI.

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
| `@widget <name>` | scalars | `x-cybercloud-widget` — ADR-012's picker hint |

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
`@secret` (the write path replaces the value with a `SecretRef`, so a generated form must mask it and
a read must drop it), `@immutable`, `@range`, and `@widget` — which ADR-012 names directly, "with
`x-cybercloud-*` hints for widgets (a `storageclass` picker, a region picker)".

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
  compared", with every unclaimed chart and every type naming a missing chart listed by name. This is
  the state the tree is in today: `charts/managed/postgres` declares
  `CyberCloud.DBforPostgreSQL/servers`, no C# provider declares that type, and the one provider in
  the tree renders no chart. A target that inspected nothing is green because it found nothing, not
  because the tree is clean.

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

<<<<<<< HEAD
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
* **Seven members have no annotation syntax, and generation *fails* rather than dropping them.**
  `Format`, `Pattern`, `MinLength`, `MaxLength`, `ExampleJson`, `Nullable` and a non-text
  `ElementKind`. A schema declaring one produces a build failure naming the property and the
  directive that would close it — `@format`, `@pattern`, `@length`, `@example`, `@nullable`,
  `@element`. Closing one means four edits: the `Directives` table in `build/Build.Charts.cs`, its
  emission in `PropertyNode`, a row in the table above, and a case in `ChartAnnotationEmitter`.
  They were deliberately not added ahead of a chart that uses them.
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

**The first managed service already needs three of those seven.** `CyberCloud.DBforPostgreSQL/servers`
declares `Pattern` on five rows (three Kubernetes quantities, a Postgres identifier pair and an
`s3://` destination) and `MinLength`/`MaxLength` on two. They are declared in C#, where they are
enforced; the chart does not carry them. So the loss is real, it is on exactly one surface, and
`@pattern` and `@length` are no longer hypothetical directives waiting for a first user — they have
one.

## Forking discipline

Upstream charts are a starting point, not a dependency. Where a chart is close, fork it here with
the upstream repo and commit recorded in a `SOURCE` file. A drifting vendored chart with no
provenance is how a platform ends up unable to upgrade Postgres.

## Licences are a build gate, not a footnote

`Build.Licence` scans the chart set and the container images in the platform bundle and fails on any
SSPL/BUSL/AGPL image outside an allow-list with a written reason — ADR-011. Valkey not Redis,
OpenBao not Vault, FerretDB not MongoDB, OpenSearch not Elasticsearch.

See [docs/plan/03 § charts](../docs/plan/03-repository-layout.md) and
[docs/plan/12](../docs/plan/12-managed-data-services.md).
