# 23 — Build, CI and Testing

## Build

**Nuke** (`build/`), one entry point for every action, same as Survival and Vixen. `./build.sh <Target>`
locally and in CI, so "works on my machine" and "works in CI" are the same code path.

| Target | Does |
|---|---|
| `Restore` `Compile` | .NET, with CPM and deterministic builds |
| `Generate` | Provider registry → OpenAPI → CLI verbs → SDK → portal forms (ADR-012). **Fails on drift** |
| `Test` | Unit + grain tests, coverage floor per project |
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
| **Reconciler** | `k3s` in Testcontainers, real API server, real SSA | Every PR, < 15 min | All pass |
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

#### The first run — 2026-09-18, `test/CyberCloud.Chaos` on this laptop

The suite induces each fault against a topology it starts in Docker: a Redis hot tier that is also
the reminder table, three PostgreSQL shards (`durable-00`, `durable-01`, and `platform-00` for the
null-tenant grains — the shard map, the tenant directory, the cluster connections), a k3s, and three
silos wired through the same extension methods `CyberCloud.Silo.Host` composes, with
`CyberCloud.Providers.Sample` as the type under storm and the write path composed on the client as
the gateway composes it. Each test writes a `Held` / `Violated` / `Vacuous` row with its numbers and
`./build.sh Chaos` prints ✔ ○ ✘ from the file; a row the suite did not write is ✘, because "the
nightly is green because the file was empty" is the failure the target used to be blocked to avoid.
Two deployment knobs are turned and named in the results file: the cluster health window at 20 s
with a 5 s ping ([09](09-kubernetes-fabric.md)'s 90 s is a deployment number, not a property of the
code) and the Npgsql pool at 20 per shard. ⚠ The membership probes are the shipped defaults, after
a tuned 2 s / 2 misses / 1 vote declared a silo dead while it was still starting on this shared
laptop and Orleans' fatal-error handler took the whole test process with it. The table is one
`./build.sh Chaos`, 03:48–03:58, over real sockets (the load section's finding 5 says why that has
to be said); the six runs before it, over the testing host's in-memory transport, held and violated
the same rows and are where findings 1 and 4 were first seen.

| # | Fault, as induced | Held? | Measured |
|---|---|---|---|
| 1 | 24 creates in three waves across two tenants on two shards; a secondary silo killed under the second and third wave while the write path was mid-saga and the first wave's operations mid-pass; two replacements started; every operation driven to terminal; every member of both groups swept through the real listing. ⚠ 90 s compressed to the length of a wave — the kill is what the row is about, not the interval | ✔ | 2 kills holding 218 activations between them; 16 write calls and 16 drive calls threw; 8 names were left claimed by a write whose silo died between the claim and the operation start, and their retried `PUT`s were accepted up to 273 s later — the five-minute lease of [06 § Two-phase create](06-tenancy-and-resource-model.md) measured from the retry's first attempt; settled in 338 s with 24 Succeeded, 0 Failed, 0 stuck, 0 orphans, **0 duplicated names**, 24/24 ConfigMaps in k3s |
| 2 | Eight converged widgets and one acknowledged-but-undriven create; `FLUSHALL` on the Redis; every idle activation collected so the reads that follow come out of PostgreSQL | ✔ | 41 keys flushed, and **4 → 0 reminder rows with them** (the reminder table is in the same Redis); 8/8 durable widgets intact with their rows in the shard; the in-flight create converged; a fresh write was accepted 2.1 s after the flush and converged at 2.1 s against the 60 s budget |
| 3 | `durable-00` stopped for 27 s while a tenant on each shard writes; started again on the same port | ✔ | bystander on `durable-01`: 12/12 writes accepted; affected tenant: 0 accepted, 12 refused, slowest refusal 2.1 s, and **12/12 reads of its existing state answered from memory**; writes resumed 0.0 s after the shard came back; 0/8 seeded widgets lost |
| 4 | k3s stopped with a create and an update in flight; both driven every 3 s; k3s started again on the same port; then the first widget's ConfigMap deleted with the raw client and the widget updated | ✔ | 3 passes came back `retrying` (a transport failure, retryable) before the 20 s window closed and the connection went Degraded at 21.4 s; 3 passes after it were suspended with [09](09-kubernetes-fabric.md)'s "Cannot reach your cluster" sentence; **0 operations failed**; the API server answered 9.4 s after the container started and the first unsuspended pass ran at 9.4 s; both operations converged within 9 s of it, the second widget present and the first carrying its new message; the deleted ConfigMap was put back by the next update. ⚠ **6/6 reads of the resources during the blackhole showed `Failed`** — see finding 3 |
| 5 | `platform-00` stopped for 62 s, every activation collected, eight rounds of reads, scope reads and writes for two existing tenants, one new-tenant creation through `IScopeManager.CreateTenantAsync`. ⚠ 10 min compressed to 62 s — the directory snapshot has no TTL ([05 § The tenant directory](05-state-and-storage.md)), so the length of the outage is not the variable | ✘ | 32 reads and 16 writes: **0 tenant-facing errors**; 0/16 accepted writes converged while the directory was gone (the cluster connection is a null-tenant grain on that shard, so the data plane pauses) and 0 were lost after restore; a new tenant was possible 0.0 s after the shard came back. **New tenant creation answered in 2.1 s with `CodecNotFoundException: Could not find a codec for type Npgsql.NpgsqlException`** — an exception, not a `Result`, and one about the serializer rather than about the directory. Neither clean nor retryable |
| 6 | Nothing — no silo speaks NATS | ○ | `Microsoft.Orleans.Streaming.NATS` is a prerelease no project references; the AppHost's `nats` container has no client; the metering consumer of [22](22-billing-metering-and-quota.md) is not built. Nothing to partition, nothing to dedup |
| 7 | Every secondary silo stopped gracefully and replaced on a new port, one at a time, under four readers and a writer creating fresh widgets | ✘ | 688 requests over 10 s at 66 rps, **4 failed** — `SiloUnavailableException: Silo unavailable`, requests that landed on a silo as it left — 31 creates accepted and every one converged; graceful stop 0.1 s, replacement up in 0.2 s. Over the in-memory transport the same test saw 1 of 673 and, once, 0 of 689. Three silos and one version, not thirty and two: the row is ○ on a zero and ✘ on anything else, and this one is anything else |

