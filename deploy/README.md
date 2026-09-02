# `deploy/` — how Cyber Cloud itself is installed

```
deploy/
├── bootstrap/          # what you run on the FIRST cluster, by hand, once   ← exists
├── platform/           # helmfile/kustomize for the platform chart per environment
└── managed-cluster/    # the bundle applied to a cluster the platform adopts or creates
```

`platform/` and `managed-cluster/` are not written yet. `bootstrap/` is, because it is the one that
has to work when nothing else does.

## ⚠ The operator layer is installed from `charts/bundle/`, not from here

`managed-cluster/` is described above as "the bundle applied to a cluster the platform adopts or
creates", and **the bundle itself landed on 2026-08-19 in [`charts/bundle/`](../charts/bundle/README.md)
rather than here**. The split is worth stating, because the two directories both install things onto a
cluster and only one of them is on a repair path.

| | `deploy/bootstrap/` | `charts/bundle/` |
|---|---|---|
| Installs | Cyber Cloud itself | nineteen third-party operators, a CNI, cert-manager and a storage class |
| Onto | the cluster the platform runs on | a cluster the platform will manage |
| Tools | `kubectl` and checked-in YAML only | `helm` and `kubectl` |
| Run when | the platform is the broken thing | a managed cluster is being prepared |

Everything in `bootstrap/` is `kubectl` and checked-in YAML because **its availability is what is being
repaired** — see § The platform's own cluster is not Kamaji-hosted below. `charts/bundle/install.sh` is
under no such constraint: it uses `helm`, it reads its pins out of nineteen `component.yaml` files, and
nothing about a broken platform makes it unusable, because nothing about it involves the platform.

So `bootstrap.sh` must not grow a `--bundle` flag and `install.sh` must not learn to install a silo.
What `deploy/managed-cluster/` is still for is the **per-environment overrides** of that bundle — an
air-gapped registry mirror, a storage class, a CNI configured for one datacentre's fabric — which are
environment facts and do not belong beside a pin.

⚠ **Neither the platform's own cluster nor a CI run installs `charts/bundle/` today.** Every pin in it
was resolved against the registry that serves it and the apply path has never been exercised —
`charts/bundle/README.md` § Verification, and its honest limit says why, and `bundle.yaml` § owed
records it as the first thing that directory owes.

## `bootstrap/` answers the chicken-and-egg

The platform manages clusters, but something has to run the platform. The decision is already made:
Cyber Cloud is **installed on an existing cluster by hand, manages a second one, and moves onto that
second one once it is boring**. `deploy/bootstrap/` is that hand-installation.

It is kept honest by being **the same thing CI uses to stand up e2e**. A bootstrap procedure that
only exists in a runbook is a bootstrap procedure that does not work.

## ⚠ The platform's own cluster is not Kamaji-hosted

[docs/plan/09 § The platform's own cluster](../docs/plan/09-kubernetes-fabric.md) sets out a
four-phase migration, and phase 3 — the platform running on a cluster the platform manages — has a
circular dependency. Its written answer has three parts, and the third is a constraint on this
directory:

> **Cluster B's control plane is not Kamaji-hosted by us.** It is a standalone cluster (Talos or
> whatever the operator runs), because a hosted control plane whose host is the thing that broke is
> not recoverable. In-house *tenant* clusters are Kamaji-hosted; the platform's cluster is not.

So: **in-house tenant clusters get a Kamaji-hosted control plane; the cluster Cyber Cloud itself runs
on never does.** A Kamaji control plane runs as pods on a host cluster. If the platform's own control
plane lived there, the failure that takes out the host takes out the API server you would use to
repair it — and `bootstrap.sh`, which needs a reachable API server and nothing else, has nothing to
talk to.

The second part of that answer is why `bootstrap/` is a first-class directory rather than a runbook:

> **`deploy/bootstrap/` remains supported and tested forever** — it is what an operator runs to repair
> or reinstall the platform with no platform running. It is exercised by every e2e run, so it cannot
> rot.

Everything in `bootstrap/` is therefore `kubectl` and checked-in YAML. No `cyc`, no portal, no Orleans
client, no call to the platform API — nothing whose availability is the thing being repaired.

