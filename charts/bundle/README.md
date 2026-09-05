# `charts/bundle/` — the operator layer every managed chart renders against

Every chart in `charts/managed/` renders a **custom resource** and installs no controller. Three of
them say so in the template itself:

> It renders a custom resource; it does not install the operator. The operator is `charts/bundle/`'s
> job, which is why this chart has no CRD directory and no operator Deployment.

This directory is that job. It is what turns a bare Kubernetes cluster into one where
`CyberCloud.DBforPostgreSQL/servers` converges instead of returning 202 and waiting forever.

> ⚠ **This absence had already been misread twelve times.** Twelve provider agents each wrote some
> form of "the k3s the cluster suite starts has no `<X>` operator", and each read it as a limitation
> of the test harness. It was not a harness limitation. It was this directory not existing. The same
> sentence appears in `test/CyberCloud.Cluster.Conformance`'s own remarks — *"the platform cluster
> installs its CRDs from `charts/bundle/` long before a tenant creates one"* — which is why the
> harness derives CRD stubs rather than installing real ones.

## This directory holds no Helm charts

Nothing here has a `Chart.yaml`, and that is deliberate rather than unfinished.

`Build.Charts` globs `charts/**/Chart.yaml`, and for every chart it finds it regenerates a
`values.schema.json` from an annotated `values.yaml`, lints it and packages it. That pipeline exists
to describe **a resource type's configuration surface** — the annotated values file is, in
`charts/README.md`'s words, "the single description of a managed service's configuration surface". A
bundle component has no resource type and no tenant-facing configuration surface. Giving it a
`Chart.yaml` would put nineteen charts through a generator whose output nobody reads and whose
failure mode is a schema drift no API depends on.

So a component is a directory holding **one file**, `component.yaml`, describing an install that
somebody else's chart or manifest performs.

> ⚠ **`SOURCE` is one file in `charts/managed/` and zero files here, and the difference is a real
> one rather than a shortcut.** A managed chart has two separable questions — *where did these
> templates come from* (`SOURCE`) and *what must be true once the reconciler ran*
> (`conformance.yaml`) — because the templates are ours. A bundle component vendors nothing: the
> provenance question and the pin question have the same answer, and splitting one answer across two
> files is how the two come to disagree.

## What a component owes

`build/Build.Architecture.cs`'s **Bundle** gate fails the build when any of this is missing. It is
listed in `ArchitectureGates` beside the other sixteen rows, and it reports `○` rather than `✔`
if it ever inspects nothing.

| Key | Required | What it is |
|---|---|---|
| `component` | always | The component name. Must equal the directory name |
| `phase` | always | Install order. Must equal the phase `bundle.yaml` gives it |
| `licence` | always | The upstream's SPDX identifier — ADR-011 |
| `install` | always | `helm`, `helm-archive` or `manifest` |
| `source` | always | The URL that was read to resolve the pin |
| `checked` | always | The ISO date it was read. Not in the future |
| `serves` | unless `servesNoDefinitions` | The `group/version` pairs the component's definitions serve |
| `servesNoDefinitions` | when `serves` is absent | Why this component installs no CustomResourceDefinition. At least 60 characters of prose |
| `images` | unless `rendersNoWorkloadImages` | Every image the pinned artefact renders, as `repository:tag@sha256:…` |
| `rendersNoWorkloadImages` | when `images` is absent | Why this component renders no container. At least 60 characters of prose |
| `requiredBy` | always | The charts and components that need it |
| `repo`, `chart`, `version` | `install: helm` | Chart repository, chart name, chart version |
| `archive`, `chart`, `version` | `install: helm-archive` | Packaged-chart URL, chart name, chart version |
| `manifest`, `release` | `install: manifest` | Manifest URL and the release tag it belongs to |

### `serves:` is the load-bearing key

The gate's coverage check reads every `apiVersion:` out of every `charts/managed/*/templates/` file,
drops the Kubernetes built-in groups, and requires that **every remaining group/version is served by
exactly one component**. A group nothing serves fails the build, naming the chart and the template.
Two components serving the same group/version fails as well, because that is two operators owning
one definition.