**What the numbers said, in order of what it cost:**

1. **The two-phase create had a fourth failure window, and it made ghosts.** The first storm left
   two names claimed by a write whose silo died between starting the operation and confirming the
   claim; the reminder drove both to `Succeeded`, the reaper — which sweeps `Creating` members —
   never saw them, the leases expired at 301 s, the retried `PUT`s created two more, and the sweep
   found 26 `Succeeded` members and 26 ConfigMaps for 24 creates. Fixed on this branch:
   `OperationGrain.ConfirmClaimAsync` finishes step 3 on the operation's first pass and cancels the
   create if the claim is gone; [06 § Two-phase create](06-tenancy-and-resource-model.md) carries the
   corrected story and `ClaimConfirmedByTheOperationTests` pins it. The sweep now asserts no two
   members share a name.
2. **A shard that is gone surfaces to callers as a serializer error.** In rows 3 and 5 every
   refusal was `CodecNotFoundException: Could not find a codec for type Npgsql.NpgsqlException` —
   the grain's activation threw Npgsql's exception, Orleans could not put it on the wire, and the
   caller got an exception about the codec. The gateway would answer 500 with no detail. Row 5's
   second clause is violated by exactly this: the failure is not a `Result`, carries no
   `ErrorCode`, and says nothing a client could retry on. Owed: a closed-set error code for "the
   store this needs is unreachable" (there is none — `ErrorCode.All` has no 503), and the storage
   failure caught where the platform grains activate so the scope manager returns it.
3. **A resource whose cluster is unreachable reads `Failed`, and stays `Failed` while suspended.**
   Row 4: every read of both resources during the blackhole answered `ProvisioningState.Failed`,
   because a retryable reconcile failure records itself on the resource
   (`ResourceGrain.CompleteAsync`) and a suspended pass changes nothing afterwards. The operation
   was `Running` and said "suspended, not failed" throughout — the resource said the opposite.
   [09 § Connection health](09-kubernetes-fabric.md) has the portal say "cannot reach your
   cluster" instead of "provisioning failed"; a portal reading the resource's state would say the
   second. Owed: a transitional state or a flag on the resource for "retrying" that is not
   `Failed`, or a read path that consults the operation before reporting `Failed`.
4. **A rolling restart drops a request now and then, and the gateway does not retry it.** Row 7:
   4 of 688 requests met `SiloUnavailableException` as their silo left the cluster — 1 of 673 and
   0 of 689 over the in-memory transport, where a closing connection is cheaper than a closing
   socket. Orleans surfaces the shutdown to the caller rather than resending, and the
   gateway maps an unhandled exception to a 500 — so a tenant sees a rolling upgrade. Owed: a retry
   of `SiloUnavailableException` on the gateway's grain calls (every request type the gateway makes
   is idempotent by design — `PUT`, and reads), or Orleans' own `ResendOnTimeout` shape for it.