## What an operator actually types

```bash
cp deploy/bootstrap/shards.env.example ./shards.env
$EDITOR ./shards.env                      # real connection strings; do not commit this

./deploy/bootstrap/bootstrap.sh \
    --image registry.example.com/cybercloud/silo@sha256:… \
    --shards ./shards.env

helm install cybercloud charts/platform --namespace cybercloud --set image=…
```

`--dry-run` renders every manifest and applies nothing. `--namespace` and `--context` do what they
look like.

## The order, and why each step is before the next

| # | Step | Must precede | Because |
|---|---|---|---|
| 1 | `Namespace` | everything | The `pre-install` schema hook is scheduled before the chart's own manifests, so a chart-templated namespace does not exist yet |
| 2 | The two `orleans.dot.net` CRDs | any silo | Nothing creates them at runtime — see below. Cluster-scoped, so this needs the operator's rights, not a ServiceAccount's |
| 3 | `ServiceAccount` + `Role` + `RoleBinding` | any silo pod | A pod that starts before its Role exists fails its first membership write, not its scheduling |
| 4 | `Secret cybercloud-durable-shards` | the schema job | It is the job's entire configuration, and the silos' |
| 5 | The durable-schema `Job`, **to completion** | any silo | `Orleans.Multitenant`'s bootstrap provider opens the shard eagerly, so a silo that starts first fails at *start* |

Step 2 earns its own line. `Orleans.Clustering.Kubernetes` 10.0.1 makes exactly five Kubernetes calls
— create, get, list, replace and delete on namespaced custom objects — and **no `apiextensions.k8s.io`
call at all**. Read off the shipped assembly, not off its README. A silo on a cluster without those
definitions does not create them and does not fall back; it fails, and the failure names a custom
resource rather than a missing CRD.

## The gap this closes

`Microsoft.Orleans.Persistence.AdoNet` ships **zero SQL** and does not migrate.
`AdoNetGrainStorage.Init` runs `SELECT QueryKey, QueryText FROM OrleansQuery` and expects four rows;
against a fresh database that is `relation "orleansquery" does not exist`. The DDL is two files from
the `dotnet/orleans` repository, applied in order, and
`CyberCloud.ServiceDefaults.Storage.OrleansAdoNetSchema` embeds both.

[docs/plan/05 § The shard map](../docs/plan/05-state-and-storage.md) says a shard is added by starting
a server and putting it in the map. **The step between those two clauses is this job**, and until now
nothing outside `CyberCloud.AppHost` ran it.

## The five failure classes

### Idempotence

Re-running `bootstrap.sh` must not fail. Three layers, and the interesting one is not the Helm one:

1. **The DDL.** `OrleansAdoNetSchema.ApplyAsync` probes `SELECT to_regclass('orleansquery')::text`
   before touching a shard and returns `false` — not an exception — when the relation is already
   there. Re-application is a no-op at the level that matters, and it is a no-op *in the program*
   rather than in a wrapper, so every caller inherits it. The scripts themselves are bare
   `CREATE TABLE`, not `CREATE TABLE IF NOT EXISTS`; `IF NOT EXISTS` was never on the table, because
   the files are copied verbatim from `dotnet/orleans` and editing them makes them ours to maintain.
2. **The Job object.** A `Job`'s `spec.template` is immutable. `kubectl apply` over a completed Job
   with a new image fails with `field is immutable`, which reads like a broken script rather than a
   converging one. `bootstrap.sh` deletes the Job with `--wait=true` and creates it fresh; the chart
   gets the same behaviour from `helm.sh/hook-delete-policy: before-hook-creation`.
3. **Everything else.** Namespace, CRDs, RBAC: `kubectl apply`, which converges. The Secret uses
   `kubectl create … --dry-run=client -o yaml | kubectl apply -f -`, because plain
   `kubectl create secret` fails with `AlreadyExists` on exactly the run an operator makes while
   rotating a password during an incident.

