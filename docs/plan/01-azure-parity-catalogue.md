# 01 — The Azure Parity Catalogue

This is the audit the brief asked for: **what Azure offers, and what Cyber Cloud does about each of
it.** It is the document that turns "an Azure-like cloud" from a slogan into a scope.

The Azure column is taken from Microsoft's own product taxonomy as published at
[azure.microsoft.com/products](https://azure.microsoft.com/en-us/products/) (read 2026-08-08 — the
taxonomy moves, the shape does not). Nineteen categories, ~200 products.

## How to read the verdict column

| Verdict | Meaning |
|---|---|
| **M1** | In the first shippable platform. A tenant can create it, and it is billed. |
| **M2** | Second wave. The control plane already supports it; the provider is not written. |
| **M3** | Named, scoped, scheduled after the catalogue is broad enough to be worth deepening. |
| **P1** | Post-1.0. Wanted, understood, not budgeted. |
| **✗** | **Declined**, with the reason on the row. Declining loudly is the point of this table — an unmarked gap becomes a promise. |
| **⊂** | Subsumed by another row; not a separate product here. |

Effort is in **EM** — engineer-months — and is for the *provider*, assuming the control plane exists.
Anything marked `+` carries platform work that is budgeted in [24 — Roadmap](24-roadmap.md) instead.

---

## A — The control plane itself

Azure's control plane is not on its own product page, which is exactly why it gets underestimated.
Everything below in this table is downstream of it.

| Azure | What it is | Cyber Cloud | Verdict | How |
|---|---|---|---|---|
| Azure Resource Manager | The resource ID grammar, providers, API versions, LROs, tags, locks, `PUT` semantics | `CyberCloud.ResourceManager` | **M1** | [08](08-resource-manager.md). Grains hold desired state; a reconcile scheduler drives providers |
| ARM templates / Bicep | Declarative deployment of a resource graph | `CyberCloud.Resources/deployments` | **M2** | A JSON template with `dependsOn`, evaluated into a DAG by a deployment grain. **No Bicep** — a second language is a compiler we would own |
| Management Groups | Subscription grouping for policy and RBAC | `CyberCloud.Management/managementGroups` | **M2** | A tree above subscriptions; ReBAC inherits down it |
| Subscriptions | Billing and quota boundary | `CyberCloud.Platform/subscriptions` | **M1** | [06](06-tenancy-and-resource-model.md) |
| Resource Groups | Lifecycle boundary | ⊂ subscriptions | **M1** | Same doc |
| Azure Policy | Deny/audit/modify rules over resource shape | `CyberCloud.Policy/policyDefinitions` | **M3** | JSON-Logic-shaped conditions over the resource body, evaluated in the write path before the provider is called |
| Azure RBAC | Role assignments at scope | `CyberCloud.Authorization` | **M1** | **Superseded by ReBAC** — Azure RBAC is a special case of it. [07](07-rebac-authorization.md) |
| Azure Blueprints | Retired by Microsoft in favour of templates | — | **✗** | Microsoft deprecated it; copying a deprecated design is a choice we can just not make |
| Azure Lighthouse | Cross-tenant delegated management for MSPs | `CyberCloud.Platform/delegations` | **P1** | Falls out of ReBAC almost free — a cross-tenant relation with an explicit `ICrossTenantAuthorizer` grant. Deferred because the *audit* story is the hard half |
| Azure Managed Applications | Publish a resource type someone else operates | `CyberCloud.Marketplace` | **P1** | The provider model already supports third-party providers; the missing half is isolation and billing split |
| Azure Advisor | Recommendations | — | **P1** | Needs a year of metering data before it says anything true |
| Azure Resource Graph | KQL over all your resources | `CyberCloud.ResourceManager` query API | **M3** | A per-tenant projection into ClickHouse fed by the resource change stream. Not KQL — SQL over a flattened table |
| Cost Management | Cost analysis, budgets, alerts | `CyberCloud.Billing` | **M2** | [22](22-billing-metering-and-quota.md) |
| Azure Monitor | Metrics, logs, alerts, action groups | `CyberCloud.Monitor` | **M1**+ | [16](16-observability.md) |
| Activity Log | Control-plane audit | ⊂ `CyberCloud.Monitor` | **M1** | Every resource-manager write emits an audit event; it is the same pipeline as metrics |
| Resource locks | `CanNotDelete` / `ReadOnly` | ⊂ ResourceManager | **M1** | Cheap, and it prevents the demo-day incident |
| Azure Migrate, Resource Mover, Site Recovery, Storage Mover, Data Box, Automanage, Update Manager, Chaos Studio, Automation, Lab Services | Migration and fleet-ops surface | — | **✗** | Each is a product, not a feature. They exist because Azure has customers with a decade of on-prem estate. We do not, and will not for years |

