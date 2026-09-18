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
| `E2E` `Chaos` `Load` | Against a real deployment |
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
| **Reconciler** | `k3s` in Testcontainers, real API server, real SSA — and, since 2026-09-15, a kubelet, so an operator `charts/bundle/install.sh` installs can run under test | Every PR, < 15 min | All pass; `Test` prints how many cluster-backed cases ran and fails a run in which a cluster-holding suite, beside a Docker endpoint, skipped at least as many cases as it ran or skipped any case for a named missing prerequisite (`Build.Test.cs § ReportClusterBackedCases`) |
| **Conformance** | The shared provider suite, per provider | Every PR touching a provider | 100 % — a provider that fails is not registered |
| **Isolation** | `CyberCloud.Isolation` — every provider, every verb, wrong tenant | Every PR | **Zero** findings |
| **Contract** | OpenAPI diff, SDK/CLI regeneration, wire round-trip | Every PR | No breaks |
| **Portal** | Jest + Angular TestBed; Playwright for critical journeys | Every PR | Journeys pass, budgets met |
| **E2E** | Playwright + `cyc` against a real staging deployment | Nightly + pre-release | Green before release |
| **Cluster e2e** | `kind` + CAPI + Kamaji + KubeVirt: create and destroy a real tenant cluster | Nightly, ~20 min | ⚠ The highest-value test in the suite — the one that catches operator drift |
| **Hostile BYO** | Old Kubernetes minor, restrictive PSA, no default storage class, a rejecting webhook | Nightly | The brief's core premise |
| **Chaos** | Silo kills, Redis `FLUSHALL`, shard failover, cluster blackhole, global-cluster blackhole, network partition | Nightly | Invariants below |
| **Load** | The [00](00-vision-and-principles.md) quality bar, at scale | Weekly + pre-release | Budgets met |
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

Nineteen suites in this repository hold a `rancher/k3s:v1.35.7-k3s1` in Docker — fifteen provider
`*.Cluster.Conformance` assemblies (one per family with a cluster; the PostgreSQL one was added on
2026-09-17, after its Docker-free half had promised a project that did not exist since the family
landed), `test/CyberCloud.Cluster.Conformance`, `test/CyberCloud.Bundle.Cluster.Conformance`,
`CyberCloud.Kubernetes.Tests` and `CyberCloud.AppHost.Tests` — and until 2026-09-15 none of them
could schedule a pod on the machine that wrote them. The reason was two
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

| Proven on k3s-in-Docker (2026-09-15, re-measured 2026-09-17) | Stays for real nodes — the VM lane |
|---|---|
| 19 of the bundle's 20 components installed and serving through `install.sh`; the four Cluster API controllers 1/1 after the `${VAR:=default}` substitution; KubeVirt and CDI `Deployed`; every `waitFor:` returning | **kube-ovn** — needs the `kube-ovn/role=master` node label, a CNI-less cluster, ADR-019 values and OVS kernel modules; refuses at template time here |
| Every one of the nineteen cluster-backed suites, run alone on 2026-09-17 with Docker, `helm` and `kubectl` present: 387 cases executed, 0 failed, 22 skipped (the full `./build.sh Test` run's own line, whose per-suite figures sum to it; an earlier draft said 385, a count from before `SkipConventionTests` was in the run, and the review of that commit did the sum. Four daemon-free cases have joined since: one in `CyberCloud.AppHost.Tests`, three in the bundle suite) — and every one of the 22 is the same honest skip, "created no PersistentVolumeClaim on a real cluster", made once per type whose storage belongs to an operator. Not one skip names the infrastructure. Three defects came out of running them: the initdb one in the next row; `CyberCloud.AppHost.Tests` failing a model-only test on the SeaweedFS identity file the running topology held open (`CyberCloudTopology.HoldsOtherContent`); and, in a full `./build.sh Test`, the reclaim assertion of `ClusterConformanceTests.ARealNamespaceHoldsWhatKubernetesPutsThereAndTheReclaimSeesIt` reached for the first time — a young k3s answers 503 for `metrics.k8s.io` and the test had always taken its refusing arm — and found asserting a leftover that cluster-scoped families never leave in a namespace | **LINSTOR/DRBD** — the replicated storage stage, a kernel module (`bundle.yaml` § owed, `the-replicated-stage-is-not-installed`) |
| **A managed database, started by the resource manager on an operator the bundle installed** — the M1 exit story's steps 4–5, `test/CyberCloud.Bundle.Cluster.Conformance § M1StoryOnAFreshCluster`: `install.sh` puts cert-manager, openebs-localpv and cloudnative-pg on a fresh k3s in one run; the real write path creates a `virtualNetworks`, a subnet under it and a `DBforPostgreSQL/servers`; CloudNativePG brings the primary pod to Running; `listKeys` hands out the operator's credential and `psql` connects with it. ⚠ The first such run found that no managed database had ever been able to start — the renderer named `bootstrap.initdb.secret`, which makes the operator *expect* the Secret rather than write it (`PostgresServers.ClusterJson`) | **KubeVirt guests** — Docker Desktop's VM lends no `/dev/kvm`, so a Machine here is software emulation; the Cluster e2e row of the table above is this lane's, and it is still nightly-and-unbuilt |
| The two Kube-OVN objects in that story are admitted against open-schema stubs and route nothing: the network half proves the write path crosses a provider boundary in one silo, not a packet | **kube-ovn's `Vpc` and `Subnet` doing anything** — the same row as the first, seen from the resource manager's side |
| A cgroup-v2 host is the *only* prerequisite for the `.Cluster.Conformance` lanes above. ⚠ The bundle suite needs two more — `helm` and `kubectl` on `PATH` — and without `helm` its three installing classes and the story *skip*, with the daemon-free companions keeping the run green; measured 2026-09-17, 16 passed and 3 skipped with no helm, 20 passed with it. `Build.Test.cs § ReportClusterBackedCases` now fails such a run when a Docker endpoint is present | |

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