⚠ **One residual hole, and it is in the probe, not in the manifests.** The probe asks about
`orleansquery` only. If a run dies *between* `PostgreSQL-Main.sql` and `PostgreSQL-Persistence.sql` —
a pod evicted in that window — the shard has `orleansquery` and no `OrleansStorage`, every later run
concludes "already applied" and skips it, and the silo then fails with the four-rows error at start.
Recovery is manual, and destructive only of an empty schema:

```sql
DROP TABLE IF EXISTS orleansstorage, orleansquery;
DROP FUNCTION IF EXISTS writetostorage;
```

then re-run bootstrap. Nothing in `deploy/` can close that window; closing it means the probe checking
for all three objects, in `CyberCloud.ServiceDefaults`.

### N silos racing

**Proof by construction, in two parts.**

*A silo cannot apply the schema.* `CyberCloud.Silo.Host`'s `Program.cs` tests for
`--apply-durable-schema` on its first executable line and `return`s from the one-shot path before
`OrleansApplication.CreateSilo` is ever called. `OrleansAdoNetSchema` has exactly one production call
site, inside that branch — the rest are three test fixtures with their own copies. There is no code
path from a running silo to a `CREATE TABLE`.

*The job runs one pod.* `completions: 1` and `parallelism: 1`, written out rather than defaulted so
that removing them is a visible edit.

⚠ **What that does not cover**, said because it is the honest limit: the Job controller creates pods
at-least-once. Under a node partition it can start a replacement while the original still runs on the
unreachable node, and probe-then-`CREATE` is not atomic. The blast radius is small and self-healing:
the loser fails on `42P07 duplicate table`, Npgsql's implicit transaction around a multi-statement
batch rolls its partial script back, the pod exits non-zero, and the next attempt (`backoffLimit: 4`)
probes, finds the schema present, and succeeds. The one bad interleaving is the one in § Idempotence
above — a winner that dies between the two scripts.

### Ordering

`Orleans.Multitenant`'s bootstrap provider opens the shard eagerly, so a silo that starts before the
schema exists fails at *start*, not at first write. `CyberCloud.AppHost` expresses this with
`WaitForCompletion(durableSchema)`. The Kubernetes equivalents, in order of preference:

- **Inside the chart: a `helm.sh/hook: pre-install,pre-upgrade` Job.** Helm runs each hook and *waits
  for it to reach a completed state* before applying the release's own manifests. That wait is the
  literal equivalent of `WaitForCompletion`, and it is why the hook shape is right and an ordinary Job
  in the same release is not — an ordinary Job is applied concurrently with the Deployment it is meant
  to precede.
- **Outside the chart: `bootstrap.sh` polls the Job to completion** before printing the `helm install`
  command. It polls `.status.succeeded` *and* the `Failed` condition rather than using
  `kubectl wait --for=condition=complete`, which waits out the full timeout on a job that has already
  failed.

There is no third primitive. An init container on the silo pod running the same argument would
reintroduce exactly the N-way race the Job exists to avoid, one container per replica.

⚠ Getting the order wrong is loud and recoverable rather than corrupting: the silo crashes, the
kubelet restarts it, and once the schema lands it comes up. The cost is CrashLoopBackOff's backoff,
which reaches five minutes — so a cluster that looks stuck for five minutes after a fast schema job is
this, not a hang.

### Multiple shards

[docs/plan/05 § Durable](../docs/plan/05-state-and-storage.md) starts at 16 shards, so "one Job per
shard, or one Job iterating?" is a real question. **One Job, iterating**, for three reasons:

- **One configuration.** The job binds the same `CyberCloud:Storage:Durable:Shards` section a silo
  binds, from the same Secret. Sixteen Jobs is sixteen chances for the job's shard set and the silos'
  to diverge, and a shard the silos know about that no job visited is the exact failure being
  prevented.
- **Retry is resume.** `ApplyAsync` walks the shards sequentially in `StringComparer.Ordinal` order and
  probes each before touching it. **When shard 9 fails, shards 1–8 are already applied** — the process
  exits non-zero with an `InvalidOperationException` naming shard 9 and quoting the error a silo bound
  to it would hit — **and the next attempt re-probes 1–8, skips them, and arrives back at 9.** The
  order is deterministic, so that is a property rather than a hope, and it is what makes a single Job
  safe where a fan-out would only be faster.