**Platform work implied by this section: ~9 EM.** It is the largest single item in the plan and it is
the one that must not be rushed, because every row in every table below assumes it.

---

## B — Identity

The brief calls this "basically Azure Entra", and that is the right target.

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Microsoft Entra ID — users, groups, service principals, app registrations, tokens | `CyberCloud.Identity` | **M1** | OpenIddict for OAuth2/OIDC; users, groups, service principals and managed identities as grains. [11](11-identity.md) | 4.0 |
| Sign-up, sign-in, password reset, email verification | `CyberCloud.Identity.Host` | **M1** | Server-rendered pages, cookie-authenticated, separate host from the API gateway so a session cookie never travels with an API token | ⊂ |
| MFA — TOTP, SMS, email | ⊂ Identity | **M1** | TOTP in-house (RFC 6238, ~200 lines); SMS/email via `CyberCloud.Communication` | ⊂ |
| MFA — WhatsApp | ⊂ Identity | **M2** | Meta Cloud API through `CyberCloud.Communication`. ⚠ Template pre-approval and per-country cost make this a business decision more than an engineering one | 0.3 |
| Passkeys / FIDO2 / WebAuthn | ⊂ Identity | **M1** | `Fido2.AspNet`. Passkeys are the *default* offered credential, not an add-on — a platform that starts in 2026 with a password-first sign-up is making an avoidable mistake | 0.7 |
| Conditional Access | `CyberCloud.Identity/conditionalAccessPolicies` | **M3** | Same evaluator as Azure Policy, different subject | 1.0 |
| Managed identities | `CyberCloud.ManagedIdentity/*` | **M2** | A workload in a tenant cluster gets a projected SA token; the gateway trusts the cluster's OIDC issuer and exchanges it. This is the mechanism that lets a tenant's app read a Vault secret without a stored credential | 1.2 |
| Privileged Identity Management (just-in-time roles) | `CyberCloud.Authorization` time-bounded relations | **M3** | A ReBAC tuple with an expiry is the whole feature | 0.5 |
| Entra External ID (B2C) | ⊂ Identity | **P1** | The tenancy model already supports it; the branding/customisation surface is the work |
| Entra Domain Services (managed LDAP/Kerberos) | — | **✗** | Serves Windows workloads we do not have |
| Entra Verified ID | — | **✗** | Interesting, unrelated to the product |
| Entra ID Governance, access reviews, entitlement management | — | **P1** | Enterprise-sales features; write them when an enterprise asks |

---