5. **The reminder table lives in the hot tier's Redis and a `FLUSHALL` empties it.** Row 2 held
   because the in-flight operation's next pass re-registers its reminder and the reminder service's
   local copy keeps ticking until its table refresh — but 3 → 0 rows is a fact about the design
   `SiloComposition.ConfigureStorage` chose, and [25 § R2](25-risks-and-open-questions.md) now
   carries it as a decision owed.
6. **The dead-write lease is five minutes of tenant-visible 409.** Row 1's eight orphaned claims
   were freed by the lease and not by anything sooner, which is [06](06-tenancy-and-resource-model.md)'s lease working as written;
   whether five minutes is the right number for a name a tenant is retrying is a product question the
   run makes concrete.
7. **A tenant's reads survive its shard.** Row 3: 12/12 reads of the affected tenant's existing
   widgets answered while its shard was stopped, from activations already in memory. Reassuring,
   and not a property anything guarantees — a collected activation would have read from the shard
   and failed.
8. **The testing host's kill is not a SIGKILL.** Rows 1 and 7 report the cluster noticing a death in
   0.0 s because Orleans' `KillSiloAsync` still writes the dying silo's Dead row; a pod that is
   SIGKILLed does not, and detection then takes the membership probes — about a minute at the
   shipped defaults. What happens after the death is known is the same either way, and that is what
   the rows assert.

**Owed, and where:** findings 2, 3 and 4 above — in `CyberCloud.Core`'s error codes and
`ScopeManagerService`, in `ResourceGrain`/the read path, and in the gateway's dispatch — each of
which keeps a row ✘ until it lands; the staging run of all seven at the doc's own scale — 30 silos,
two versions, a real NATS — which is nightly's and does not exist, and needs a suite mode that
attaches to a deployment rather than starting one (`Build.Chaos` refuses a `--kube-context` until
then rather than dropping it). The compressions above are stated per row; none changes what the row
asserts.

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

#### The first run — 2026-09-18, at 10 % of the rates above, on this laptop

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
time; `load-baseline.json` at the repository root is this run, so the 20 % rule has a first release
to compare the next one against. The numbers are the sixth run of the suite; the five before it are
findings 3 and 5.

| Metric | Budget | Measured (p50 / p95 / p99) | Status | How |
|---|---|---|---|---|
| control-plane-read-p99-ms | 25 ms | 1.6 / 10.0 / **23.9 ms**, max 86 ms; 15 001 requests, 0 errors, at 500 of 500 rps for 30 s after 10 s of warm-up; slowest request per 10 s: 29, 86, 28 ms | ✔ | `GET` of a converged widget, round-robin over the 20 subscriptions and 80 callers: bearer validation, the subject and subscription rate limits, the ReBAC check, the grain read |
| control-plane-write-p99-ms | 60 ms | 25.8 / 48.1 / **636.7 ms**, max 1 724 ms; 7 500 requests, 0 errors, at 50 of 50 writes/s for 150 s after 5 s of warm-up; slowest per 10 s: 62 52 55 56 49 57 59 **1724 1626** 420 51 81 55 69 67 | ✘ | `PUT` of a new widget — the two-phase create of [06](06-tenancy-and-resource-model.md) to its 202 — across the 20 subscriptions on both shards |
| reconcile-queue-depth-slope-per-minute | 0 | **+9.1 items/min**, least-squares over the 7 samples from 60 s into the writes to their end; peak depth 3 015 of 7 750 accepted, at the end of the writes; drained to 0 in 70 s after they stopped; 0 of 7 750 ended `Failed` | ✘ | depth = accepted creates not yet terminal, counted every 10 s through the real listing |
| rebac-check-p99-warm-ms | 10 ms | 0.3 / 0.5 / **1.8 ms**, max 4.9 ms; 39 992 checks, 0 errors, at 2 000 of 2 000/s for 20 s | ✔ | `ICheckGrain.CheckAsync(read)` over a 5-deep group chain with 1 000 members, from the cluster client; the graph took 40 s to write |
| rebac-check-p99-cold-ms | 50 ms | 3.6 / 8.0 / **9.7 ms**, max 20 ms; 200 checks | ✔ | the first check after the check grain is deactivated, 200 times: the tuples re-read and the chain walked |
| silo-working-set-gb | 12 GB | **1.49 GB** for the whole test process after 200 000 resource grains were made resident in 18 s; 0.65 GB over the 0.84 GB before, **3.2 KB per activation** — a floor, these are grains with no resource behind them | ○ | a tenth of the population, and the process holds three silos, the client and the gateway, so the number is an upper bound on one silo's share |
| grain-activation-churn-per-minute | 0 | **0**: 0 of the 200 000 collected over 60 s idle, 0 re-activated when 2 000 of them were touched again | ✔ | |
| terminal-stream-p99-ms | 80 ms | — | ○ | a thousand sessions are a thousand exec streams into a thousand cloud-shell pods, on a one-node k3s in Docker with a laptop's share of it; `CyberCloud.Providers.Terminal`'s conformance suite proves one session, and a hundred pods here would measure the laptop. The row needs the staging cluster |
| span-ingest-drops | 0 | — | ○ | there is no span-ingest path in this repository to drive — the collector and VictoriaMetrics are bundle components nothing in a silo feeds; the row needs the observability stack installed and an ingest client |