> ⚠ **One component installs no definitions at all, and the escape is written down rather than
> faked.** `charts/bundle/openebs-localpv` installs a ServiceAccount, a ClusterRole, a
> ClusterRoleBinding, a Deployment and a `StorageClass` — five Kubernetes built-ins and no custom
> resource. It declares `servesNoDefinitions:` with the reason, which the gate requires to be at least
> sixty characters of prose because a boolean is a checkbox and a checkbox is how an exception becomes
> the default. The trap it exists to close was available and cheap: `storage.k8s.io/v1` matches the
> gate's `group/version` pattern, satisfies the old check, and asserts that a component *serves* a
> group the API server has served since 1.6. A component may declare `serves:` or
> `servesNoDefinitions:`, never both.

Today that is **nineteen components serving twenty-one `group/version` pairs against twenty-one charts
rendering sixteen**, one of the nineteen serving none. The five that no chart renders are the reason
a bundle cannot be derived from `charts/managed/` alone: `cluster-api-provider-kubevirt` reconciles a Machine into a `kubevirt.io/v1`
VirtualMachine and imports its disk through `cdi.kubevirt.io/v1beta1`; Cluster API's and Kamaji's
webhooks mount a Secret only `cert-manager.io/v1` creates; and Kamaji's own `kamaji.clastix.io/v1alpha1`
`DataStore` is what `charts/managed/kubernetes`'s `dataStoreName: default` resolves against.

That check is not decoration. It is what the ordering rule reduces to:

> ⚠ **It already caught a live one.** Strimzi 1.0.0 removed `kafka.strimzi.io/v1beta2`, and 1.1.0 —
> the newest release — serves `v1` alone with `conversion: strategy: None`. `charts/managed/kafka`
> renders `v1beta2`. A bundle pinned at the newest Strimzi would have made every Kafka create fail
> at the API server, for every tenant, with an error naming an api-version rather than a bundle. The
> pin here is 0.51.0, the last release serving both. See
> `charts/bundle/strimzi-kafka-operator/component.yaml`.

Write a `serves:` line only for a group/version you read off the definitions the pin installs. It is
a claim the build tests, not an inventory of what a chart contains — `prometheus-operator-crds`
installs `monitoring.coreos.com/v1alpha1` and does not claim it, because nothing here renders one.

## The ordering rule, and which half a machine enforces

`charts/managed/opensearch/conformance.yaml` § owed, `api-group-is-deprecated`:

> Closing it is a new api-version on the resource type plus a `charts/bundle/` bump, in that order,
> and the two must not be done in one commit: a bundle that moved first would strand every existing
> service.

Two halves, enforced by two different things because only one of them is a property of files on disk.

* **The direction** is the coverage check above, on every run, over every chart. A bundle that moved
  first is a bundle whose `serves:` no longer covers what a chart renders, and that is red.
* **The granularity** — *not in one commit* — is checked over the tip commit: a commit that changes a
  component's `version:` or `release:` line **and** touches a `charts/managed/*/templates/` file
  fails. A repository with no parent commit reachable, such as a shallow CI clone, is reported as
  not inspected rather than passed.

## Installing

```bash
./charts/bundle/install.sh --dry-run              # print every command, run none
./charts/bundle/install.sh                        # apply, phase by phase
./charts/bundle/install.sh --phase 50             # one phase — which here is EIGHT components
./charts/bundle/install.sh --component kube-ovn   # one row. Repeatable
./charts/bundle/install.sh --verify               # resolve every pin against its registry, apply nothing
```

The script reads `bundle.yaml` and each `component.yaml`; it hard-codes no version. `--verify`
answers the question this directory rots on — *does every pin still resolve* — without needing a
cluster.