## C — Compute

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Virtual Machines, VM Scale Sets, Spot, Dedicated Host | `CyberCloud.Compute/virtualMachines`, `/scaleSets` | **M2** | KubeVirt `VirtualMachine` + CDI for disks. Scale sets are a replica count and a shared instancetype. [13](13-compute-vm-containers.md) | 3.0 |
| VM images / Image Builder / golden images | `CyberCloud.Compute/images` | **M2** | CDI `DataVolume` from an HTTP/registry source; a platform image catalogue plus tenant-private images | ⊂ |
| Azure Kubernetes Service | `CyberCloud.ContainerService/managedClusters` | **M1** | Cluster API + Kamaji control planes + KubeVirt workers. This is the *first* provider because it is what proves the fabric. [09](09-kubernetes-fabric.md) | 4.0+ |
| AKS node pools / autoscaling | ⊂ managedClusters | **M2** | `MachineDeployment` + cluster-autoscaler with the Kamaji provider | ⊂ |
| Azure Container Instances | `CyberCloud.ContainerInstance/containerGroups` | **M2** | A `Job`/`Pod` in the tenant's namespace with a lifetime. The cheapest real provider to write, and a good second one | 0.8 |
| Azure Container Apps | `CyberCloud.App/containerApps` | **M3** | Knative Serving, or a Deployment+HPA+Gateway triple. ⚠ Scale-to-zero and revision traffic-splitting are the actual product; without them this is just a Deployment with a UI | 2.5 |
| Azure Functions | `CyberCloud.Web/functionApps` | **P1** | Needs a runtime, a trigger fabric, a cold-start story and a language matrix. Real product, not a provider |
| App Service / Static Web Apps / Spring Apps | — | **✗** | ⊂ containerApps once that exists. A separate PaaS runtime is a second platform |
| Batch | — | **✗** | Volcano or Kueue on a tenant cluster does this and the tenant can install it |
| Azure Virtual Desktop | `CyberCloud.DesktopVirtualization/workspaces` | **M3** | Ubuntu + XFCE in a container, Apache Guacamole (RDP/VNC gateway) fronted by our own web client. [19](19-cloud-terminal-and-virtual-desktop.md) | 2.0 |
| Cloud Shell | `CyberCloud.Terminal/consoles` | **M1** | Explicitly in the brief. SignalR → grain → exec into a per-user pod with a 5 GB `$HOME` PVC. [19](19-cloud-terminal-and-virtual-desktop.md) | 1.5 |
| Azure VMware Solution, Nutanix, Quantum, Azure Local / Stack | — | **✗** | Partnerships and hardware programmes, not software we can write |

---

## D — Containers and registry

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Azure Container Registry | `CyberCloud.ContainerRegistry/registries` | **M1** | Harbor per tenant (Cozystack ships a Harbor chart). Harbor already does OCI, replication, retention, scanning hooks and robot accounts — writing a registry instead would be a year | 1.5 |
| ACR — vulnerability scanning | ⊂ registries | **M2** | Trivy through Harbor's scanner adapter, and the same verdict surface as [18 — Malware Scan](18-security-vault-and-malware-scan.md) |
| **NuGet / npm / Maven feeds** (Azure Artifacts) | ⊂ registries | **M2** | Harbor does OCI only. NuGet/npm are a separate proxy — ⚠ this is the row most likely to be underestimated: three protocols, three auth schemes, three upstream-proxy semantics | 1.5 |
| Azure Container Storage | ⊂ `CyberCloud.Storage` | **M2** | LINSTOR/DRBD storage classes exposed as a resource |
| Kubernetes Fleet Manager | **P1** | Multi-cluster placement. The fabric can already address N clusters; fleet *policy* is the missing part |
| Azure Red Hat OpenShift | — | **✗** | Someone else's distribution |

---

## E — Databases and data services

This is the catalogue the brief lists, and the section where Cozystack's operator survey is taken
wholesale. Details, versions and per-service topology in [12](12-managed-data-services.md).

| Azure | Cyber Cloud | Verdict | Operator / engine | EM |
|---|---|---|---|---|
| Azure Database for PostgreSQL | `CyberCloud.DBforPostgreSQL/servers` | **M1** | CloudNativePG | 1.2 |
| Azure Managed Redis | `CyberCloud.Cache/redis` | **M1** | Valkey via `spotahome/redis-operator`. ⚠ **Valkey, not Redis** — Redis's licence change in 2024 makes redistributing a *managed Redis* a commercial question; Valkey is BSD and is what the ecosystem moved to | 1.0 |
| Azure Cosmos DB for MongoDB | `CyberCloud.DocumentDB/accounts` | **M2** | ⚠ Two options and they are not equivalent: **FerretDB** (Apache-2.0, Postgres-backed, what Cozystack ships) is licence-clean but is a compatibility layer; **Percona Server for MongoDB** is real MongoDB under SSPL, which we cannot offer as a service. FerretDB, and say so on the product page | 1.2 |
| Azure Service Bus / Event Grid | `CyberCloud.Messaging/natsClusters` | **M1** | NATS + JetStream. We run it for ourselves anyway, which makes it the cheapest managed service in the catalogue | 0.8 |
| — (no Azure equivalent) | `CyberCloud.Messaging/rabbitmqClusters` | **M2** | RabbitMQ Cluster Operator | 0.8 |
| Event Hubs | `CyberCloud.Messaging/kafkaClusters` | **M2** | Strimzi | 1.2 |
| Azure Data Explorer / Synapse | `CyberCloud.Analytics/clickhouseClusters` | **M2** | Altinity ClickHouse operator. Also our own telemetry store, so it gets built either way | 1.2 |
| Azure Database for MySQL | `CyberCloud.DBforMySQL/servers` | **M3** | mariadb-operator | 0.8 |
| Azure AI Search | `CyberCloud.Search/services` | **M3** | OpenSearch operator | 1.0 |
| — | `CyberCloud.Search/vectorStores` | **M3** | Qdrant. Not an Azure row, but a 2026 catalogue without a vector store is dated on arrival | 0.6 |
| Azure SQL / SQL MI | — | **✗** | Licensing. SQL Server is not ours to offer |
| Azure Cosmos DB (native, multi-model, global) | — | **✗** | The globally-distributed multi-model database is a decade of work, not a provider |
| Azure Cache for Redis Enterprise, Managed Cassandra, HorizonDB | — | **✗** | Commercial engines |
| Data Factory, Databricks, Stream Analytics, Fabric, Purview, Power BI | — | **✗** | An entire second company |
| Table Storage / Queue Storage | ⊂ Storage | **M3** | Both fall out of the object-storage layer cheaply once it exists |