- **Sixteen pod start-ups buy nothing.** The whole run is a handful of `CREATE`s.

⚠ **The cost, stated: it is fail-fast, not continue-on-error.** A shard that is genuinely unreachable
blocks every shard after it in ordinal order, forever. That is the right trade — a green job with
seven unwritten shards is strictly worse than a red one — but it means an operator needs the next
section.

#### When a shard fails

Read the log; it names the shard. Then, in order of what is usually true:

1. **The shard is wrong in the Secret** — password, host, database. Fix `shards.env`, re-run
   `bootstrap.sh`. Applied shards are skipped.
2. **The shard is genuinely down.** Fix the server, re-run. Nothing else was installed and no silo is
   up, so there is no partial-service state to reason about.
3. **The shard is not coming back and the platform must come up without it.** Remove it from
   `shards.env` and re-run — **and remove it from the chart's shard set in the same change**. A shard
   present for the silos and absent from the job is the state this whole directory exists to prevent.
   docs/plan/05 § The shard map has no automatic rebalancing, so tenants assigned to a removed shard
   are unreachable until it returns. That is an outage decision, not a configuration one.

### RBAC minimality

Namespaced `Role`s in the platform's own namespace — never `cluster-admin`, never a `ClusterRole`.
Every verb was read off the shipped assemblies; `bootstrap/20-rbac.yaml` carries the per-rule
evidence. The summary:

| Identity | apiGroup | Resources | Verbs | Why |
|---|---|---|---|---|
| `cybercloud-silo` | `orleans.dot.net` | `silos`, `clusterversions` | `create`, `get`, `list`, `update`, `delete` | The five calls `Orleans.Clustering.Kubernetes` makes. No `watch` — it polls. No `patch` — it replaces |
| `cybercloud-silo` | `""` (core) | `pods` | `get`, `list`, `watch`, `patch` | Verbatim from the example role `Microsoft.Orleans.Hosting.Kubernetes` prints when denied. `patch` writes the `orleans/clusterId` and `orleans/serviceId` labels onto its own pod |
| `cybercloud-gateway` | `orleans.dot.net` | `silos`, `clusterversions` | `get`, `list` | `UseKubeGatewayListProvider` reads the same custom resources. Read-only |
| `cybercloud-durable-schema` | — | — | — | Talks to PostgreSQL and nothing else. `automountServiceAccountToken: false` |

Three deliberate absences:

- **`delete` on pods.** Only `KubernetesHostingOptions.DeleteDefunctSiloPods` needs it, and it is off
  by default. Leaving the verb out turns a flag flipped in a values file into a logged error rather
  than into a silo that can delete its neighbours.
- **Anything on `apiextensions.k8s.io`.** The CRDs are installed by the operator in step 2. A
  compromised silo cannot rewrite the schema of its own membership store.
- **Any cluster-scoped grant at all.** `Orleans.Clustering.Kubernetes` reads its namespace from
  `/var/run/secrets/kubernetes.io/serviceaccount/namespace` and cannot address another one, so a
  `ClusterRole` would grant reach the code has no way to use.

## The shard set is one object

`Secret/cybercloud-durable-shards` is consumed with `envFrom` by **both** the schema job and the silo
pods. Its keys are `CyberCloud__Storage__Durable__Shards__<shardId>`, which the .NET
environment-variable configuration provider maps to `CyberCloud:Storage:Durable:Shards:<shardId>` —
the same section `CyberCloudStorageOptions` binds.

That sharing is the whole mechanism behind "the job and the silos cannot disagree about which shards
exist". It is structural, not procedural: there is no second list to keep in step.

⚠ Shard ids are the shard map's keys and assignment is permanent
([docs/plan/05 § The shard map](../docs/plan/05-state-and-storage.md)). Renaming `durable00` moves
every tenant assigned to it to a shard that does not exist.

## What `charts/platform` must honour

`charts/` is not this directory's to write. This is the interface between them.

**Objects the chart consumes by name and must not template:**