> ⚠ **`--phase` is not "one row", whatever the usage text used to say.** Phase 30 is two components,
> phase 40 is four and phase 50 is eight, so fourteen of the nineteen could not be addressed
> individually at all until `--component` existed. `--component` **filters** the roster and never
> reorders it: two of them given the other way round still install in `bundle.yaml`'s order, because
> the order is the roster's property and a flag that reordered it would be a second place the order
> is written.
>
> ⚠ **The usage text went on saying it anyway until #74, and now it counts instead of claiming.**
> `install.sh --help` prints the phases and how many components each holds, read out of `bundle.yaml`
> at the moment it is asked. The four numbers in the paragraph above are this file's own and are
> counted here on 2026-09-05; they go stale when the roster moves, and `--help` does not.

> ⚠ **A selector that matches nothing is an error, and until 2026-09-03 it was a green run.**
> `--phase 99` printed one empty phase header and exited 0 under *"Bundle applied"*; `--verify
> --phase 99` printed *"Every pin resolves"* having resolved none. `bundle.yaml` § owed,
> `a-selector-that-matched-nothing-reported-success`.

`deploy/bootstrap/` is a different job and the two must not be merged. `bootstrap/` installs
**Cyber Cloud onto a cluster** with `kubectl` and checked-in YAML only, because it is what an
operator runs when the platform is the thing that is broken — `deploy/README.md` § The platform's own
cluster is not Kamaji-hosted. This directory installs **operators into a cluster the platform will
manage**, may use `helm`, and is not part of any repair path. `deploy/managed-cluster/`, which
`deploy/README.md` lists and which does not exist, is where an environment's overrides of this bundle
belong.

## What this bundle does not install

* **A CSI driver, and a replicated storage class.** ✅ The *storage class* half of this bullet closed
  on 2026-08-20 — `charts/bundle/openebs-localpv` installs the default class the eleven managed charts
  that name one were waiting for. Two things it is not, both said here because both are easy to
  assume. It is **not a CSI driver**: `provisioner: openebs.io/local` is a pre-CSI external
  provisioner with no `CSIDriver` object and no node plugin. And it is **not replicated**: one copy,
  on the node the pod landed on, with a requested size that nothing enforces. A node loss loses that
  node's volumes. The replicated stage is declared and off —
  `openebs-localpv/component.yaml` § which stage is on has the parts list, `bundle.yaml` § owed,
  `the-replicated-stage-is-not-installed`, has the debt, and ADR-011 footnote 1 has the decision
  behind it: **no LINBIT contract; the platform runs LINSTOR and DRBD unsupported.**
* **An ingress controller or a load-balancer implementation.** `charts/managed/cloud-shell` renders a
  `networking.k8s.io/v1` Ingress, which is a built-in kind and so does not fail the coverage check,
  and which nothing here serves.
* **OpenBao.** ADR-011 picks it over Vault and `docs/plan/18` puts one cluster per region with a
  namespace per tenant. Several `conformance.yaml` files record `listKeys` having no handler for want
  of it. It is platform infrastructure rather than a managed cluster's operator layer, so it belongs
  to `deploy/platform/`.
* **An operator for NATS, FerretDB, Harbor or Qdrant, because none of the four exists to install.**
  See below.

## The four rows with no operator, each checked rather than repeated

ADR-010 clause 1's amendment says its survey "is a survey of *software choices* and only sometimes a
survey of *operators*". Four rows in this catalogue are the *only sometimes*. Every claim below was
resolved against the GitHub API on 2026-08-19.

| Row | What is there | Status |
|---|---|---|
| NATS | `nats-io/nats-operator`, the only project that ever served a `NatsCluster` | **Archived.** Last release v0.8.3, 2021-11-20 |
| FerretDB | Nothing. `FerretDB/ferretdb-operator` answers 404 | **Does not exist.** The row is a Deployment in front of a CloudNativePG `Cluster` |
| Harbor | `goharbor/harbor-operator` | **Archived.** Last release v1.3.0, 2022-07-02 |
| Qdrant | `qdrant/qdrant-operator` answers 404 | **Does not exist.** The organisation publishes API definitions, not a controller |