**What the numbers said:**

1. **The write p99 is a stall, not a slope.** The p95 is 48 ms, thirteen of the fifteen
   ten-second buckets have no request slower than 82 ms, and the p99 is one pause of 1.7 s at
   84–86 s into the window, in which nothing was created and after which 106 creates landed in a
   second. Three runs of the same code measured 69.5 ms (no pause), 7 631 ms (twenty-five seconds
   from 56 s at 7–30 creates/s, then 229 in one second) and this 637 ms. Every pause sat inside the
   first reminder wave — the minute after the first creates, when the ~3 000 operations accepted in
   the first minute take their first pass against k3s while the writes continue — in a process that
   holds three silos, the client, the gateway and the driver, on a laptop the other suites share.
   Owed: the diagnosis, with a profiler and the silos out of process; the candidates are the k3s API
   server (one node, in Docker), a gen-2 collection in a 1.5 GB process, and thread-pool starvation.
   The budget stands and the row is ✘ until the pause is explained or gone.
2. **The reconcile queue's floor is one reminder period deep.** At 50 creates/s the depth
   plateaued at 3 015, which is 50 × 60: an accepted create's first pass is the reminder's, a minute
   later — nothing drives a pass at acceptance, and `OperationGrain`'s remarks said an in-activation
   timer did until this branch corrected them. The queue did not grow unboundedly: it drained in 70 s
   once the writes stopped. The +9.1 items/min is 0.3 % of the plateau per minute, the noise of a
   sampler that lists 3 000 items every ten seconds, and the row's budget of 0 encodes "does not grow"
   as a slope of exactly zero, which a sampler will not produce. Owed, in this order: a first pass at
   acceptance (the timer, or a one-shot reminder), which moves the plateau to near zero and a create's
   time-to-converged from about a minute to seconds; and a stated tolerance for the slope in the
   table above, so the row can be ✔ for the right reason instead of ✘ for a wrong one.
3. **A budget met by one millisecond on a shared laptop is not a margin.** 23.9 ms against 25;
   the run before it, same code, was 4.3 ms with a maximum of 18 ms, and the slowest ten seconds of
   this one carried an 86 ms request. What moved between the two runs was the laptop's other
   tenants. The weekly number needs a machine of its own, or the trend rule will fire on noise.
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
   the only way in. At the row's own 10 000 members that is seven minutes of setup, and an
   enterprise directory sync is a bulk write nobody has asked the tuple store for yet.

**Owed, and where:** the full-scale weekly run against staging, with the four hosted rows at
10 000 tenants and 5 000 rps and the two unhosted ones at all; a suite mode that takes a base URL
and a token source, which `Build.Load` refuses a `--e2e-base-url` until; the first pass at
acceptance `OperationGrain` owes (finding 2), and the slope tolerance in the table; the write stall's
diagnosis (finding 1); a machine the weekly run does not share (finding 3).

## CI shape

| Workflow | Trigger | Duration |
|---|---|---|
| `pr.yml` | Every PR | ≤ 25 min — everything in the "Every PR" rows above, parallelised |
| `main.yml` | Merge | + images, charts, SBOM, signatures, deploy to dev |
| `nightly.yml` | 02:00 | E2E, cluster e2e, hostile BYO, chaos, security |
| `weekly.yml` | Sunday | Load, licence scan, dependency review, a restore drill |
| `release.yml` | Tag | Full gate, publish everything, staged rollout |

**25 minutes for a PR is a budget, not an observation.** It is enforced: a PR that pushes the pipeline
past it fails, and the fix is parallelism or moving a test to nightly — with a written reason. A
40-minute PR pipeline is how a team stops running tests locally and starts merging on hope.

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
