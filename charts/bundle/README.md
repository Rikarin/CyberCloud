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
listed in `ArchitectureGates` beside the other fourteen rows, and it reports `○` rather than `✔`
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
./charts/bundle/install.sh --dry-run          # print every command, run none
./charts/bundle/install.sh                    # apply, phase by phase
./charts/bundle/install.sh --phase 50         # one phase
./charts/bundle/install.sh --verify           # resolve every pin against its registry, apply nothing
```

The script reads `bundle.yaml` and each `component.yaml`; it hard-codes no version. `--verify`
answers the question this directory rots on — *does every pin still resolve* — without needing a
cluster.

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

## Verification, and its honest limit

**Two of the nineteen components are installed onto a real cluster by CI. Seventeen are not, and the
state of the tree says which in `bundle.yaml` § owed rather than implying otherwise.**

> ⚠ **The denominator here read "eighteen" until 2026-09-02 and had been wrong since
> `openebs-localpv` landed.** `bundle.yaml`'s `components:` holds nineteen rows and this directory
> holds nineteen subdirectories. A count written in words is a claim nothing checks, and this one
> outlived its own correction in `deploy/README.md` and in `build/Build.Bundle.cs`' prose too.

`test/CyberCloud.Bundle.Cluster.Conformance` starts an empty k3s — **a fresh one per test class, so
neither component's assertions are about a cluster the other one touched** — and runs **this
directory's own `install.sh`**, not a re-implementation of it.

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

What that supports is **the install mechanism**: the script runs unattended against a cluster it is
handed, reads a pin out of a `component.yaml` rather than carrying one, and its `--wait` makes
"installed" mean "serving". What it does not support is the roster. Seventeen pins are still
resolved-but-never-applied; no `manifest:` component has been applied, so `kubectl` has never been
invoked by this script under test; nothing has installed two components onto **one** cluster; and
the phase barrier is unexercised, because `--phase` is the flag whose own usage text says it
*"skips that guarantee"*. `bundle.yaml` § owed, `two-of-nineteen-have-been-installed`, keeps the
full list.

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
`build/Build.Test.cs` — because a ten-CPU host starved itself running ten k3s suites at once.
Nineteen components, three of which run virtual machines, do not fit in that lane. A suite that
installed two of them and asserted the bundle works would be the failure class this repository has
shipped roughly ten times: **a check that answers a narrower question than it appears to**. So the
honest deliverable is a documented, reproducible install procedure plus a gate over the manifests,
and the cluster-backed proof is named as owed rather than faked.

That paragraph used to end with the next step rather than a plan — one cluster-backed case, behind
the same skip the other cluster suites use, installing **one** component and saying so in its own
name. That is the suite described above; it took cert-manager rather than a phase-50 row because
cert-manager's readiness is observable from outside and a data-service operator's is not.

**Costs, measured on a ten-CPU host rather than estimated:** a green run of the suite is **2 m 20 s
to 3 m 15 s** end to end across repeated runs — roughly 80 s for Testcontainers to bring up k3s, 45 s
for the helm install with `--wait`, the assertions in under a second, and the rest variance in what
the machine was already doing. A red run costs more: the sabotage that removes `crds.enabled` takes
**6 m 40 s**, because helm retries its post-install hook before giving up. The
suite takes `ClusterSlot`, the same cross-process permit the other fifteen k3s-backed assemblies
take, so it does not widen the concurrency Task #95 capped — it lengthens the serial tail by about
two minutes on a machine where a daemon answers, and by nothing at all on one where none does.

**The "nothing to run" trap was checked rather than reasoned about.** A run with `helm` off `PATH`
reports *1 passed, 1 skipped* in 414 ms — not "Zero tests ran", which `--minimum-expected-tests 1`
treats as a failure. That is what the second, daemon-free test class is for. ⚠ The equivalent run
with **no Docker daemon** is the one path here that has not been exercised end to end: pointing
`DOCKER_HOST` at a dead endpoint does not reproduce it, because Testcontainers falls back to the
default socket when the override does not answer. The two branches record their failure into the same
field and produce the same skip, so the untested half is the container start and not the reporting.

The next row is **not** a second component. It is either the **phase barrier** — two components on
one cluster, which is the first thing `--phase 15` cannot reach — or a **`manifest:` component**,
which would be the first time `install.sh`'s `kubectl` path and its establishment wait ran at all.
Both are strictly more than "one more operator installs", and either would want the nightly lane
rather than this one.

See [ADR-010](../../docs/plan/02-technology-decisions.md) § ADR-010, ADR-011 § The licence audit, and
[docs/plan/12](../../docs/plan/12-managed-data-services.md) § The pattern, once.