---

## F — Storage

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Blob Storage | `CyberCloud.Storage/accounts` + `/blobServices` | **M1** | SeaweedFS with the S3 gateway. ⚠ **The API we expose is S3**, not the Azure Blob API — every client library, CLI and tool in existence speaks S3, and inventing a second dialect buys nothing | 2.0 |
| Azure Files (SMB/NFS) | `CyberCloud.Storage/fileShares` | **M2** | SeaweedFS FUSE/NFS, or a `ReadWriteMany` LINSTOR volume exposed by an NFS server pod. ⚠ SMB is a separate, worse problem — NFS first, SMB only if asked | 1.2 |
| Disk Storage | `CyberCloud.Compute/disks` | **M2** | LINSTOR/DRBD PVC, attached to a KubeVirt VM | ⊂ VM |
| Archive tier | ⊂ accounts | **M3** | Lifecycle rules to a cold SeaweedFS volume set |
| Data Lake Storage Gen2 | — | **✗** | Hierarchical namespace on top of blobs — a real feature, and nobody has asked |
| NetApp Files, Elastic SAN, Managed Lustre, Container Storage, Storage Discovery/Actions | — | **✗** | Hardware and enterprise-storage products |
| Backup | `CyberCloud.RecoveryServices/vaults` | **M2** | Velero + `vsnap` volume snapshots. Per-resource backup policy is a control-plane feature; the execution is Velero | 1.5 |

---

## G — Networking

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Virtual Network, subnets, NSGs | `CyberCloud.Network/virtualNetworks` | **M1** | Kube-OVN VPC + subnets + security groups. Cozystack's `vpc` app is this exact shape. [14](14-networking.md) | 2.5 |
| Azure DNS (public + private zones) | `CyberCloud.Network/dnsZones` | **M1** | Authoritative: **CoreDNS or PowerDNS** with a grain-backed backend. ⚠ Public authoritative DNS means running anycast nameservers and being on the hook for their uptime — the *provider* is 1 EM, the *operations* are the cost | 1.5 |
| VPN Gateway (site-to-site, point-to-site) | `CyberCloud.Network/vpnGateways` | **M1** | **WireGuard** primary — explicitly in the brief. IPsec/IKEv2 via strongSwan for site-to-site with equipment that only speaks it. ⚠ Cozystack ships Outline/Shadowsocks, which is a *censorship-circumvention* tool, not a corporate VPN; do not copy that choice | 1.5 |
| Load Balancer (L4) | `CyberCloud.Network/loadBalancers` | **M1** | Cilium LB-IPAM + BGP for platform VIPs, Kube-OVN EIP for tenant-VPC addresses (ADR-019), or HAProxy for TCP with a public VIP | 0.8 |
| Application Gateway / Front Door / WAF | `CyberCloud.Network/applicationGateways` | **M2** | Envoy Gateway (Gateway API) + Coraza WAF. Front Door's *global* half is anycast and PoPs — declined as a topology, offered as a feature name only where it is honest | 2.0 |
| Private Link / Private Endpoint | `CyberCloud.Network/privateEndpoints` | **M3** | A per-tenant service in the consumer's VPC pointing at the producer's, brokered by the control plane. This is the feature that makes managed services usable by serious customers | 1.5 |
| NAT Gateway, Route Server, Virtual WAN, Virtual Network Manager, Traffic Manager | `CyberCloud.Network/*` | **M3/P1** | Kube-OVN gives us most of the primitives; each is a small provider over an existing capability |
| ExpressRoute | — | **✗** | Carrier interconnect. Physical |
| DDoS Protection | — | **✗** | Bought from the upstream transit provider; exposed as a flag, not built |
| CDN | `CyberCloud.Cdn/profiles` | **M3** | Cozystack's `http-cache` (nginx) at each PoP we have. Honest framing: this is a caching reverse proxy, and calling it a CDN before there are PoPs would be a lie |
| Bastion | ⊂ `CyberCloud.Terminal` | **M2** | The cloud terminal already exec's into a tenant network; bastion is the same machinery pointed at a VM |
| Network Watcher | ⊂ `CyberCloud.Monitor` | **M3** | Flow logs from Cilium/Hubble |

