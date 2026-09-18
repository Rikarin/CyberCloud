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
| **Reconciler** | `k3s` in Testcontainers, real API server, real SSA — the `*.Cluster.Conformance` suites | Every merge to main + nightly. ⚠ Not every PR — see below | All pass |
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