| Object | Name | Why bootstrap owns it |
|---|---|---|
| `Namespace` | `cybercloud` | The `pre-install` hook is scheduled before the release's manifests, and `helm uninstall` must not take the RBAC and the membership records with it |
| `CustomResourceDefinition` | `silos.orleans.dot.net`, `clusterversions.orleans.dot.net` | Cluster-scoped, and Helm's `crds/` directory is installed once and never upgraded |
| `ServiceAccount` | `cybercloud-silo`, `cybercloud-gateway`, `cybercloud-durable-schema` | The hook pod is scheduled before the release's own ServiceAccounts exist |
| `Role` / `RoleBinding` | `cybercloud-silo`, `cybercloud-gateway` | Same, and RBAC that survives an uninstall is what makes a reinstall possible |
| `Secret` | `cybercloud-durable-shards` | Operator-supplied credentials; a chart value would put them in the release history |

Install with `--namespace cybercloud` and **without** `--create-namespace`.

**The schema hook.** `charts/platform` renders `bootstrap/30-durable-schema-job.yaml`'s pod spec —
same image, same `args: ["--apply-durable-schema"]`, same `envFrom` — plus these annotations:

```yaml
annotations:
  helm.sh/hook: pre-install,pre-upgrade
  helm.sh/hook-weight: "-5"
  helm.sh/hook-delete-policy: before-hook-creation,hook-succeeded
```

- `pre-install,pre-upgrade`, and **not** `post-install`. An upgrade that adds a shard must apply its
  schema before the new silo pods roll.
- `hook-weight: "-5"` puts it ahead of any other hook the chart grows. Weights sort ascending.
- `before-hook-creation` is what makes the hook re-runnable: it deletes the previous hook Job, whose
  `spec.template` is immutable, before creating the new one.
- `hook-succeeded` and **not** `hook-failed` — a failed hook's pod must survive for `kubectl logs`. It
  names the shard.

**The rest of the contract:**

1. **The hook's image equals the silo's image, by digest.** The job and the silos share
   `OrleansAdoNetSchema` and its two embedded SQL scripts. One value, referenced twice.
2. **The chart's shard set equals the Secret's shard set.** See § The shard set is one object.
3. **`CyberCloud:Storage:Durable:NullTenantShard` is set explicitly**, to the `platform` shard. Unset,
   every null-tenant platform grain in docs/plan/04 § Grain taxonomy hashes the literal string `Null`
   into the tenant shard list — deterministic, arbitrary, and indistinguishable from working until a
   shard is added and the tenant directory moves.
4. **`POD_NAME`, `POD_NAMESPACE` and `POD_IP` from the downward API** on every silo pod.
   `Microsoft.Orleans.Hosting.Kubernetes` reads exactly those three names, and without `POD_NAMESPACE`
   it fails with `KubernetesHostingOptions.Namespace is not set`.
5. **`ASPNETCORE_ENVIRONMENT` is not `Development`.** That value selects `UseLocalhostClustering` over
   `UseKubeMembership` in `OrleansApplication.CreateSilo` — a cluster of one silo per pod that never
   notices the others.
6. **`serviceAccountName: cybercloud-silo`** on silos, `cybercloud-gateway` on gateways. The `default`
   ServiceAccount has none of the membership rights.
7. **`KubernetesHostingOptions.DeleteDefunctSiloPods` stays off** unless `delete` on `pods` is added to
   the Role in the same change.
8. **A `terminationGracePeriodSeconds` longer than Orleans' graceful shutdown.** Pod identity becomes
   silo identity precisely so that a rolling update's SIGTERM is a graceful `StopAsync` with grain
   migration rather than a 60-second membership gap; a grace period shorter than that shutdown throws
   the benefit away.

## What does not live here

Chart *sources* are in [`charts/`](../charts). This directory is composition and environment
configuration over them — values files, helmfile/kustomize overlays, and the ordering.

See [docs/plan/03 § deploy](../docs/plan/03-repository-layout.md),
[docs/plan/09 § The platform's own cluster](../docs/plan/09-kubernetes-fabric.md) and
[docs/plan/05 § Durable](../docs/plan/05-state-and-storage.md).
