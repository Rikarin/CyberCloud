# 23 — Build, CI and Testing

## Build

**Nuke** (`build/`), one entry point for every action, same as Survival and Vixen. `./build.sh <Target>`
locally and in CI, so "works on my machine" and "works in CI" are the same code path.

| Target | Does |
|---|---|
| `Restore` `Compile` | .NET, with CPM and deterministic builds |
| `Generate` | Provider registry → OpenAPI → CLI verbs → SDK → portal forms (ADR-012). **Fails on drift** |
| `Test` | Unit + grain tests, coverage floor per project. `--test-lane Fast` / `Cluster` split the `*.Cluster.Conformance` suites off for CI — § CI shape |
| `Bootstrap` | `deploy/bootstrap/bootstrap.sh`: seven preflight cases, and `--dry-run` against `--kube-context`. The first phase of `E2E`, callable alone ([09 § The platform's own cluster](09-kubernetes-fabric.md)) |
| `Charts` | `helm lint`, generate `values.schema.json` from annotated values, **fail on drift**, package |
| `Images` | Build, SBOM (Syft), sign (cosign), push by digest |
| `Architecture` | The gates below |
| `Licence` | ADR-011 scan over charts and images |
| `Portal` | pnpm install/lint/test/build, performance budget, axe |
| `E2E` | Against a real deployment |
| `Chaos` `Load` | Against a topology the suite starts in Docker — three silos over a real Redis, three PostgreSQL shards (two for tenants, one for the null-tenant grains) and a k3s — and, for `Load`, the real gateway over HTTP. ⚠ Each prints ✔ ○ ✘ per row from a results file the suite writes; ○ is a row the machine cannot host, named. § The chaos invariants and § The load scenarios carry the dated tables |
| `Publish` | NuGet, npm, charts, CLI binaries per RID |

## The architecture gates

These are the enforcement half of [00 § Non-negotiables](00-vision-and-principles.md). Each fails the
build with a message naming the offending type and the rule.

| Gate | Checks |
|---|---|
| **Assembly graph** | The seven rules in [03](03-repository-layout.md) |
| **Storage tier** | Every `[PersistentState]` against `durable-grains.txt`; a Durable binding outside the list needs `[DurableStateRationale]` |
| **Tenant keys** | No string literal containing `|` in a `GetGrain` argument; every tenant-scoped grain interface is `IGrainWithStringKey` |
| **Serializer discipline** | Every `[GenerateSerializer]` type has a stable `[Alias]`; `[Id(n)]` numbers never reused (checked against a committed manifest) |
| **Wire compatibility** | Round-trip every wire type through the **last three released** contract assemblies |
| **Secrets** | No `[Id]` member named `*Password`/`*Secret`/`*Token`/`*Key` outside `CyberCloud.Vault` |
| **No blocking** | `.Result`, `.Wait()`, `async void` banned in grain assemblies |
| **Generated surfaces** | OpenAPI/CLI/SDK/forms regenerate byte-identically from the registry |
| **OpenAPI compatibility** | Published api-versions diffed; a breaking change fails |
| **Labels** | Every reconciler's rendered output carries the seven `cybercloud.io/*` labels — asserted by the conformance suite against real output, not by inspection |

⚠ The wire-compatibility gate against *three* releases rather than one is deliberate: a hotfix branch
will eventually be older than the previous tag, and discovering that during an incident is the worst
time.

## Test layers

| Layer | Tool | Runs | Gate |
|---|---|---|---|
| **Unit** | xUnit v3, NSubstitute, Shouldly | Every PR, < 3 min | Coverage ≥ 70 % per project |
| **Grain** | `Orleans.TestingHost` + Testcontainers (Redis, Postgres, NATS) — ADR-018 | Every PR, < 12 min | All pass |
| **Reconciler** | `k3s` in Testcontainers, real API server, real SSA — the `*.Cluster.Conformance` suites | Every merge to main + nightly. ⚠ Not every PR — see below | All pass |
| **Conformance** | The shared provider suite, per provider | Every PR touching a provider | 100 % — a provider that fails is not registered |
| **Isolation** | `CyberCloud.Isolation` — every provider, every verb, wrong tenant | Every PR | **Zero** findings |
| **Contract** | OpenAPI diff, SDK/CLI regeneration, wire round-trip | Every PR | No breaks |
| **Portal** | Jest + Angular TestBed; Playwright for critical journeys | Every PR | Journeys pass, budgets met |
| **E2E** | Playwright + `cyc` against a real staging deployment | Nightly + pre-release | Green before release |
| **Cluster e2e** | `kind` + CAPI + Kamaji + KubeVirt: create and destroy a real tenant cluster | Nightly, ~20 min | ⚠ The highest-value test in the suite — the one that catches operator drift |
| **Hostile BYO** | Old Kubernetes minor, restrictive PSA, no default storage class, a rejecting webhook | Nightly | The brief's core premise |
| **Chaos** | Silo kills, Redis `FLUSHALL`, shard failover, cluster blackhole, global-cluster blackhole, network partition — `test/CyberCloud.Chaos`, on a topology it starts | Nightly; ~25 min on a laptop | Invariants below, ✔ ○ ✘ per row |
| **Load** | The [00](00-vision-and-principles.md) quality bar — `test/CyberCloud.Load`, at 10 % of the rates below through the real gateway | Weekly + pre-release | Budgets met; the full-scale run is staging's and is owed |
| **Security** | CodeQL, `NuGetAudit`, Trivy on images, secret scanning, ZAP against staging | Every PR + nightly | No criticals |

⚠ **The 70 % is unchanged and one project ships under it.** Until 2026-08-20 the floor had never run
anywhere a developer could see it — `dotnet-coverage` ships no arm64 profiler, so `./build.sh Test`
printed "NOT ENFORCED" on every Apple Silicon machine and an x64 CI runner produced the only numbers
there had ever been. The collector is `coverlet` now and the gate runs everywhere. The debt it found
is carried in `coverage-below-floor.txt`, a reviewed file in the shape of `actions-without-handlers.txt`:
a row names a project and the rate it is held to, an unlisted project below 70 % still fails, a
listed project that drops below its rate fails, and a listed project that reaches 70 % fails until
its row is deleted. `build/README.md § coverage-below-floor.txt` has the reasoning. It carries **one**
project.

⚠ **The Reconciler row left the PR on 2026-09-18, and the reason is a measurement, not a preference.**
This table said "Every PR, < 15 min" of it. On the GitHub-hosted `ubuntu-24.04` runner — 4 vCPUs, so
the suites that hold a k3s run one at a time (`build/Build.Test.cs § ClusterBackedSuiteDegree`) —
`main.yml` run 35027771880 (2026-09-15) spent **24 minutes** on that serial chain before most other
suites could start, and `gate / test` took 29 m 14 s: over the PR budget below by itself. The doc's own
remedy is "parallelism or moving a test to nightly — with a written reason", and this is the reason.
`./build.sh Test --test-lane Fast` runs everything but the sixteen `*.Cluster.Conformance` suites on
every PR; `--test-lane Cluster` runs only those, as its own job, on every merge to main; `nightly.yml`
runs the whole target in one process. `Build.Test.cs § TestLane` has the timestamps and the second
measurement that shaped the split: `CyberCloud.Kubernetes.Tests` and `CyberCloud.AppHost.Tests` hold a
k3s too, and leaving them out of the PR lane put `CyberCloud.Kubernetes` at 10.1 % and `CyberCloud.AppHost`
at nothing, so they stay on the PR. **The coverage floor follows the lane**: the PR lane enforces it
over the suites it ran, the nightly full run over every suite, and the cluster lane does not measure
coverage at all rather than fail every project it does not touch. A merge therefore sees the
reconciler suites the same day, which is what #25 asked for — a suite red on Linux for ten days was
one whose only run was inside a job that was red anyway.

⚠ **The Conformance row validates against the operators' real definitions since 2026-09-18, and for
a month it did not.** Every reconciler renders its custom resource in C#, and the Docker-free suite's
API server was a dictionary that held whatever it was handed; the Reconciler row's k3s served a stub
definition per kind with an open schema. So `charts/managed/seaweedfs-bucket` rendered three fields
in a shape SeaweedFS's operator refuses, and twenty-eight green assertions per run said nothing
(issue #91). The real `CustomResourceDefinition` of every kind a managed chart renders is now
committed beside the bundle component that installs it, under `charts/bundle/<component>/crds/`,
fetched from the pinned release by `charts/bundle/crds.sh`; `FakeKubeCluster` validates every apply
against it — required, type, enum, undeclared fields, bounds, associative-list keys, defaults — and
the k3s harness installs the same bytes in place of the stub. Two gate rows hold the files: **Bundle**
checks offline that every rendered operator-owned kind has one and nothing else is committed;
**Definitions** re-fetches the release and compares bytes, ○ when offline. The first run over every
family found one more wrong shape, in `charts/managed/postgres` (`conformance.yaml` § owed,
`backup-destination-is-not-filled-in`). What the fake still cannot evaluate is a definition's CEL
rules — `charts/bundle/bundle.yaml` § owed, `the-fake-does-not-evaluate-cel-rules`; the k3s lane does.

⚠ **One body per family is one shape per family, and the review of #91 showed what that misses.**
Every row of the shared suite converges `ProviderConformanceCase.Body`, so the definition was only
ever asked about the default body: the Postgres renderer wrote `spec.postgresql_synchronous` — a key
CloudNativePG does not declare — for every server with `synchronousReplication: true`, and the flag
defaults to `false`. `ProviderConformanceTests.EveryPropertyVariantTheSchemaAdmitsRendersAShapeTheDefinitionAdmits`
now derives, from the type's own schema, one body per property value the default does not carry —
booleans flipped, every enum value, the declared bounds, an example or a fixed word for an open
string — writes each through the manager and holds the fake to the committed definitions on every
apply (318 variants across the 25 cluster-backed types on 2026-09-18). Its first run found a second
Postgres shape: a pooling mode the schema publishes and the operator's Pooler refuses
(`conformance.yaml` § owed, `statement-pooling-is-a-mode-the-pooler-does-not-have`). One property at
a time; a pair a reconciler renders jointly is the next gap.

### Skipped by default — the assertions that need a server, and what running them proved

Tracked here rather than left in a commit message, because a test that nobody knows is skipped is
worse than a missing one.

| Where | What is skipped without a server | What it needs |
|---|---|---|
| `CyberCloud.ServiceDefaults.Tests.Storage.OrleansAdoNetSchemaTests` | Five assertions about the durable schema on a real PostgreSQL: a half-applied schema is completed, a complete one is a no-op that takes no advisory lock, four concurrent appliers produce one winner and three clean no-ops, a hand-torn schema is refused with an inventory, and a reachable shard reports reachable | `CYBERCLOUD_TEST_SHARD` set to a **scratch** database — every test starts by running the recovery SQL from `deploy/README.md § Idempotence`, which drops both tables. Deliberately a connection string rather than Testcontainers, so the same assertions run against a container, a local server, or a staging shard |

⚠ **These five were written unrun and have since been run.** On 2026-08-11 all five passed against a
scratch PostgreSQL 17, and the concurrency one was sabotage-tested rather than merely observed
passing: replacing `pg_advisory_xact_lock` with a `SELECT` of the same two integers — leaving the
transaction, the re-probe inside it and every assertion in place — makes
`TwoConcurrentAppliersDoNotCorrupt` fail with a real `23505` on `pg_type_typname_nsp_index`, which is
two appliers running `CREATE TABLE` at once. The other four still passed under the sabotage, which is
the right shape: they are single-applier scenarios and the lock is not what they are about. So the
lock is load-bearing and the test is what holds it.

They remain skipped in CI, which has no shard. Nothing above changes that; what changed is that
"skipped" no longer means "never once observed to pass".

Everything else about that pair of gaps runs everywhere and needs nothing:
`DurableSchemaPlanTests` decides what to apply from an observed set of objects,
`DurableShardHealthCheckTests` probes closed ports and a socket that accepts and never answers, and
`UnreachableShardReadinessTests` starts a real silo with both tiers pointed at closed ports and asks
`/health` and `/api/health` over HTTP.

### The lane that needs a kubelet

Seventeen suites in this repository hold a `rancher/k3s:v1.35.7-k3s1` in Docker, and until
2026-09-15 none of them could schedule a pod on the machine that wrote them. The reason was two
layers down: WSL2's kernel booted cgroup v1 (hybrid), Docker Desktop inherited it (`docker info`:
`Cgroup Version: 1`), and from Kubernetes 1.35 the kubelet's `failCgroupV1` defaults to `true`, so
k3s came up as an API server and shut its agent down — every cluster-backed suite skipped, and the
skips read as a missing daemon. The bundle's six `manifest:` components were first applied against
such a server (#74): definitions Established, no operator ever started.

**What fixed it, on this laptop, and what the next one needs:** `kernelCommandLine = cgroup_no_v1=all`
in `%UserProfile%\.wslconfig`, `wsl --shutdown`, restart Docker Desktop, and `docker info` reports
`Cgroup Version: 2`. After that a k3s node is `Ready` in 7 s, `CyberCloud.Kubernetes.Tests` went
from 36 `K3sFixture` failures to 146/146, and the whole of `charts/bundle/` was installed phase by
phase through its own `install.sh` — issue #2's first real run, whose findings are recorded in
`charts/bundle/bundle.yaml` § owed. The `failCgroupV1: false` kubelet drop-in the k3s fixtures carry
is moot on a v2 host and is kept for a host in the state this one was in.

> ⚠ **Two more things a k3s-in-Docker node has to be, found by installing KubeVirt on one.**
> `/var/run` must be a *shared* mount — `--tmpfs /var/run` is private, and `virt-handler` refuses
> the node — so every k3s recipe in this repository (the AppHost, `K3sFixture`,
> `ClusterInfrastructure`) now starts through `/bin/sh -c 'mount --make-rshared /var/run && exec
> /bin/k3s "$@"'`. And it needs no CNI of its own if kube-ovn is to be installed, which k3s cannot
> be asked for after the fact: kube-ovn is a cluster-creation component and stays off this lane.

**What this lane can and cannot prove**, so nobody re-derives it:

| Proven on k3s-in-Docker (2026-09-15) | Stays for real nodes — the VM lane |
|---|---|
| 19 of the bundle's 20 components installed and serving through `install.sh`; the four Cluster API controllers 1/1 after the `${VAR:=default}` substitution; KubeVirt and CDI `Deployed`; every `waitFor:` returning | **kube-ovn** — needs the `kube-ovn/role=master` node label, a CNI-less cluster, ADR-019 values and OVS kernel modules; refuses at template time here |
| The `.Cluster.Conformance` suites, the reconciler layer, the bundle suite's three helm rows | **LINSTOR/DRBD** — the replicated storage stage, a kernel module (`bundle.yaml` § owed, `the-replicated-stage-is-not-installed`) |
| A cgroup-v2 host is the *only* prerequisite for the per-PR lanes above | **KubeVirt guests** — Docker Desktop's VM lends no `/dev/kvm`, so a Machine here is software emulation; the Cluster e2e row of the table above is this lane's, and it is still nightly-and-unbuilt |

The lane that holds those three is the Hyper-V / real-node lane the Cluster e2e row already names.
It does not exist yet; what changed on 2026-09-15 is that everything *else* no longer waits for it.

### The chaos invariants

Each is an assertion, not an observation:

1. Kill a random silo every 90 s during a provisioning storm → **zero** resources stuck in a
   transitional state after settling; every operation reaches `Succeeded` or `Failed`.
2. `FLUSHALL` the hot tier → **zero** durable state lost, **zero** acknowledged control-plane writes
   lost, full function within 60 s.
3. Fail over a durable shard → writes for that shard's tenants pause and resume; no data loss; other
   tenants unaffected.
4. Blackhole a managed cluster → its resources go `Degraded`, reconciles suspend, **no** operations
   fail, clean resumption on restore.
5. Blackhole the global directory cluster for 10 minutes → **zero** tenant-facing errors; new tenant
   creation fails cleanly with a retryable error.
6. Partition the NATS cluster → streams recover, consumers resume from their cursor, no duplicate
   billing after dedup.
7. Rolling upgrade of a 30-silo cluster under load → **zero** failed tenant requests.

#### The runs — 2026-09-18, `test/CyberCloud.Chaos` on this laptop

The suite induces each fault against a topology it starts in Docker: a Redis hot tier that is also
the reminder table, three PostgreSQL shards (`durable-00`, `durable-01`, and `platform-00` for the
null-tenant grains — the shard map, the tenant directory, the cluster connections), a k3s, and three
silos wired through the same extension methods `CyberCloud.Silo.Host` composes, with
`CyberCloud.Providers.Sample` as the type under storm and the write path composed on the client as
the gateway composes it. Each test writes a `Held` / `Violated` / `Vacuous` row with its numbers and
`./build.sh Chaos` prints ✔ ○ ✘ from the file; a row the suite did not write is ✘, because "the
nightly is green because the file was empty" is the failure the target used to be blocked to avoid.
Two deployment knobs are turned and named in the results file's `topology` block: the cluster
health window at 20 s with a 5 s ping ([09](09-kubernetes-fabric.md)'s 90 s is a deployment
number, not a property of the code) and the Npgsql pool at 20 per shard. ⚠ The membership probes
are the shipped defaults, after a tuned 2 s / 2 misses / 1 vote declared a silo dead while it was
still starting on this shared laptop and Orleans' fatal-error handler took the whole test process
with it. Every silo logs to `artifacts/chaos/silos.log` — `CyberCloud.*` at Information, the rest
at Warning, with the test's own marks (`killing …`, `stopping shard …`) in the same timeline — so
a violated row carries its diagnosis; the first version of the suite gave the silos no log
provider at all, and the two runs of the branch's review that could not reproduce rows 3 and 5
had nothing to read.

**The review, and what it changed.** The first table here (03:48–03:58, the seventh run) was
read by an adversarial review against the code, and four of its rows did not survive the reading.
Row 1's ✔ was not proven for the window the branch's fix is about: the kills were issued after
the writes were sent, nothing asserted that a kill landed on a write in flight, and the review's
own run had both land on nothing — 0 write faults, 0 claims, settled in 0.4 s — with a ✔ beside
it; a kill that landed after `OperationGrain.StartAsync` had the retried `PUT` refused
`OperationInProgress`, which the storm did not tolerate, so the test died before writing a row.
Rows 1 and 2's "every operation reaches Succeeded" and "zero acknowledged writes lost" were proven
with the test as the driver — every accepted operation was `DriveAsync`'d from the client from the
moment of its 202 — so what was shown is what an operation does when driven, not that the platform
drives it; row 2's sentence about the reminder service's local copy ticking was inferred, never
observed. Row 4 printed ✔ with 6/6 reads of the resources showing `Failed` during the blackhole,
which is the opposite of its first clause. And a test that threw mid-fault left the fault in
place — the review's second run had row 1 die on the refused `PUT`, hand rows 5 and 3 a two-silo
cluster with the storm's drivers still going, and both died at seeding with operations stuck
`Running`. Each is fixed in the suite: the kills are timed into a staggered wave and the number
of writes in flight at each kill is recorded, with the row ○ rather than ✔ when it is zero; the
storm's retry client waits out `OperationInProgress` as it waits out a claimed name, and measures
it; rows 1 and 2 watch the operations' durable rows in PostgreSQL until the reminders alone have
taken them to terminal (`ChaosTopology.ObserveUntilTerminalAsync` — not a status call, because
`OperationGrain.OnActivateAsync` re-registers the reminder on activation and a poll through the
grain would arm the driver it is watching for); row 4's `Failed` reads are in its verdict; and
every restore is in a `finally`. The table is the run that followed, 05:41–05:50, over real
sockets; rows 3 and 5 reproduce in it, with row 5's accepted writes all converging after the
shard came back, which is what the first table said and the review's runs could not see.

| # | Fault, as induced | Held? | Measured |
|---|---|---|---|
| 1 | 36 creates in three waves across two tenants on two shards, each wave's `PUT`s issued 30 ms apart; a secondary silo killed under the second and third wave while the later `PUT`s were still on the wire; two replacements started; the settle left to the reminders and watched through the durable rows; every member of both groups swept through the real listing. ⚠ 90 s compressed to the length of a wave — the kill is what the row is about, not the interval | ✔ | 2 kills at 0.6 s and 63.0 s, **each with 6 writes in flight** and 172 activations between them; 4 write calls threw; 1 name was left claimed by a write whose silo died between the claim and the operation start, and its retried `PUT` was the no-op 60 s later — the dead attempt's operation had confirmed the claim and converged on its first reminder tick, which is `OperationGrain.ConfirmClaimAsync` doing what finding 1 says it does; 0 names were held by a dead write's live operation; **the reminders alone drove 35 operations in 37 passes**, the last terminal 91 s after acceptance, at most 2 passes and 1 activation per operation; settled in 187 s with 35 Succeeded, 0 Failed, 0 stuck, 0 orphans, **0 duplicated names**, 36/36 ConfigMaps in k3s |
| 2 | Eight converged widgets and one acknowledged-but-undriven create; `FLUSHALL` on the Redis; every idle activation collected so the reads that follow come out of PostgreSQL; the in-flight create watched, not driven | ✔ | 34 keys flushed, and **2 → 0 reminder rows with them** (the reminder table is in the same Redis); 8/8 durable widgets intact with their rows in the shard; **the in-flight create converged with no call from the test**: one activation and one pass, terminal 58 s after the flush — the reminder service's local copy ticked (its table refresh is every 5 min, the tick at most 1 min away), the tick activated the grain, the activation re-registered the reminder, the pass converged and unregistered it inside the same second, which is why a once-a-second poll of the table never saw the row return; a fresh write was accepted 2.1 s after the flush and converged at 2.1 s against the 60 s budget |
| 3 | `durable-00` stopped for 27 s while a tenant on each shard writes; started again on the same port, in a `finally` | ✔ | bystander on `durable-01`: 14/14 writes accepted; affected tenant: 0 accepted, 14 refused, slowest refusal 2.1 s, and **14/14 reads of its existing state answered from memory**; writes resumed 0.0 s after the shard came back; 0/8 seeded widgets lost |
| 4 | k3s stopped with a create and an update in flight; both driven every 3 s; k3s started again on the same port, in a `finally`; then the first widget's ConfigMap deleted with the raw client and the widget updated | ✘ | 4 passes came back `retrying` (a transport failure, retryable) before the 20 s window closed and the connection went Degraded at 22.4 s; 2 passes after it were suspended with [09](09-kubernetes-fabric.md)'s "Cannot reach your cluster" sentence; **0 operations failed**; the API server answered 9.3 s after the container started and the first unsuspended pass ran at 9.3 s; both operations converged within 9 s of it, the second widget present and the first carrying its new message; the deleted ConfigMap was put back by the next update. ⚠ **6/6 reads of the resources during the blackhole showed `Failed`**, which is the first clause of the invariant not holding — finding 3. The first table printed this row ✔ with the same six reads in its footnote |
| 5 | `platform-00` stopped for 63 s, every activation collected, eight rounds of reads, scope reads and writes for two existing tenants, one new-tenant creation through `IScopeManager.CreateTenantAsync`; the shard started again in a `finally`. ⚠ 10 min compressed to 63 s — the directory snapshot has no TTL ([05 § The tenant directory](05-state-and-storage.md)), so the length of the outage is not the variable | ✘ | 32 reads and 16 writes: **0 tenant-facing errors**; 0/16 accepted writes converged while the directory was gone (the cluster connection is a null-tenant grain on that shard, so the data plane pauses — the silo log has every pass failing at `NamespaceEnsurer` on the connection grain's `ReadStateAsync`, refused by Npgsql in 2 s) and **0 were lost after restore**; a new tenant was possible 0.2 s after the shard came back. **New tenant creation answered in 2.0 s with `CodecNotFoundException: Could not find a codec for type Npgsql.NpgsqlException`** — an exception, not a `Result`, and one about the serializer rather than about the directory. Neither clean nor retryable |
| 6 | Nothing — no silo speaks NATS | ○ | `Microsoft.Orleans.Streaming.NATS` is a prerelease no project references; the AppHost's `nats` container has no client; the metering consumer of [22](22-billing-metering-and-quota.md) is not built. Nothing to partition, nothing to dedup |
| 7 | Every secondary silo stopped gracefully and replaced on a new port, one at a time, under four readers and a writer creating fresh widgets; the cluster brought back to three in a `finally` | ○ | 679 requests over 10 s at 66 rps, **0 failed** this run; 31 creates accepted and every one converged; graceful stop 0.0 s, replacement up in 0.1 s. The run before it saw **4 of 688 fail** with `SiloUnavailableException: Silo unavailable` — requests that landed on a silo as it left — and over the in-memory transport 1 of 673 and 0 of 689. Three silos and one version, not thirty and two: the row is ○ on a zero and ✘ on anything else, and a zero that comes and goes between runs is finding 4 still open, not closed |

**What the numbers said, in order of what it cost:**

1. **The two-phase create had a fourth failure window, and it made ghosts.** The first storm left
   two names claimed by a write whose silo died between starting the operation and confirming the
   claim; the reminder drove both to `Succeeded`, the reaper — which sweeps `Creating` members —
   never saw them, the leases expired at 301 s, the retried `PUT`s created two more, and the sweep
   found 26 `Succeeded` members and 26 ConfigMaps for 24 creates. Fixed on this branch:
   `OperationGrain.ConfirmClaimAsync` finishes step 3 on the operation's first pass and cancels the
   create if the claim is gone; [06 § Two-phase create](06-tenancy-and-resource-model.md) carries the
   corrected story and `ClaimConfirmedByTheOperationTests` pins it. The sweep now asserts no two
   members share a name, and the run above has the fix doing its work under a kill that landed on
   six writes in flight: one claim without a confirm, finished by its own operation's first tick.
   ⚠ `ConfirmClaimAsync`'s "transient" branch, which the first version said retried an unreachable
   index on the next pass, is unreachable: `IndexClaimMachine.Confirm` returns only `Conflict`,
   and an index shard that cannot be reached throws out of the grain call and out of `DriveAsync`
   before the pass — the next reminder tick is the retry, and the pass does not run. The comment
   says so now.
2. **A shard that is gone surfaces to callers as a serializer error.** In rows 3 and 5 every
   refusal was `CodecNotFoundException: Could not find a codec for type Npgsql.NpgsqlException` —
   the grain's activation threw Npgsql's exception, Orleans could not put it on the wire, and the
   caller got an exception about the codec. The silo log now shows the whole chain: `Error from
   storage provider MultitenantStorage.clusterConnection during ReadStateAsync`, `Lifecycle start
   canceled due to errors at stage 'SetupState'`, then `Exception sending message Response
   … -> sys.client` for each operation whose pass hit it. The gateway would answer 500 with no
   detail. Row 5's second clause is violated by exactly this: the failure is not a `Result`,
   carries no `ErrorCode`, and says nothing a client could retry on. Owed: a closed-set error code
   for "the store this needs is unreachable" (there is none — `ErrorCode.All` has no 503), and the
   storage failure caught where the platform grains activate so the scope manager returns it.
3. **A resource whose cluster is unreachable reads `Failed`, and stays `Failed` while suspended.**
   Row 4: every read of both resources during the blackhole answered `ProvisioningState.Failed`,
   because a retryable reconcile failure records itself on the resource
   (`ResourceGrain.CompleteAsync`) and a suspended pass changes nothing afterwards. The operation
   was `Running` and said "suspended, not failed" throughout — the resource said the opposite.
   [09 § Connection health](09-kubernetes-fabric.md) has the portal say "cannot reach your
   cluster" instead of "provisioning failed"; a portal reading the resource's state would say the
   second. The row is ✘ for it, as the invariant's first clause reads. Owed: a transitional state
   or a flag on the resource for "retrying" that is not `Failed`, or a read path that consults the
   operation before reporting `Failed`.
4. **A rolling restart drops a request now and then, and the gateway does not retry it.** Row 7:
   4 of 688 requests met `SiloUnavailableException` as their silo left the cluster in the seventh
   run, 0 of 679 in the run above, 1 of 673 and 0 of 689 over the in-memory transport. Orleans
   surfaces the shutdown to the caller rather than resending, and the gateway maps an unhandled
   exception to a 500 — so a tenant sees a rolling upgrade, some of the time. Owed: a retry of
   `SiloUnavailableException` on the gateway's grain calls (every request type the gateway makes
   is idempotent by design — `PUT`, and reads), or Orleans' own `ResendOnTimeout` shape for it.
5. **The reminder table lives in the hot tier's Redis and a `FLUSHALL` empties it — and what
   brings an operation back was observed this time, not inferred.** Row 2 held with the test's
   hands off the in-flight operation: 2 → 0 rows at the flush, one activation and one pass 58 s
   later with no call from the test, which is the reminder service's local copy ticking from
   memory (its table refresh is every 5 min) and `OperationGrain.OnActivateAsync` re-registering
   the row on the way in. ⚠ The ordering the suite does not induce is the unlucky one: a list
   refresh between the flush and the next tick, after which the operation has no driver until
   something activates it. The flush lands seconds after the acceptance here, so this run met the
   lucky ordering by construction; [25 § R2](25-risks-and-open-questions.md) carries the design
   decision the fact points at.
6. **The dead-write lease is five minutes of tenant-visible 409.** The seventh run's eight orphaned
   claims were freed by the lease and not by anything sooner, which is
   [06](06-tenancy-and-resource-model.md)'s lease working as written; in the run above the one claim
   was finished by its operation's first tick at 60 s, which is the fix in finding 1 turning five
   minutes into one. Whether either number is right for a name a tenant is retrying is a product
   question the runs make concrete.
7. **A tenant's reads survive its shard.** Row 3: 14/14 reads of the affected tenant's existing
   widgets answered while its shard was stopped, from activations already in memory. Reassuring,
   and not a property anything guarantees — a collected activation would have read from the shard
   and failed.
8. **The testing host's kill is not a SIGKILL.** Rows 1 and 7 report the cluster noticing a death in
   0.0 s because Orleans' `KillSiloAsync` still writes the dying silo's Dead row; a pod that is
   SIGKILLed does not, and detection then takes the membership probes — about a minute at the
   shipped defaults. What happens after the death is known is the same either way, and that is what
   the rows assert.
9. **A chaos test that throws has to put the fault back, or the next row is measuring it.** The
   review's runs are the evidence: a storm that died on a refused `PUT` left two silos and its
   drivers running, and the next two invariants failed at seeding for it — "✘ no outcome
   recorded" twice, for faults nobody induced. Every restore is in a `finally` now, and the silo
   log is there for the next time a row does not reproduce.

**Owed, and where:** findings 2, 3 and 4 above — in `CyberCloud.Core`'s error codes and
`ScopeManagerService`, in `ResourceGrain`/the read path, and in the gateway's dispatch — each of
which keeps a row ✘ until it lands; the staging run of all seven at the doc's own scale — 30 silos,
two versions, a real NATS — which is nightly's and does not exist, and needs a suite mode that
attaches to a deployment rather than starting one (`Build.Chaos` refuses a `--kube-context` until
then rather than dropping it; `nightly.yml`'s `chaos` job runs this suite on the runner's Docker
and is red while a row is ✘). The compressions above are stated per row; none changes what the
row asserts.

### The load scenarios

| Scenario | Asserts |
|---|---|
| 10 000 tenants, 1 000 000 resources, 5 000 rps reads | Control-plane read p99 < 25 ms |
| 500 writes/s sustained | Write p99 < 60 ms; reconcile queue does not grow unboundedly |
| ReBAC: 5-deep groups, 10 000 members, 20 000 checks/s | Check p99 < 10 ms warm, < 50 ms cold |
| 2 000 000 resident grains | Silo working set ≤ 12 GB; no activation thrash |
| 1 000 concurrent terminal sessions | Stream latency p99 < 80 ms |
| 500 000 spans/s ingest | No drops below quota; ingest pods scale linearly |

**The load suite runs weekly, not per-PR**, and its results are tracked over time. A 20 % regression
between releases is a release blocker even if the absolute number still passes — the trend is the
signal.

#### The runs — 2026-09-18, at 10 % of the rates above, on this laptop

`test/CyberCloud.Load` is the runnable [24 § Phase 2](24-roadmap.md)'s exit criterion names: the
chaos suite's topology over real sockets, the real `CyberCloud.Gateway.Host` in front of it on a
free port, on its own `appsettings.json`, with JWKS bearer validation against a stand-in issuer the
suite hosts, and an open-loop driver over `HttpClient` at one tenth of each row's rate — 500 rps of
reads, 50 writes/s, 2 000 checks/s, 200 000 resident grains — across 10 tenants on both shards, 20
subscriptions and 80 service-principal callers, which is the population the gateway's own rate limits
need before 500 rps is not a 429. **The budgets are not scaled**: a p99 ceiling does not move with
load, so a budget met here is necessary for the row and not sufficient, and `Build.Load` prints the
scale beside every number. The one budget that is about population rather than speed — the working
set at two million grains — is ○ with the tenth-scale number beside it. The results file carries
`metrics` for what was measured, `vacuous` for what could not be (a sentence each), and behind every
p99 the full distribution and the slowest request of each ten seconds, so a tail can be placed in
time. `load-baseline.json` at the repository root is the run in the table, and it is **provisional**:
docs/plan/23's sentence is about a regression *between releases*, there has been none, and the
branch's review measured the first version's cold p99 moving 50 % between two runs of the same
code on this laptop — a trend rule armed by that file would fire on noise, and did. So the file
carries no `release`, `Build.Load` prints every delta against it and blocks on none, and the first
release stamps the file with its tag to arm the rule (`Build.Load.cs § LoadBaselineFile`). The
table is the run that followed the review, 05:51–05:59; the first table (the sixth run of the
suite, 01:44) is described where a row moved, and the five runs before it are findings 3 and 5.

⚠ **The review found the cold ReBAC number was a cache hit**, and the row's meaning changed. The
check grain's cache is `IPersistentState` on the Hot tier, written after every walk, so
deactivating the grain does not empty it — the next activation reads it back from Redis and answers
a subject it has seen from the cache, in a millisecond, `FromCache` true. The first version's cold
samples re-checked subjects the warm phase had cached every one of, so its 9.7 ms was two hundred
Redis reads. The cold subjects are now members the warm phase never asks about, one per sample,
each asserted `FromCache == false` with the triples the walk visited reported — and there are 500
of them rather than 200, because a nearest-rank p99 over 200 samples is the second-slowest sample.

| Metric | Budget | Measured (p50 / p95 / p99) | Status | How |
|---|---|---|---|---|
| control-plane-read-p99-ms | 25 ms | 1.7 / 5.2 / **6.6 ms**, max 33 ms; 15 003 requests, 0 errors, at 500 of 500 rps for 30 s after 10 s of warm-up; slowest request per 10 s: 15, 17, 33 ms. The first table's run measured 1.6 / 10.0 / 23.9 ms on the same code — finding 3 | ✔ | `GET` of a converged widget, round-robin over the 20 subscriptions and 80 callers: bearer validation, the subject and subscription rate limits, the ReBAC check, the grain read |
| control-plane-write-p99-ms | 60 ms | 23.7 / 32.3 / **45.7 ms**, max 78 ms; 7 500 requests, 0 errors, at 50 of 50 writes/s for 150 s after 5 s of warm-up; slowest per 10 s: 43 49 45 48 54 50 56 65 62 78 54 64 63 73 52 — no stall this run. The first table's run measured 25.8 / 48.1 / **636.7 ms**, max 1 724 ms, slowest per 10 s 62 52 55 56 49 57 59 **1724 1626** 420 51 81 55 69 67 — finding 1, which stands: a stall that comes on one run in two is not gone | ✔ this run | `PUT` of a new widget — the two-phase create of [06](06-tenancy-and-resource-model.md) to its 202 — across the 20 subscriptions on both shards |
| reconcile-queue-depth-slope-per-minute | 0 | **+2.4 items/min** (+9.1 in the first table's run), least-squares over the 7 samples from 60 s into the writes to their end; peak depth 3 004 of 7 750 accepted, at the end of the writes; drained to 0 in 70 s after they stopped; 0 of 7 750 ended `Failed` | ✘ | depth = accepted creates not yet terminal, counted every 10 s through the real listing |
| rebac-check-p99-warm-ms | 10 ms | 0.4 / 1.0 / **1.4 ms**, max 7 ms; 39 992 checks, 0 errors, at 2 000 of 2 000/s for 20 s | ✔ | `ICheckGrain.CheckAsync(read)` over a 5-deep group chain with 1 000 members (plus the 600 cold members), from the cluster client; the graph took 59 s to write |
| rebac-check-p99-cold-ms | 50 ms | 8.0 / 13.6 / **34.7 ms**, max 49 ms; 500 checks, **500/500 `FromCache == false`**, 2 triples visited per walk. The full-consistency walk — no cache, no index, every object's durable row re-read — is 11.1 / — / **47.3 ms** over 100 samples, beside the number rather than in it. The first table's 3.6 / 8.0 / 9.7 ms was the cache | ✔ | the first check after the check grain is deactivated, for a member the cache has never held: the activation read back from the hot tier, the tenant version read, the walk — which the membership index answers in two triples, so "the chain walked" is the index's work, and the 47 ms is what the walk costs without it |
| silo-working-set-gb | 12 GB | **1.53 GB** for the whole test process after 200 000 resource grains were made resident in 12 s; 0.65 GB over the 0.88 GB before, **3.2 KB per activation** — a floor, these are grains with no resource behind them | ○ | a tenth of the population, and the process holds three silos, the client and the gateway, so the number is an upper bound on one silo's share |
| grain-activation-churn-per-minute | 0 | **0**: 0 of the 200 000 collected over 60 s idle, 0 re-activated when 2 000 of them were touched again | ✔ | |
| terminal-stream-p99-ms | 80 ms | — | ○ | a thousand sessions are a thousand exec streams into a thousand cloud-shell pods, on a one-node k3s in Docker with a laptop's share of it; `CyberCloud.Providers.Terminal`'s conformance suite proves one session, and a hundred pods here would measure the laptop. The row needs the staging cluster |
| span-ingest-drops | 0 | — | ○ | there is no span-ingest path in this repository to drive — the collector and VictoriaMetrics are bundle components nothing in a silo feeds; the row needs the observability stack installed and an ingest client |

**What the numbers said:**

1. **The write p99 is a stall, not a slope.** In the first table's run the p95 was 48 ms,
   thirteen of the fifteen ten-second buckets had no request slower than 82 ms, and the p99 was
   one pause of 1.7 s at 84–86 s into the window, in which nothing was created and after which
   106 creates landed in a second. Four runs of the same code have measured 69.5 ms (no pause),
   7 631 ms (twenty-five seconds from 56 s at 7–30 creates/s, then 229 in one second), that
   637 ms, and the 45.7 ms in the table (no pause). Every pause sat inside the
   first reminder wave — the minute after the first creates, when the ~3 000 operations accepted in
   the first minute take their first pass against k3s while the writes continue — in a process that
   holds three silos, the client, the gateway and the driver, on a laptop the other suites share.
   Owed: the diagnosis, with a profiler and the silos out of process; the candidates are the k3s API
   server (one node, in Docker), a gen-2 collection in a 1.5 GB process, thread-pool starvation, and
   — named by the branch's review — the console again: `LoggingResourceChangedSink` writes one
   Serilog Information line per `PUT` inside the gateway's write path and the gateway's
   `appsettings.json` keeps `Default` at Information, so the 7 500 writes were 7 500 lines through
   the same stdout pipe finding 4 measured, at 50 a second rather than 1 000. The budget stands and
   the row is ✘ until the pause is explained or gone.
2. **The reconcile queue's floor is one reminder period deep.** At 50 creates/s the depth
   plateaued at 3 015, which is 50 × 60: an accepted create's first pass is the reminder's, a minute
   later — nothing drives a pass at acceptance, and `OperationGrain`'s remarks said an in-activation
   timer did until this branch corrected them. The queue did not grow unboundedly: it drained in 70 s
   once the writes stopped. The +9.1 items/min (+2.4 in the run after the review) is 0.3 % of the
   plateau per minute, the noise of a sampler that lists 3 000 items every ten seconds, and the
   row's budget of 0 encodes "does not grow"
   as a slope of exactly zero, which a sampler will not produce. Owed, in this order: a first pass at
   acceptance (the timer, or a one-shot reminder), which moves the plateau to near zero and a create's
   time-to-converged from about a minute to seconds; and a stated tolerance for the slope in the
   table above, so the row can be ✔ for the right reason instead of ✘ for a wrong one.
3. **A budget met by one millisecond on a shared laptop is not a margin, and a baseline from it
   is not a release.** 23.9 ms against 25 in the first table's run; the run before it, same code,
   was 4.3 ms with a maximum of 18 ms; the run after the review, 6.6 ms. What moved between the
   runs was the laptop's other tenants. The branch's review ran the first baseline against a
   fresh run of the same code and the trend rule blocked the release on a cold p99 that had moved
   +49.7 % — the cache-hit number, over 200 samples — while the write p99 that had been 637 ms
   passed at 53 ms. That is the rule firing on noise, as this finding predicted, and the file is
   provisional until a release stamps it (the paragraph above the table). The weekly number still
   needs a machine of its own.
4. **The console was the read p99 once.** The fourth run measured 74.8 ms with a maximum of
   1.1 s, and the gateway had been started without its content root — no `appsettings.json`, so
   ASP.NET Core's request logging wrote two Information lines per request to the same stdout the
   test host writes: 30 000 lines in 30 s. On its own `appsettings.json` (`"Microsoft": "Warning"`,
   which is what ships) the same scenario measured 4.3 ms. The suite now starts the gateway on
   `src/Hosts/CyberCloud.Gateway.Host` as its content root, and the remark beside it says why.
5. **The testing host's silos listen on nothing.** `TestClusterOptions.ConnectionTransport`
   defaults to in-memory: silos and the testing host's client talk through a hub in the process, no
   port is bound, and the gateway's connection to the primary's gateway port was refused on every
   attempt of the first two runs. The chaos topology now asks for `TcpSocket` — which is also what
   the deployment uses — and a cluster id of `cybercloud` rather than the testing host's `dev`, because
   `OrleansApplication.CreateClient` binds the ids from `CyberCloud:Cluster` before
   `UseLocalhostClustering` and refuses a cluster under any other. The chaos table above is the
   `./build.sh Chaos` that followed the switch; the transport moved row 7 from 1 dropped request in
   673 to 4 in 688 and nothing else.
6. **Orleans' per-silo grain statistics are the cluster's.** `GetSimpleGrainStatistics` returns
   one row per silo and documents it as that silo's count; in 10.2.2 every row carries the
   cluster-wide number, so the suite's first sum said 624 450 resource grains for a population of
   208 150. `GetRuntimeStatistics` (68 207 + 67 485 + 67 344) and `GetTotalActivationCount` (203 036)
   agree with each other and with the population; the suite now takes the rows' maximum, and the
   3.2 KB per activation above is the corrected figure — 1.1 KB was the tripled count's.
7. **Bulk grants write at 25 tuples a second.** The ReBAC graph — 1 000 members over a 5-deep
   chain — took 40 s to write through `ITupleStoreGrain.WriteAsync` one tuple at a time, which is
   the only way in (1 600 members, with the cold ones, took 59 s). At the row's own 10 000 members that is seven minutes of setup, and an
   enterprise directory sync is a bulk write nobody has asked the tuple store for yet.

8. **"Cold" has to be checked, not arranged.** The first version deactivated the check grain and
   called the next check cold; the grain's cache is persisted on the hot tier and came back with
   the activation, and every one of the 200 samples answered from it. The row now asserts
   `FromCache == false` on every sample and reports the triples visited — two per walk, because
   the membership index answers the five-deep chain without walking it, which is what the index is
   for; the walk without it is the 47 ms full-consistency aside. A cold number a test does not
   check to be cold is a warm number with a longer name.

**Owed, and where:** the full-scale weekly run against staging, with the four hosted rows at
10 000 tenants and 5 000 rps and the two unhosted ones at all; a suite mode that takes a base URL
and a token source, which `Build.Load` refuses a `--e2e-base-url` until (`weekly.yml`'s `load` job
runs this suite on the runner's Docker meanwhile); the first pass at acceptance `OperationGrain`
owes (finding 2), and the slope tolerance in the table; the write stall's diagnosis (finding 1); a
machine the weekly run does not share, and a release to stamp the baseline (finding 3).

## CI shape

| Workflow | Trigger | Duration |
|---|---|---|
| `pr.yml` | Every PR | ≤ 25 min — everything in the "Every PR" rows above, parallelised; `Test` in its `Fast` lane |
| `main.yml` | Merge | + the `Cluster` lane of `Test`, images, charts, SBOM, signatures, deploy to dev |
| `nightly.yml` | 02:00 | The full `Test` run, E2E, the bootstrap dry-run on kind, hostile BYO, chaos, security |
| `weekly.yml` | Sunday | Load, licence scan, dependency review, a restore drill |
| `release.yml` | Tag | Full gate, publish everything, staged rollout |

**25 minutes for a PR is a budget, not an observation.** It is enforced: a PR that pushes the pipeline
past it fails, and the fix is parallelism or moving a test to nightly — with a written reason. A
40-minute PR pipeline is how a team stops running tests locally and starts merging on hope.

⚠ **A job that needs a secret this repository does not have skips, and says so; it does not fail.**
Until 2026-09-18 `main`, `nightly` and `weekly` were red on every run because they named secrets and
targets that do not exist (#25), and a red job cannot get redder: a suite went red on Linux with #75
and nobody saw it for ten days, because the only place it ran was inside a workflow that was red
anyway. A job whose secrets are all absent now has a step named
`skipped: <SECRET> is not configured — docs/plan/23 § CI secrets` in its step list, a notice on the
run page, and a green tick that means "the things that could run, ran". A job with *some* of its
secrets set still fails naming the rest — that is a half-configured job, not an unconfigured one
(`.github/scripts/gate-on-secrets.sh`). Jobs blocked on work rather than on a secret (`deploy-dev`,
`rollout`, `restore-drill`) carry a `::warning` annotation naming the work, every run, and fail only
when somebody creates the secret that nothing can yet consume. § CI secrets below is the list.

⚠ **What skips is the half that needs the secret, never the gate.** `release.yml`'s row above says
"full gate, publish everything", and `./build.sh Publish` is both at once — its dependency list *is*
the gate, and only the pushes need a credential. With no release secrets the job runs that list by
name, `./build.sh Test Generate Architecture Portal Licence --skip Images`, and the `skipped:` step
names the publishing alone; a tag on a repository with no release secrets has still run every suite,
every gate and the licence scan. The first draft of #25 skipped the whole target, which left a tag
validated by nothing the workflow ran; its review caught it.

⚠ **The secret scans read `verified,unknown`, and exempt four files by path.** `unknown` is a
credential-shaped string whose host the runner could not reach — the shape of every connection
string this platform has — so dropping it tree-wide to silence thirteen fixtures would have dropped
the one finding the repository is most likely to produce. `.github/trufflehog-exclude-paths.txt`
names the four test files that have to hold secret-shaped strings, and what each tests; it is the
only place a file is exempted from either scan, and adding a row to it is a review request.

## CI secrets

Configured under the repository's *Settings → Secrets and variables → Actions*, or on the `dev` and
`release` environments where a job names one. Who sets them: the repository owner — there is one
maintainer — and every row below is a *decision* before it is a credential. #25 named two of those
decisions outright: **where staging runs**, and whether the E2E lane is allowed anywhere near a
production cluster. The safe shape is a disposable k3s or a Lima VM rather than a shared environment;
an E2E suite that can reach production is one misconfigured base URL away from a bad day.

`CiSecretsReconciliationTests` in `src/CyberCloud.ResourceManager.Contracts.Tests` fails the build
when a workflow references a secret this table does not list, or the table lists one no workflow
reads, so the table and the workflows cannot drift apart.

| Secret | Unlocks | Who sets it, and what has to be decided first |
|---|---|---|
| `CONTAINER_REGISTRY` | `main.yml / images` — build, SBOM, cosign signature, push by digest ([18 § Platform security](18-security-vault-and-malware-scan.md)); `weekly.yml / licence`'s platform-image half; `nightly.yml / security-runtime`'s Trivy scan; `release.yml / publish` | The owner, once **which registry** is decided. `ghcr.io/<owner>/cybercloud` is the option that needs no new account; the value is the registry and repository prefix, e.g. `ghcr.io/acme/cybercloud` |
| `REGISTRY_USERNAME` | The same four jobs | A principal that can push there — for ghcr.io, a fine-grained token with `write:packages` |
| `REGISTRY_PASSWORD` | The same four jobs | Its password or token |
| `DEV_KUBECONFIG` | `main.yml / deploy-dev` | ⚠ **Not yet.** The job is blocked on work (table below); creating this turns a skip into a failure |
| `E2E_BASE_URL` | `nightly.yml / e2e`; `weekly.yml / load` | The owner, once **where staging runs** is decided — a disposable environment that cannot reach production. `Build.E2E.cs § E2EBaseUrl` has no fallback on purpose |
| `STAGING_KUBECONFIG` | `nightly.yml / e2e` (the bootstrap dry-run against staging) and `nightly.yml / chaos` | The same decision; for chaos, a cluster "that can lose a silo, a shard and a NATS node without anybody minding" |
| `STAGING_KUBE_CONTEXT` | Optional — the context inside `STAGING_KUBECONFIG`; defaults to its current-context | With the kubeconfig, when it holds more than one context |
| `ZAP_TARGET_URL` | `nightly.yml / security-runtime` — the ZAP baseline | Usually the same host as `E2E_BASE_URL` |
| `DOCKERHUB_USERNAME` `DOCKERHUB_TOKEN` | Optional — `weekly.yml / licence` spends an account's Docker Hub budget on manifest reads instead of the runner's shared anonymous one (`build/OciRegistry.cs`) | Only if the first no-registry `licence` run reports HTTP 429 on the eleven docker.io images |
| `NUGET_FEED` `NUGET_API_KEY` | `release.yml / publish` | The owner, once **the feed** is decided. `Build.Publish.cs` gives it no default because "a default feed is how a pre-release build ends up on nuget.org" |
| `CHART_REGISTRY` | `release.yml / publish` — packaged charts, `oci://…` | With the registry decision above |
| `PROD_KUBECONFIG` | `release.yml / rollout` | ⚠ **Not yet.** Blocked on work (table below) |

**Blocked on work, not on a secret.** These are green with a `::warning` on every run, and the warning
names the work. Creating the secret does not unblock them.

| Job | What is missing | Where it is argued |
|---|---|---|
| `main.yml / deploy-dev`, `release.yml / rollout` | A `Deploy` target in `build/`; `charts/platform`; for `rollout`, the canary's abort condition — error rate, p99 and grain-activation failures queryable per stage | `build/Build.cs § target graph`; `deploy/README.md § What an operator actually types`; [16](16-observability.md) |
| `nightly.yml / e2e`, and the Cluster e2e row | `test/CyberCloud.E2E`; a `cyc` under `cli/`; for the row, CAPI + Kamaji + KubeVirt on the VM lane | `build/Build.E2E.cs`; § The lane that needs a kubelet |
| `nightly.yml / chaos` | `test/CyberCloud.Chaos`; a way to verify the environment's size (invariant 7 needs 30 silos) | `build/Build.Chaos.cs` |
| `weekly.yml / load` | `test/CyberCloud.Load`; a committed baseline for the 20 %-regression rule | `build/Build.Load.cs` |
| `weekly.yml / restore-drill` | A backup mechanism for the durable tier; a written restore procedure; a scratch environment to restore into | [05 § The two tiers](05-state-and-storage.md); `deploy/README.md § Idempotence` |

### Action pins

Every `uses:` in `.github/` is a 40-hex commit SHA with the release it stands for written beside it
(`actions/checkout@3d3c42e5… # v7.0.1`). A tag is a pointer somebody else moves — the 2025
tj-actions/changed-files compromise repointed every version tag at a commit that read the runner's
secrets, and every workflow that trusted a tag ran it. [18 § Platform security](18-security-vault-and-malware-scan.md)
says "a pinned digest, never a tag" of images; this is the same rule for the code that builds them,
and `.github/scripts/assert-actions-pinned.sh` enforces it on every PR. The comment is what makes a
SHA reviewable: bump the SHA, bump the comment, and check the two agree on the action's release page,
which is the one thing a local check cannot do.

## Environments and rollout

| Env | Purpose | Data |
|---|---|---|
| `dev` | Every merge to main | Synthetic |
| `staging` | Release candidates, nightly suites, e2e | Synthetic + a mirrored anonymised subset |
| `prod` | — | Real |

Release: canary (1 silo, 1 gateway, 5 % of traffic) → 25 % → 100 %, with automatic rollback on error
rate, p99 or grain-activation-failure regression. Database changes are expand/migrate/contract across
three releases, never a coupled schema-and-code deploy.

⚠ **A rollback must be possible after the wire format changes**, which is why the compatibility gate
covers three releases: rolling back one release must not meet state written by a version whose
serializer the older code cannot read.

## What is deliberately not tested automatically

Written down so the gap is a decision:

| Not automated | Instead |
|---|---|
| Real payment flows | PSP test mode in CI; a quarterly manual run against real cards in a sandbox |
| Real email deliverability | Weekly manual send to seed accounts at the major providers, plus RBL monitoring ([17](17-communication-and-email.md)) |
| Physical network, BGP, anycast | Staged manually with the transit provider |
| Vault unseal-key recovery | ⚠ Quarterly human drill ([18](18-security-vault-and-malware-scan.md)). It cannot be automated because the whole point is that the keys are not on a machine |
| Support/impersonation workflows | Manual, with a checklist, because the controls are human ones |
