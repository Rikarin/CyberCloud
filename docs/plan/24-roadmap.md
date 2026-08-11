# 24 — Roadmap

Phases, exit criteria, and the cut list — decided in advance, because a cut list written under pressure
is a list of the things that were least defended rather than least important.

Effort is **EM** (engineer-months). Assume a team of 4–5. Plan against milestones, not dates.

---

## Phase 0 — Prerequisites · 1.5 EM

Small, unglamorous, and every one of them blocks something.

| Item | EM | Blocks |
|---|---|---|
| Repo, Nuke build, CPM, analyzers, `.editorconfig`, CI skeleton | 0.4 | Everything |
| Aspire AppHost: Redis, Postgres, NATS, k3s, hosts (ADR-014) | 0.3 | Local development |
| ⚠ **`Rikarin.Orleans.Streaming.NATs` → Orleans 10** | 0.4 | ADR-005 — currently on Orleans 9 |
| `CyberCloud.ServiceDefaults` — silo/client builders, health, OTel, Serilog | 0.2 | Every host |
| Bootstrap cluster stood up by hand from `deploy/bootstrap/` | 0.2 | Phase 1 |

**Exit:** `dotnet run` on the AppHost brings up a two-silo cluster with real Redis, Postgres, NATS and
k3s; a hello-world tenant-scoped grain round-trips through both storage tiers; CI is green.

---

## Phase 1 — The spine · 14 EM

Nothing tenant-facing ships. This is the phase people try to shorten and must not.

| Item | EM | Doc |
|---|---|---|
| Core: ids, `Result<T>`, `ResourceId`/`ResourceKey`, error registry | 0.8 | [06](06-tenancy-and-resource-model.md) |
| Tenancy: tenant/subscription/RG grains, the global directory, the shard map | 1.5 | [05](05-state-and-storage.md), [06](06-tenancy-and-resource-model.md) |
| Storage tiers: multitenant Redis + sharded Postgres, PgBouncer, hash tags | 1.2 | [05](05-state-and-storage.md) |
| ReBAC: schema builder, tuple grains, `Check`, cache, consistency tokens, role-assignment view | 2.1 | [07](07-rebac-authorization.md) |
| Resource manager: registry, versions, write path, LROs, reconcile scheduler, drift | 6.0 | [08](08-resource-manager.md) |
| Kubernetes fabric: connections, command builder, SSA, informers, k3s tests | 2.0 | [09](09-kubernetes-fabric.md) |
| Conformance suite + isolation suite + architecture gates | 0.4 | [23](23-build-ci-and-testing.md) |

**Exit criteria — all of them, no partial credit:**

1. A **trivial provider** (`CyberCloud.Sample/widgets`, a ConfigMap) passes the full conformance suite.
2. The isolation suite finds nothing.
3. Kill a silo mid-provision; the resource converges.
4. `Build.Generate` produces an OpenAPI document, CLI verbs, an SDK client and a portal form for the
   sample provider, and drift fails the build.
5. Two tenants on two shards; no shared write path on a tenant-rate path (architecture gate green).

⚠ **If phase 1 slips, everything slips, and no amount of provider work compensates.** Twenty providers
built on an unfinished manager is twenty copies of the manager's missing half.

---

## Phase 2 — M1: a tenant can log in and run something · 26 EM

| Item | EM | Doc |
|---|---|---|
| Identity: OpenIddict, users/groups/apps/SPs, passkeys, TOTP, sign-up flow, sessions | 4.8 | [11](11-identity.md) |
| Managed identity + token exchange | 1.2 | [11](11-identity.md) |
| Gateway: pipeline, auth, rate limits, region proxy, SignalR hubs, LRO endpoints | 4.2 | [10](10-gateway-and-api.md) |
| **Managed Kubernetes** (CAPI + Kamaji + KubeVirt) + node pools + credentials | 4.0 | [09](09-kubernetes-fabric.md), [13](13-compute-vm-containers.md) |
| Vault (OpenBao) | 2.0 | [18](18-security-vault-and-malware-scan.md) |
| Postgres · Valkey · NATS providers | 3.0 | [12](12-managed-data-services.md) |
| Container registry (Harbor) | 1.5 | [13](13-compute-vm-containers.md) |
| Network: VPC, subnets, security groups, public IPs, DNS, L4 LB, WireGuard | 6.3 | [14](14-networking.md) |
| Object storage (SeaweedFS, S3) | 2.0 | [15](15-storage-blob-file.md) |
| Monitor workspaces + ingest + platform self-monitoring | 2.5 | [16](16-observability.md) |
| Metering + quota (no invoicing) | 1.8 | [22](22-billing-metering-and-quota.md) |
| Cloud terminal | 1.5 | [19](19-cloud-terminal-and-virtual-desktop.md) |
| Portal M1 subset | 5.0 | [20](20-portal.md) |
| `cyc` + .NET SDK + TypeScript packaging | 3.2 | [21](21-cli-and-sdks.md) |
| Platform hardening: supply chain, admission, isolation, log canary | 1.0 | [18](18-security-vault-and-malware-scan.md) |

*(Sums to ~44; ~18 of it runs in parallel with phase 1's tail and with itself. The 26 is the critical
path, not the total.)*

**Exit — the M1 story, end to end, by a design partner with no help from us:**