---

## H — Integration, messaging and communication

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Azure Communication Services — SMS, WhatsApp, email, chat, telephony | `CyberCloud.Communication/services` | **M2** | In the brief. A provider-abstraction over Twilio / Meta Cloud API / Vonage plus our own SMTP. ⚠ We are a *broker*, not a carrier — say so, because the alternative is regulatory work in every country | 2.0 |
| — (Azure has no managed mail server) | `CyberCloud.Mail/domains`, `/mailboxes` | **M2** | In the brief. Postfix + Dovecot (⚠ **not Cyrus** — see [17](17-communication-and-email.md) for why), per-tenant instance, own IP, own web UI | 3.5 |
| SignalR Service | ⊂ Gateway | **M1** | We run SignalR for the portal; exposing it as a tenant resource is a later, easy provider |
| Web PubSub | — | **P1** | ⊂ the SignalR resource when it exists |
| API Management | `CyberCloud.ApiManagement/services` | **P1** | Envoy Gateway + a policy layer. Wanted; large |
| Logic Apps / Event Grid | — | **✗** | Event Grid's job is done by NATS for our own purposes; a visual workflow engine is a product |
| Notification Hubs | ⊂ Communication | **M3** | APNs/FCM fan-out |

---

## I — Security

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Key Vault | `CyberCloud.KeyVault/vaults` | **M1** | OpenBao (the Apache-2.0 fork of Vault; ⚠ Vault itself is BUSL since 2023 and cannot be offered as a service). Per-tenant namespace, transit engine for envelope encryption, managed-identity auth. [18](18-security-vault-and-malware-scan.md) | 2.0 |
| Key Vault — managed HSM / Cloud HSM | — | **✗** | Hardware |
| Microsoft Defender for Cloud | `CyberCloud.Security/assessments` | **M3** | Trivy + kube-bench + our own resource-shape rules, surfaced as a score. A thin version of this is worth more than nothing |
| Microsoft Sentinel (SIEM) | — | **P1** | The audit and flow logs land in ClickHouse anyway; SIEM is the query layer and the content |
| — (Azure has no VirusTotal analogue) | `CyberCloud.Security/scanners` | **M2** | In the brief. ClamAV + YARA + a pluggable backend set, modelled on `Rikarin/MalwareMultiScan`. ⚠ That repo is a 2021 .NET Core 3.1 codebase — it is a *design reference*, not a dependency | 1.5 |
| Attestation, Information Protection, confidential compute | — | **✗** | Hardware and enterprise-content products |

---

