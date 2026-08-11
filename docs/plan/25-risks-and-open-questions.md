# 25 — Risks and Open Questions

Ranked by expected cost, not by likelihood. A risk that is cheap to be wrong about is not on this list
even if it is likely.

## R1 — The resource manager is under-built and providers absorb the difference

**Cost if realised: the whole schedule.** Every provider in [01](01-azure-parity-catalogue.md) is
costed at 0.6–4 EM *given a finished control plane*. If phase 1 exits early with the generation
pipeline half-done or the LRO model incomplete, each of twenty providers grows a private copy of the
missing part, and the 2-engineer-weeks-per-service number becomes 6.

**Leading indicator, and it is measurable from provider three onward:** the number of commits to
`CyberCloud.ResourceManager` made by a provider PR. Once it is consistently zero, the manager is done.
While it is not, it is not.

**Mitigation.** Phase 1's exit criteria are all-or-nothing ([24](24-roadmap.md)). The sample provider
is written *first* and deliberately trivial, so its friction is unmistakably the platform's.

## R2 — Redis as the default grain store loses data that mattered

**Cost: a data-loss incident and the trust that follows it.** The brief suggested Redis; ADR-003
qualifies it into two tiers. The risk is not the design, it is the discipline: one grain that should
have been Durable and was left on the default is one incident.

**Mitigation.** `durable-grains.txt` plus the architecture gate ([23](23-build-ci-and-testing.md)), and
the chaos suite's `FLUSHALL` run, which fails if anything durable was lost. **The gate is worth more
than the design** — the design is one paragraph and the gate is what makes it true in month fourteen.

⚠ **Open question:** should the durable tier be Postgres at all, or should it be a per-tenant
event journal in NATS JetStream with Redis as a pure projection? The journal design is more Orleans-native,
removes Postgres entirely, and makes rebuild-from-truth automatic. It is also much more code and puts
the durability of the whole platform on JetStream's file store. **Decision: Postgres, and revisit only
if the shard count exceeds 40.** Recorded because it will be re-proposed.

## R3 — Kamaji provisioning time and operational load are worse than the plan assumes

**Cost: 3–6 EM and a worse product.** The 6–9 minute figure in [09](09-kubernetes-fabric.md) is
derived from the components, not measured on our hardware. The flakiest step — first worker VM boot
and join — is exactly the one most sensitive to image size, network and storage latency. And the
comparison research is explicit that building a production platform around Kamaji is substantial DIY.

**Mitigation.** A **spike in phase 1**, not phase 2: stand up CAPI + Kamaji + KubeVirt on real
hardware, create and destroy 20 clusters, and measure. If the number is 20 minutes rather than 8, that
changes the portal design, the LRO timeouts and possibly the decision. This spike is the highest-value
0.3 EM in the plan.

**Fallback if it is bad:** offer only Connected clusters at M1 and sell managed Kubernetes later. That
loses a headline feature and loses nothing structural, because the fabric is the same either way.

## R4 — The operator selections require forks

**Cost: 0.5–1 EM per forked chart, forever.** [12](12-managed-data-services.md) takes Cozystack's
operator survey. Their charts are shaped for *their* control plane; ours applies objects directly
(ADR-013) rather than through `HelmRelease`, so a chart assuming Flux annotations or Cozystack's
namespace conventions needs changes. Multiply by twelve and it is a maintenance stream.

**Mitigation.** Charts are forked into `charts/managed/` with a `SOURCE` file naming the upstream
commit, and a scheduled job diffs upstream and opens a PR. **The rule that keeps this bounded: fork the
chart, never the operator.** A forked operator is a fork of a distributed system.

## R5 — ReBAC is slower than the budget at real graph shapes

**Cost: 2 EM and a latency regression on every request.** The p99 < 10 ms budget assumes the Leopard
index works and that real customer graphs are not pathological. Real enterprise group structures are
sometimes very deep and very wide.

**Mitigation.** The property-test corpus includes deliberately pathological graphs, and the load suite
measures at depth 5 / 10 000 members ([23](23-build-ci-and-testing.md)). The index is M2, so M1's small
tenants are a safe place to discover the shape.

⚠ **Open question: is negation restricted enough?** [07](07-rebac-authorization.md) confines `!` to the
top level over same-object relations, which keeps invalidation sane. If a real customer requirement
needs richer negation, the cache model changes and that is not a small change. **Decision: hold the
restriction until a paying customer's requirement breaks it**, and treat that as a design review, not
a patch.

## R6 — Multi-region is deferred and then urgently needed

**Cost: 4–6 EM, unplanned.** [04](04-orleans-topology.md) homes a tenant to one region and does not
solve active-active. That is right — the alternative is a globally consistent store, which is the
bottleneck the brief rules out. But the first enterprise customer with a DR requirement will ask, and
"we route to your home region" is not an answer to "what happens when that region is gone".

**Mitigation.** M3 has region migration, which is the honest 80 %. What is *not* budgeted is
per-tenant active-active, and it is named here so that a sales commitment to it is recognised as a
roadmap change rather than a configuration.