So those four charts render plain workloads, and that is a necessity rather than a style. It is also
why `charts/managed/harbor` renders fourteen objects — `charts/README.md`: "there is no Harbor
operator, so the workload is the chart".

⚠ **A fifth row was found while pinning this bundle and it is a different shape from all four.**
`spotahome/redis-operator` is archived, its newest operator tag is a pre-release, and its published
chart pins an image tag that does not exist in the registry it names. The operator exists, installs,
and works; what it does not have is anybody to fix it. See
`charts/bundle/redis-operator/component.yaml` and `bundle.yaml` § owed.

## What this bundle pulls

A chart version is immutable once published, which is what makes `--verify` a real answer to *does
the pin still resolve*. **The image tag inside that chart is not.** So a bundle whose every pin
resolves can be running bytes somebody rebuilt last night, and until 2026-09-03 nothing in this
directory knew which images those were.

Now every component records them. `images:` lists each container image the pinned artefact renders,
with the digest its registry served when somebody looked, and `./charts/bundle/images.sh` re-renders,
re-resolves and compares:

```bash
./charts/bundle/images.sh                      # compare every component against its record
./charts/bundle/images.sh --component kamaji   # one component
./charts/bundle/images.sh --resolve            # regenerate the block after a bump
```

**Thirty-two images across eighteen components**, counted on 2026-09-03; `prometheus-operator-crds`
renders CustomResourceDefinitions and no container, and says so in `rendersNoWorkloadImages:`.

> ⚠ **A record, not a pin, and being exact about that is the point.** The tag is still what reaches
> the kubelet. This detects a tag that moved; it does not prevent one. Preventing it needs a values
> override per chart and several charts have no digest key — `redis-operator/component.yaml`
> documents that case: its template composes `repository:tag` and nothing else, so an `image.digest`
> value would be a key that silently does nothing, which is worse than a tag because it reads as a
> stronger pin than it is.

> ⚠ **The row that used to say redis-operator was the exception was wrong, and the correction is the
> more useful half.** It pinned a *tag* and recorded an `imageDigest:` beside it whose own comment
> said `install.sh --verify` compared it. Nothing read that key — `verify_component` reads the chart
> and manifest pins and has never read a digest. The true count of images checked by digest was zero
> of nineteen. See `bundle.yaml` § owed, `images-are-not-pinned-by-digest`.

> ⚠ **Two images in this bundle are `latest`.** `clickhouse-operator` renders
> `bitnami/kubectl:latest` and `kamaji` renders `cfssl/cfssl:latest` — neither is ours to fix, both
> are recorded with the digest they resolve to today, and `images.sh` is what will notice when they
> move. `bundle.yaml` § owed, `two-images-in-this-bundle-are-latest`, has the reading on why
> `bitnami/kubectl` is the sharper of the two, and why the two untagged references in the Kamaji
> provider's CRD schema are deliberately *not* counted here.

The scan ADR-011 § Enforcement asks for is a different thing again, and `build/Build.Licence.cs`
carries the measurement showing it cannot be written against the allow-list that ADR names: 76 of
the 99 packaged components in `mcr.microsoft.com/dotnet/aspnet:10.0` declare GPL or LGPL, so the
gate would fail on our own base image. Which list answers which question is issue #18's decision.

## Verification, and its honest limit

**Three of the nineteen components are installed onto a real cluster by CI. Sixteen are not, and the
state of the tree says which in `bundle.yaml` § owed rather than implying otherwise.**

> ⚠ **The denominator here read "eighteen" until 2026-09-02 and had been wrong since
> `openebs-localpv` landed.** `bundle.yaml`'s `components:` holds nineteen rows and this directory
> holds nineteen subdirectories. A count written in words is a claim nothing checks, and this one
> outlived its own correction in `deploy/README.md` and in `build/Build.Bundle.cs`' prose too. The
> owed row that tracked it is no longer *named* after a count either — three names in a row carried
> a number that was wrong by the time somebody quoted it, so it is
> `most-of-the-roster-has-never-been-installed`.