> Sign up with a passkey → tenant and subscription created → create an in-house Kubernetes cluster
> (~8 min, with visible steps) → create a VPC and a Postgres server in it → get the connection string
> from Vault → open the cloud terminal and `psql` into it using a managed identity → see metrics and
> logs → invite a colleague and grant them Reader on one resource group → do all of it again from
> `cyc` → see the usage accruing.

Plus: three design-partner tenants running for four weeks with no cross-tenant incident; the chaos
invariants green; the load suite meeting the [00](00-vision-and-principles.md) budgets at 10 % of
target scale.

---

## Phase 3 — M2: a catalogue that is a business · 28 EM

| Group | Items | EM |
|---|---|---|
| Data | FerretDB, RabbitMQ, Kafka, ClickHouse | 4.4 |
| Compute | VMs + disks + images, scale sets, container instances | 3.8 |
| Registry | NuGet/npm/Maven feeds | 1.5 |
| Storage | File shares, backup vaults, customer-managed keys | 3.0 |
| Network | Application gateway + WAF, NAT, peering, flow logs | 3.6 |
| Observability | App Insights views, OTel collector service, managed Grafana, alerts | 3.0 |
| Communication | Channels, templates, suppression, delivery receipts | 2.0 |
| **Mail** | Postfix/Dovecot/Rspamd, domains, mailboxes, deliverability, minimal webmail | 3.5 |
| Security | Malware scanning | 1.5 |
| Fabric | ⚠ Agent-initiated cluster connections (BYO behind NAT) | 1.5 |
| ReBAC | `ListObjects`, Leopard index | 2.2 |
| Billing | Rating, invoicing, PSP, tax service, cost views, budgets | 3.6 |
| Platform | Management groups, deployments (templates), shard pinning | 1.5 |
| SDKs | Python, Go | 1.0 |
| Portal | Cost analysis, metrics explorer, log search, identity admin | 2.3 |

**Exit:** 28 resource types; paying customers on self-serve billing; a BYO on-prem cluster in
production behind NAT; the first managed mail domain sending with a clean reputation for 30 days;
median time-to-add-a-managed-service measured and ≤ 2 engineer-weeks.

---

## Phase 4 — M3: depth · 20 EM

Policy engine · private endpoints · conditional access · JIT roles · MariaDB · OpenSearch · Qdrant ·
Container Apps · virtual desktops · CDN/http-cache · resource graph API · security posture · GPU pools
and fractional sharing · placement policies · region migration · Terraform provider · full webmail.

**Exit:** compliance-shaped customers can adopt (private endpoints, policy, posture, residency);
multi-region is real for at least two regions; the Terraform provider is published.

---

## Running total

| Phase | EM (critical path) | Cumulative |
|---|---|---|
| 0 — Prerequisites | 1.5 | 1.5 |
| 1 — Spine | 14 | 15.5 |
| 2 — M1 | 26 | 41.5 |
| 3 — M2 | 28 | 69.5 |
| 4 — M3 | 20 | 89.5 |

**~90 EM to M3.** At 4–5 engineers that is roughly 18–20 months, and the estimate is load-bearing on
two assumptions, both stated so they can be checked early: that the operator selections in
[12](12-managed-data-services.md) hold up without a fork, and that phase 1 actually delivers the
generation pipeline. If either fails, the number is 30 % worse and the cut list below is how that is
absorbed.

## The cut list, in cutting order

Written now, so that under pressure the decision is a lookup rather than an argument.

| # | Cut | Costs | Saves |
|---|---|---|---|
| 1 | Virtual desktops | A me-too feature nobody has asked for | 2.0 |
| 2 | Container Apps | ⊂ Kubernetes; the tenant can install Knative | 2.5 |
| 3 | Full webmail (keep IMAP + minimal client) | Polish, not capability | 2.0 |
| 4 | CDN / http-cache | Honest anyway — we have no PoPs | 1.0 |
| 5 | MariaDB, OpenSearch, Qdrant | Catalogue breadth | 2.4 |
| 6 | Terraform provider | Enterprise adoption friction. ⚠ Cutting this hurts more than it looks | 1.5 |
| 7 | **The whole Mail module** | A differentiator, and an abuse-desk commitment ([17](17-communication-and-email.md)) we may not want | 3.5 |
| 8 | Security posture | A nice-to-have score | 1.5 |
| 9 | Region migration | One region until someone pays for two | 1.0 |
| 10 | Kafka | Strimzi is the heaviest to operate; NATS covers most needs | 1.2 |

**Never cut, at any pressure:** the resource manager, ReBAC, the isolation suite, the storage-tier
discipline, the label discipline, the metering *events*, the wire-compatibility gate, the conformance
suite. Every one of them is cheap now and unaffordable to retrofit — which is precisely the property
that makes something look like a good cut under deadline pressure and be the wrong one.

## The three decisions that must be made before phase 2 starts

Each is a business decision with an engineering consequence, and each blocks work if it is left open.

1. **Do we run public authoritative DNS ourselves, or front a wholesale provider?**
   ([14](14-networking.md)) — decides whether anycast nameservers are in the infrastructure plan.
2. **Are we prepared to staff an abuse desk?** ([17](17-communication-and-email.md)) — decides whether
   the Mail module is built at all. Building it without staffing it is the worst outcome.
3. **LINBIT support contract for LINSTOR/DRBD?** (ADR-011) — decides whether customer data goes onto
   DRBD in M1 or whether M1 uses a simpler storage class.