## J — Observability

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Azure Monitor metrics + Log Analytics | `CyberCloud.Monitor/workspaces` | **M1** | VictoriaMetrics (metrics) + ClickHouse (logs and traces). ⚠ Loki is the Cozystack choice and is rejected: label-only indexing makes "find this request id across a tenant" the exact query it is bad at | 2.5 |
| Application Insights | ⊂ workspaces | **M2** | OTLP ingest, a per-tenant ClickHouse database, and the trace/exception views |
| **OTel Collector as a service** | `CyberCloud.Monitor/collectors` | **M2** | In the brief. A tenant-owned collector deployment with a validated pipeline config — this is the correct primitive and Azure has no equivalent | 1.0 |
| Managed Grafana | `CyberCloud.Dashboard/grafanas` | **M2** | grafana-operator, per-tenant instance, datasources pre-wired to that tenant's workspace and nothing else | 0.8 |
| Alerts + action groups | ⊂ workspaces | **M2** | vmalert → a notification grain → `CyberCloud.Communication` |
| Managed Prometheus | ⊂ workspaces | **M1** | VictoriaMetrics speaks the Prometheus remote-write and query APIs, so this is a compatibility claim rather than a component |

---

## K — Developer surfaces

| Azure | Cyber Cloud | Verdict | How | EM |
|---|---|---|---|---|
| Azure CLI (`az`) | `cc` | **M1** | In the brief. System.CommandLine, verbs generated from the provider registry. [21](21-cli-and-sdks.md) | 1.5 |
| Azure SDK for .NET | `CyberCloud.Sdk` | **M1** | In the brief. Azure.Core-shaped: `TokenCredential`, `Response<T>`, `Operation<T>`. Generated from OpenAPI | 1.5 |
| Azure Portal | `CyberCloud.Portal` | **M1** | Angular 22 + xUI + Tailwind, SSR, zoneless. Resource forms generated from provider schemas. [20](20-portal.md) | 5.0 |
| Azure DevOps / Pipelines / Repos / Artifacts | — | **✗** | Except artifact feeds — see § D. GitHub and Forgejo exist |
| Azure Arc | — | **P1** | "Manage a cluster you already run" is *the brief's own core feature*, so the Arc concept is built in rather than bolted on |
| Terraform / Pulumi provider | `terraform-provider-cybercloud` | **M3** | Generated from the same OpenAPI. Cheap once the surface is generated, and it is what a serious customer asks for second |

---

## L — Declined categories, entire

Named so nobody re-proposes them at month nine.

| Category | Why not |
|---|---|
| **AI + Machine Learning** (30 products) | Not a cloud-platform capability — it is a research organisation with a GPU fleet. What *is* in scope: GPU-attached VMs and node pools (⊂ Compute, **M3**, via the NVIDIA GPU operator and HAMI for fractional sharing, which is what Cozystack 1.4 added), and a vector store (§ E). Selling "AI" without models is selling GPUs, and we should say GPUs |
| **Internet of Things** (17 products) | A separate market with separate protocols. NATS + MQTT gateway covers the honest 5 % |
| **Mixed Reality, Media Services, Maps, Digital Twins** | Unrelated products |
| **Hybrid / Azure Local / Stack** | The premise is inverted: we *start* hybrid |
| **Migration** (6 products) | We have nothing to migrate from yet |
| **Analytics / Fabric / Power BI** | Second company |
| **Quantum, Sphere, Operator Nexus, Planetary Computer** | Not our business |

---

## Summary of scope

| Milestone | Providers | Provider EM | Platform EM | Total |
|---|---|---|---|---|
| **M1** — a tenant can log in, get a cluster, and run a database on it | 12 | 20 | 26 | **46** |
| **M2** — the catalogue is broad enough to be a business | +16 | 20 | 8 | **28** |
| **M3** — depth: policy, private link, search, desktops, CDN | +12 | 15 | 5 | **20** |
| **P1** — everything above marked P1 | — | — | — | *unbudgeted* |

**~94 EM to M3.** That is 4–5 engineers for about eighteen months, and it assumes the operator
choices in § E hold up and that nobody tries to write a database. [24 — Roadmap](24-roadmap.md) has
the sequencing, the exit criteria, and the cut list in the order it should be cut.

The number that actually matters is not 94. It is the **2 engineer-weeks per new managed service**
target in [00](00-vision-and-principles.md). If that number is right, the catalogue grows on its own
after M1 and the total is a starting point rather than a budget. If it is wrong — if the twelfth
provider still needs a change to `CyberCloud.ResourceManager` — then the control plane was not
finished and no amount of provider work will fix it.