`test/CyberCloud.Bundle.Cluster.Conformance` starts an empty k3s — **a fresh one per test class, so
no class's assertions are about a cluster another one touched** — and runs **this directory's own
`install.sh`**, not a re-implementation of it.

**cert-manager, `--phase 15`.** Asserts the two things a CRD apply could not fake: that
`cert-manager.io/v1` is served afterwards, and that a self-signed `Certificate` reaches `Ready` with
a parseable certificate in the Secret it names. The second assertion is the one that needs the
controller running, the webhook admitting and the issuer reconciling.

**openebs-localpv, `--phase 25`.** Asserts that a claim **naming `openebs-hostpath` explicitly**
binds — once a pod mounts it, because the class is `WaitForFirstConsumer` — to a `PersistentVolume`
whose class, provisioner and node-local path the API server agrees are this component's; and that
the default-class annotation landed on **our** class, on a cluster that now has two defaults.

> ⚠ **Every clause of that second paragraph is load-bearing, and the reason was measured rather than
> argued.** k3s ships Rancher's `local-path` and marks it default. A probe run against this fixture
> with nothing installed binds a bare claim, succeeds its pod, and produces a `local-path` volume
> under `/var/lib/rancher/k3s/storage` — so the obvious version of this test is green with the
> component uninstalled, and since k3s's own class also waits for a first consumer, "there is a pod"
> does not distinguish it either. `bundle.yaml` § owed, `one-volume-has-been-provisioned`, has the
> readings.

**openebs-localpv and cloudnative-pg, one run, `--component` twice.** The sentence this directory
exists for, and it was unexercised until 2026-09-03. One `install.sh` invocation installs the storage
component (phase 25) and the PostgreSQL operator (phase 50) onto **one** cluster, in the roster's
order and not the command line's. Then `helm template charts/managed/postgres` renders a
`postgresql.cnpg.io/v1` `Cluster`, the API server admits it against the definition the bundle just
installed, and **CloudNativePG** — not the test — creates a `PersistentVolumeClaim` on
`openebs-hostpath`, binds it to a volume under `/var/openebs/local`, and brings the database to
`Ready`.

> ⚠ **One field on the API server is what separates this from the storage case above.** The claim
> carries a *controller* `ownerReference` to the `Cluster`, whose kind and api-group are asserted, so
> it cannot be a claim the test wrote — which is exactly the criticism `bundle.yaml` § owed,
> `one-volume-has-been-provisioned`, makes of itself. See `an-operator-created-and-bound-the-claim`.

What that supports is **the install mechanism**, and now one path through it end to end: the script
runs unattended against a cluster it is handed, reads a pin out of a `component.yaml` rather than
carrying one, two components install onto one node without fighting, and its `--wait` makes
"installed" mean "serving" — **for a `helm` component**. What it does not support is the roster.
Sixteen pins are still resolved-but-never-applied *by a test*.

> ⚠ **The `--wait` clause was false for six of the nineteen, it took reading the script to notice,
> and half of it is now true.** The `manifest:` branch was a bare `kubectl apply --server-side` with
> no wait of any kind; the one `kubectl wait --for=condition=Established` it could reach ran only for
> a component that has a `manifestExtra`. Since #74 it runs after **every** manifest apply — after
> each component rather than once per phase, because phase 40's two providers admit against
> definitions the rows before them *in the same phase* installed, and a wait that fires after every
> apply gives the boundary property as well. What is still missing is the operator: nothing waits for
> a manifest component's Deployment to be Available, and that wait stays unwritten because no pod of
> any of the eight it would name has ever run. `bundle.yaml` § owed,
> `the-manifest-path-waits-for-nothing`, has every reading and the eight names.