## R7 — Email deliverability and abuse

**Cost: the module, plus collateral damage.** [17](17-communication-and-email.md) is explicit: the
software is ~30 % of mail hosting. If our address blocks get listed because one tenant spammed and
nobody was watching, the platform's *own* transactional email fails too — OTPs, alerts, invoices.

**Mitigation.** The decision gate in [24 § three decisions](24-roadmap.md): staff the abuse desk or cut
the module. Also: separate the platform's own transactional sending IPs from tenant sending IPs, so a
tenant's reputation problem cannot take down our OTPs. That separation costs nothing and is easy to
forget.

## R8 — Orleans expertise is a hiring constraint

**Cost: schedule, and the quality of everything.** Orleans is not widely known. A team member who
writes a grain like a service — blocking on `.Result`, holding state in a field, calling a grain in a
loop — produces code that works in development and fails at scale in a way that is hard to attribute.

**Mitigation.** Analyzers catch the mechanical mistakes ([23](23-build-ci-and-testing.md)). The rest is
a written grain-authoring guide with the six anti-patterns, and a rule that a first grain PR is
reviewed by someone who has written twenty. The `~/Projects/Survival/Server` codebase is a working
reference and is in `references/` for that reason.

## R9 — The generated portal forms are bad enough that people build around them

**Cost: the ADR-012 economics.** If the generated form is unpleasant, teams write overrides, and once
there are 40 overrides the generation pipeline is dead weight and every new resource type costs a
frontend sprint.

**Mitigation.** The override count is a **tracked metric with a budget of 10**. Exceeding it triggers a
review of the *renderer*, not permission for an eleventh. And the renderer is built against the three
ugliest schemas first (Postgres, managed cluster, VM) rather than the prettiest.

## R10 — IPv4 scarcity

**Cost: real money and a product constraint.** Public IPs are a metered resource in
[14](14-networking.md) precisely because they are scarce and expensive. A design that hands one to
every managed service with external access enabled will exhaust an allocation faster than expected —
and [17](17-communication-and-email.md)'s dedicated outbound mail IPs compete for the same pool.

**Mitigation.** External access off by default ([12](12-managed-data-services.md)); shared ingress with
SNI/host routing wherever L7 will do; a v4 address as a quota'd, billed resource; IPv6 dual-stack from
day one ([14](14-networking.md)) so v6-capable customers do not consume v4 at all.

---

## Open questions needing a decision from you

Ordered by when they block something.

