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
| **Reconciler** | `k3s` in Testcontainers, real API server, real SSA | Every PR, < 15 min | All pass |
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
