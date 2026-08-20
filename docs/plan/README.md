# Cyber Cloud — Implementation Plan

Cyber Cloud is an **Azure-shaped cloud platform**: a control plane, a resource model, an identity
system, a portal, a CLI and an SDK, provisioning managed services onto Kubernetes clusters — ours, or
the tenant's own. It is .NET 10 and Microsoft Orleans on the back, Angular 22 and
[xUI](https://xuijs.org) on the front.

This directory is the authoritative design record: what Cyber Cloud is meant to be, and why each
decision was taken. **Read 00–02 first.** After that, treat each file as the spec for its subsystem.

**These documents do not say what is built.** `../overview.md` will, once there is code, and it is
checked against the tree — so where it and a document here disagree, it says so and it wins. Keeping
the two apart is what lets a design record stay useful for its reasoning without also having to be a
status board.

## The four constraints everything is downstream of

1. **Millions of users → Orleans first, no single database bottleneck.** Clustering is Kubernetes,
   the grain directory is Orleans', grain state is sharded per tenant across Redis and Postgres,
   streams are NATS. The one global thing is a 200-byte-per-tenant directory that is read from
   process memory. [04](04-orleans-topology.md), [05](05-state-and-storage.md).
2. **Multi-tenant to the bone.** The tenant id is part of every grain key; cross-tenant calls throw by
   default. [06](06-tenancy-and-resource-model.md).
3. **The platform manages clusters it does not run.** BYO kubeconfig, BYO behind NAT, or in-house
   Kubernetes-in-Kubernetes. [09](09-kubernetes-fabric.md).
4. **Everything is a resource, everything is a provider**, and the portal form, the CLI verb, the SDK
   client and the OpenAPI document are all *generated* from one registry.
   [08](08-resource-manager.md), ADR-012.

## Documents

| # | Document | Scope |
|---|---|---|
| 00 | [Vision and Principles](00-vision-and-principles.md) | Non-negotiables, layer discipline, the quality bar, what Cyber Cloud is *not* |
| 01 | [The Azure Parity Catalogue](01-azure-parity-catalogue.md) | **The audit.** ~200 Azure products against what we build, defer or decline — with the reason on every declined row, and the scope total |
| 02 | [Technology Decisions](02-technology-decisions.md) | Every dependency, pinned version, and the twenty ADRs |
| 03 | [Repository Layout](03-repository-layout.md) | Folder tree, project graph, the provider template, the six assembly-graph rules |
| 04 | [Orleans Topology](04-orleans-topology.md) | The three clusters, grain taxonomy, placement, streams, reminders, failure and upgrade, sizing |
| 05 | [State and Storage](05-state-and-storage.md) | Every store and what its load scales with; the Hot/Durable split; sharding; the shard map; backup |
| 06 | [Tenancy and the Resource Model](06-tenancy-and-resource-model.md) | Tenant → subscription → resource group → resource; ids; two-phase create; platform admin; quota |
| 07 | [ReBAC Authorization](07-rebac-authorization.md) | Zanzibar-derived, in C#, over grains: schema, tuples, `Check`, the Leopard index, `ListObjects`, consistency tokens |
| 08 | [The Resource Manager](08-resource-manager.md) | The write path, the reconcile contract, long-running operations, the provider registry, api-versions, errors |
| 09 | [The Kubernetes Fabric](09-kubernetes-fabric.md) | Cluster connections, the type-state command builder, informers, Kubernetes-in-Kubernetes, the bootstrap |
| 10 | [Gateway and API](10-gateway-and-api.md) | REST, SignalR, rate limits, versioning, LROs over HTTP, what the gateway must never do |
| 11 | [Identity](11-identity.md) | The Entra analogue: OpenIddict, users, groups, passkeys, MFA, managed identity, sessions |
| 12 | [Managed Data Services](12-managed-data-services.md) | Postgres, Valkey, NATS, FerretDB, RabbitMQ, Kafka, ClickHouse, MariaDB, OpenSearch, Qdrant — and the pattern that makes each one two weeks |
| 13 | [Compute](13-compute-vm-containers.md) | Managed Kubernetes, VMs, scale sets, container instances, container registry, artifact feeds |
| 14 | [Networking](14-networking.md) | Kube-OVN VPCs, DNS, load balancers, WireGuard, application gateway, private endpoints, IPv6 |
| 15 | [Storage](15-storage-blob-file.md) | Object (S3, SeaweedFS), file, block, archive, backup-as-a-service |
| 16 | [Observability](16-observability.md) | VictoriaMetrics + ClickHouse, ingest, workspaces, OTel Collector as a service, managed Grafana |
| 17 | [Communication and Email](17-communication-and-email.md) | The sending API, and the managed mail server — topology, deliverability, and the abuse-desk decision |
| 18 | [Security](18-security-vault-and-malware-scan.md) | OpenBao vaults, multi-engine malware scanning, posture, platform hardening |
| 19 | [Cloud Terminal and Virtual Desktop](19-cloud-terminal-and-virtual-desktop.md) | The web console, its image, its pod, its idle economics; desktops over Guacamole |
| 20 | [The Portal](20-portal.md) | Angular 22 + xUI, generated forms, blades, live updates, SSR, the performance budget |
| 21 | [CLI and SDKs](21-cli-and-sdks.md) | `cyc`, the Azure-shaped .NET SDK, generation, other languages, the OpenAPI contract |
| 22 | [Metering, Billing and Quota](22-billing-metering-and-quota.md) | The usage pipeline, rating, invoicing, cost visibility, quota, abuse |
| 23 | [Build, CI and Testing](23-build-ci-and-testing.md) | Nuke, the architecture gates, the test layers, the chaos invariants, rollout |
| 24 | [Roadmap](24-roadmap.md) | Five phases, exit criteria, ~90 EM, and the cut list in cutting order |
| 25 | [Risks and Open Questions](25-risks-and-open-questions.md) | Ten ranked risks, ten open questions, and the four corrections to the brief |

## The load-bearing decisions, in one place

If you read nothing else:

- **ADR-001** — Orleans is the control plane; Kubernetes is a data plane. Desired state lives in
  grains, never in etcd. This is what separates us from Cozystack and it is the decision the whole
  plan rests on.
- **ADR-002** — the tenant id is in the grain key. Tenancy is an address, not a filter.
- **ADR-003** — two storage tiers, and Redis is not the system of record.
- **ADR-007** — ReBAC is written here, because our tuples are already sharded and the hard part of
  Zanzibar is the storage.
- **ADR-009** — Kamaji + KubeVirt over vcluster: the isolation boundary must be a VM.
- **ADR-011** — the licence audit. Valkey, OpenBao, FerretDB, OpenSearch. Four of the brief's modules
  were affected and would have been a product-blocking discovery.
- **ADR-012** — one provider registry generates the OpenAPI, the CLI, the SDK and the portal forms.
  This is the mechanism behind *two engineer-weeks per managed service*, and without it that number
  is fiction.
- **ADR-013** — server-side apply with mandatory labels, enforced by a type-state builder so the
  unlabelled case does not compile.

## References read

Read firsthand, at plan time, and cited where they changed a decision:

| Source | What it contributed |
|---|---|
| `~/Projects/Survival/Server` | The working Orleans + ABP + Aspire + Redis + Kubernetes-membership shape. `ServiceDefaults`, `OrleansApplication`, the `[Alias]` discipline |
| [`Orleans.Multitenant`](https://github.com/VincentH-Net/Orleans.Multitenant) | Tenant-in-key separation. The `{tenantId}|{key}` encoding was read in `Internal/Extensions.cs`, not assumed |
| [Cozystack](https://github.com/cozystack/cozystack) | The operator survey per managed service, the annotated-values → schema → form pipeline, the sizing vocabulary — and, by contrast, why Kubernetes-as-control-plane does not fit here |
| [Kamaji](https://kamaji.clastix.io) + Cluster API + KubeVirt | The in-house cluster stack, and the honest provisioning-time and DIY costs |
| [`Rikarin/MalwareMultiScan`](https://github.com/Rikarin/MalwareMultiScan) | The multi-backend scanner architecture. ⚠ 2021, .NET Core 3.1 — a design reference, not a dependency |
| `~/Projects/Rikarin/xui` ([xuijs.org](https://xuijs.org)) | 90 Angular components; the portal is built from them and extends them rather than around them |
| [Azure's product taxonomy](https://azure.microsoft.com/en-us/products/) | The parity table in [01](01-azure-parity-catalogue.md) |
| Google Zanzibar (and OpenFGA / SpiceDB as implementations) | The tuple model, userset rewrites, zookies, the Leopard index |