| # | Question | Blocks | Default if unanswered |
|---|---|---|---|
| 1 | ~~**Public DNS: run it or wholesale it?**~~ | — | ✅ **CLOSED 2026-08-11 — we run it.** ⚠ The engineering was never the question: [14 § DNS](14-networking.md) prices the provider at 1.5 EM and says "the operations are the cost". Running public authoritative DNS means **anycast nameservers, DDoS absorption, and being the reason a customer's whole business is offline when it breaks**. That cost lands in the infrastructure plan, not this one, and it is now a commitment rather than an option |
| 2 | ~~**Abuse desk: staffed?**~~ | — | ✅ **CLOSED 2026-08-11 — staffed. `CyberCloud.Mail` is IN for M2** (3.5 EM, [17](17-communication-and-email.md)). ⚠ This is an **operational** commitment before it is an engineering one: [17 § Deliverability](17-communication-and-email.md) requires `abuse@` monitored by a human **with the authority to suspend a tenant within the hour**. R7 below is explicit that building the module without staffing the desk is the one path that ends with our address blocks listed and the platform's *own* transactional email — OTPs, alerts, invoices — failing with it. Also now required and easy to forget: **separate the platform's own transactional sending IPs from tenant sending IPs**, so one tenant's reputation problem cannot take down our OTPs |
| 3 | **LINBIT contract for LINSTOR/DRBD?** (ADR-011) | Whether customer data lands on DRBD in M1 | Use a simpler storage class for M1 data services |
| 4 | **Cyrus or Dovecot?** ([17](17-communication-and-email.md)) | The mail module's shape | Dovecot, for the reasons in 17 — the seam is LMTP/IMAP either way |
| 5 | ~~**Do we depend on `Azure.Core` in our SDK?**~~ | — | ✅ **CLOSED 2026-08-11 — no. We take the shapes and own the code.** The default in this column was "yes"; asked directly, the answer was no, and the justification in [21](21-cli-and-sdks.md) did not survive examination — "existing `TokenCredential` implementations transfer directly" is false (Azure's credentials authenticate against Entra, not our identity server), and "the retry machinery for free" ignored that `Polly` was already in the register. The listed cost — "a dependency named Azure" — was the weakest one; the real costs are trim/AOT hostility and a transitive graph inside a CLI that must publish as one self-contained file. ⚠ It also resolved a contradiction nobody had noticed: [21](21-cli-and-sdks.md) requires `cyc` to be **AOT single-file** *and* the CLI to use the SDK, which `Azure.Core` would have made impossible |
| 6 | **How many regions at GA?** | The multi-region work in R6, and the DR story we can sell | One, plus region migration at M3 |
| 7 | **Windows Server images?** ([13](13-compute-vm-containers.md)) | The VM image catalogue | Linux only until a licensing arrangement exists |
| 8 | **Marketplace / third-party providers?** ([01](01-azure-parity-catalogue.md)) | Whether the provider model needs isolation and revenue split from the start | P1 — but the provider interface should not *preclude* it, and today it does not |
| 9 | **Is a tenant one region forever, or is data residency per-resource?** | The tenancy model's depth | Per tenant. Per-resource residency is a much larger model |
| 10 | ~~**`cc` or another CLI name?**~~ | — | ✅ **CLOSED — the CLI is `cyc`.** See § Corrections, item 5. The `cc` proposal is withdrawn: the ⚠ on this row was correct and understated. `cc` is not merely "a C compiler alias on some systems" — it is the POSIX-mandated name for the system C compiler, present at `/usr/bin/cc` on essentially every Linux and macOS host, and `CC` is the standard `make`/autotools variable for it, which would have made the planned `CC_*` env prefix ambiguous too. Shipping `cc` would have shadowed a build toolchain binary on 10 000 machines |
| 11 | **BGP peering available on the fabric, and does the design need FRR-grade route policy?** (ADR-019) | Whether MetalLB is installed at all | BGP available → Cilium LB-IPAM + BGP CP, no MetalLB. L2-only → MetalLB in L2 mode. FRR-grade policy or BFD needed → MetalLB + FRR-K8s **for BGP only**, alongside Cilium. **A network-team call** |

---

## Corrections to the original brief

Six, all settled, kept here because each changed what gets built.

1. **Redis alone is not a system of record.** ADR-003 splits storage into Hot and Durable, with a
   checked-in list and a build gate. Redis remains the default and the brief's instinct — fast,
   sharded, no single database — is honoured; what changed is that ~15 grain types are excluded from
   it by name. [05](05-state-and-storage.md).
2. **`Orleans.Multitenant` requires string grain keys, so "GUID as ID" needed reconciling.** GUIDs are
   the identifiers everywhere a user or an API sees one; the grain *key* is a composed string
   containing them, built only by `GrainKeys`. Verified against the library's source. ADR-002.
3. **SSH into the cloud terminal's container is the wrong transport.** The Kubernetes exec streaming
   API does everything SSH would, with no listener in the container, no key management and no network
   path. SSH stays relevant for reaching *VMs*, which is a separate, later feature.
   [19](19-cloud-terminal-and-virtual-desktop.md).
4. **Cyrus → Dovecot, and "separate IP per tenant" is only true for outbound.** Shared inbound MTA and
   IMAP front doors with per-tenant back ends; dedicated outbound IPs by plan and volume. The instinct
   was right about reputation and wrong about ports.
   [17](17-communication-and-email.md).
5. **The CLI is `cyc`, not `cc`.** `cc` is the POSIX name for the system C compiler and exists at
   `/usr/bin/cc` on virtually every Linux and macOS host; `CC` is the standard `make` variable for
   it. Installing a `cc` would shadow a build toolchain binary, and the planned `CC_*` env prefix
   carried the same ambiguity. `cyc` was checked before adoption: no binary of that name on PATH, no
   NuGet package id, and only an unrelated placeholder on npm — which does not matter, because the
   CLI ships as native per-RID binaries rather than an npm package. Config is `~/.cyc/`, env prefix
   `CYC_*`. ⚠ The platform's *other* uses of `cc` are unrelated to the binary and are unchanged: the
   NATS subject prefix (`cc.{tenant}.…`, [04](04-orleans-topology.md)), the Redis hash tag
   (`{cc:t:<tenantId>}`, [05](05-state-and-storage.md)) and the `cc-provider` project template.
   [21](21-cli-and-sdks.md).
6. **xUI is consumed from npm at its published version, not from the local checkout.** ADR-017's
   version-coupling note named Angular 22.0.8 / Tailwind 4.3.3 as "xUI's pins today", read from
   `~/Projects/Rikarin/xui`. That checkout is not the contract: xUI's CI increments the package
   version on publish and does not reflect it back into the repository, so the working tree
   understates what is released. The published truth as of 2026-08-11 is `@xui/* 2.2.0`, whose
   peer range is `@angular/*: 22` — a **major** range, not an exact pin — and which does not peer
   on `tailwindcss` at all. The portal therefore depends on `@xui/* ^2.2.0` from the registry and is
   free within Angular 22.x. ADR-017's substance stands; its version numbers were a snapshot of the
   wrong source. [20](20-portal.md).

Plus one thing the brief did not raise and that the audit made unavoidable: **the licence review**
(ADR-011). Redis, Vault, MongoDB, Elasticsearch and Terraform have all changed licences in ways that
specifically prohibit offering them as a service. Four of the brief's named modules are affected, and
the substitutions — Valkey, OpenBao, FerretDB, OpenSearch — are decided rather than discovered.