> ⚠ **The `manifest:` branch has now been run — by hand, on 2026-09-05, against an API server with no
> kubelet.** All six components were applied through `install.sh` itself onto one
> `rancher/k3s:v1.35.7-k3s1` started `--disable-agent`, because the host's Docker reports `Cgroup
> Version: 1` and 1.35's kubelet refuses to start on such a host. All six exit `0`; the two-document
> path ran for the first time. **A reading taken once by a person is not a gate**, and this one is
> recorded as what it is: nothing re-runs it, and `kubectl` has still never been invoked by this
> script under test.

**The phase *order* is exercised, the phase *barrier* is half exercised, and they are different
claims.** A full `--dry-run` — no cluster, under a second — asserts that all nineteen components are
attempted once each, in the roster's order, under ascending phase headers, and that every `manifest:`
component is followed by an establishment wait before the next component starts. That is the only
assertion here that covers every row. What a dry run cannot answer is whether "installed" implies
"serving" at a boundary, which is the paragraph above. `bundle.yaml` § owed,
`most-of-the-roster-has-never-been-installed`, keeps the full list.

> ⚠ **A defect the install found rather than the reading.** `cert-manager/component.yaml` recorded
> that dropping `crds.enabled: true` would produce "a controller and no Certificate kind", noticed
> three phases later by a Cluster API webhook. Running it says otherwise: the chart's own
> post-install `startupapicheck` Job fails and `helm` exits non-zero after about six minutes. The
> flag's absence is loud, not silent — better than the file assumed, and now recorded there.

What *is* verified, on every build, by the Bundle gate:

* every component's manifest is complete and its pin is in one place;
* every declared licence is on ADR-011's allow-list;
* every group/version any managed chart renders is served by exactly one component;
* every component either declares what it serves or argues, in prose, that it serves nothing —
  and never both. ⚠ Verified by sabotage on 2026-08-20 rather than by reading: removing
  `servesNoDefinitions:` from `openebs-localpv/component.yaml`, replacing it with
  `servesNoDefinitions: true`, and adding a `serves: storage.k8s.io/v1` beside it each turn this row
  red, with a different message;
* the roster and the directories agree;
* no commit changes a pin and a managed template together.

What is verified **by hand, on a date recorded in each `component.yaml`**: every chart repository,
chart version, release tag and manifest URL was resolved against the registry or API that serves it,
and every `serves:` line was read off the CustomResourceDefinition documents the pin actually
installs — not off a README. `./charts/bundle/install.sh --verify` repeats that pass.

What is **not** verified, and why not:

Task #95 capped container-backed suites at four concurrent — `CC_TEST_CONTAINER_PARALLELISM` in
`build/Build.Test.cs` — because a ten-CPU host starved itself running ten k3s suites at once. ⚠ That
literal four is history: the cap is derived from the host now, and since #77 the suites that hold a
*cluster* have a second cap of their own at **one** — `build/Build.Test.cs`
§ `ClusterBackedSuiteDegree`, and build/README.md § "The cluster degree is 1" has the reasoning. The
lane is therefore narrower than the sentence below assumed, not wider.
Nineteen components, three of which run virtual machines, do not fit in that lane. A suite that
installed two of them and asserted the bundle works would be the failure class this repository has
shipped roughly ten times: **a check that answers a narrower question than it appears to**. So the
honest deliverable is a documented, reproducible install procedure plus a gate over the manifests,
and the cluster-backed proof is named as owed rather than faked.

That paragraph used to end with the next step rather than a plan — one cluster-backed case, behind
the same skip the other cluster suites use, installing **one** component and saying so in its own
name. That is the suite described above; it took cert-manager rather than a phase-50 row because
cert-manager's readiness is observable from outside and a data-service operator's is not.

**Costs, measured on a ten-CPU host rather than estimated:** a green run of the suite was **2 m 20 s
to 3 m 15 s** with two installing classes — roughly 80 s for Testcontainers to bring up k3s, 45 s
for the helm install with `--wait`, the assertions in under a second, and the rest variance in what
the machine was already doing. A red run costs more: the sabotage that removes `crds.enabled` takes
**6 m 40 s**, because helm retries its post-install hook before giving up. The
suite takes `ClusterSlot`, the same cross-process permit the other **fourteen** assemblies built on
`ClusterInfrastructure` take, so it does not widen the concurrency Task #95 capped — it lengthens the
serial tail on a machine where a daemon answers, and costs nothing at all on one where none does.

> ⚠ That count read "fifteen" until 2026-09-05 and was one too many: fifteen assemblies take
> `ClusterSlot` in total, this one included. Two more hold a k3s and take it not at all —
> `CyberCloud.Kubernetes.Tests` and `CyberCloud.AppHost.Tests` — which is seventeen cluster-backed
> suites under three unrelated permits, and is what #77 turned out to be. `build/` now caps all
> seventeen itself; the permit is no longer the only thing holding the line.

**With the cloudnative-pg class it is 4 m 27 s to 4 m 47 s green across three runs, 9 tests, none
skipped, measured 2026-09-03.**
The class costs about **1 m 50 s**: 26 s for `install.sh` to put both components on the cluster
(cheaper than cert-manager's single row, which pays a `startupapicheck` Job), 8 s to the operator's
claim, 18 s to `Bound`, 68 s to `Ready` — the bulk of that last figure being the
`ghcr.io/cloudnative-pg/postgresql` pull — and a second k3s start for the rest.

> ⚠ **The lane was decided rather than deferred, and the answer is "here", which is not the same as
> "cheap".** The only nightly lane that exists is `Build.E2E`, and its own preconditions refuse to
> run without `--e2e-base-url` pointing at a real staging deployment and a `cyc` CLI to drive — a
> Testcontainers k3s suite moved there would be run by nothing at all, which is worse than slow. A
> lane for container-backed suites that are too slow for per-PR and need no deployment does not
> exist; creating one is what `bundle.yaml` § owed,
> `most-of-the-roster-has-never-been-installed`, now records as owed.

**The "nothing to run" trap was checked rather than reasoned about.** A run with `helm` off `PATH`
reports *1 passed, 1 skipped* in 414 ms — not "Zero tests ran", which `--minimum-expected-tests 1`
treats as a failure. That is what the second, daemon-free test class is for. ⚠ The equivalent run
with **no Docker daemon** is the one path here that has not been exercised end to end: pointing
`DOCKER_HOST` at a dead endpoint does not reproduce it, because Testcontainers falls back to the
default socket when the override does not answer. The two branches record their failure into the same
field and produce the same skip, so the untested half is the container start and not the reporting.

That paragraph used to say the next row was **not** a second component but the phase barrier or a
`manifest:` one, and half of it was right. Two components on one cluster came cheap — 26 s, because
neither has a post-install hook — and it arrived alongside the thing that was actually worth buying,
which is an *operator* creating the claim. The barrier turned out not to be a matter of test coverage
at all: for the six `manifest:` components it is not implemented, and that is a fix rather than a
test.

So the next row is a **`manifest:` component**, and it is now two questions rather than one. It would
be the first time `install.sh`'s `kubectl` path ran under test, and it is the only way to find out
what those six components need to be waited on for — which is what
`bundle.yaml` § owed, `the-manifest-path-waits-for-nothing`, refuses to guess at in advance.
`rabbitmq-cluster-operator` is the cheap one: phase 50, one document, no second apply, and a
`rabbitmq.com/v1beta1` `RabbitmqCluster` that `charts/managed/rabbitmq` already renders.

> ⚠ **Half of that was answered on 2026-09-05 without a suite, and the half it answered is the half
> that needed no pods.** All six were applied through `install.sh` against an API server with no
> kubelet, which settled what they create — eight Deployments in eight namespaces, none of them the
> `<component>-system` the script computes — and which is why the establishment wait could be written
> and the Availability wait still cannot. **The suite is still owed**, and it is owed more sharply
> than before: what it has to buy now is a *running operator*, not an inventory. `bundle.yaml` § owed,
> `the-manifest-path-waits-for-nothing`.

See [ADR-010](../../docs/plan/02-technology-decisions.md) § ADR-010, ADR-011 § The licence audit, and
[docs/plan/12](../../docs/plan/12-managed-data-services.md) § The pattern, once.
