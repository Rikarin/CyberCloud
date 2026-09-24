# 24 — Roadmap

Phases, exit criteria, and the cut list — decided in advance, because a cut list written under pressure
is a list of the things that were least defended rather than least important.

Effort is **EM** (engineer-months). Assume a team of 4–5. Plan against milestones, not dates.

## How to read the `Landed` column

**Reconciled against the tree on 2026-09-06** (#45). Every mark below is derived from one list — the
resource types this platform has actually published — and that list is recounted rather than quoted in
[§ What has landed](#what-has-landed--recounted-2026-09-23), which also says what would make the count
stale.

⚠ **A row is never deleted when it ships, and a shipped row's EM is never rewritten.** The phase a thing
was *planned* for, set against the phase it *landed* in, is the only record this plan keeps of how its
own estimates behaved, and striking the estimate out destroys exactly the half worth keeping. So a ✅
row's EM stays at what was estimated — it was spent, and that is the point — the `Landed` column says
what happened, and the arithmetic of what is left is done once, in
[§ Running total](#running-total). [25](25-risks-and-open-questions.md)'s closed rows are annotated in
place for the same reason and this document's own cut list has done it since 2026-08-11 — see row 7.

⚠ **The one place an EM figure *is* struck is a ⊘ row, and the distinction is the whole idea.** An
estimate for work that shipped is a record; an estimate for work that will never be done — because the
package was replaced, or because the operator it priced does not exist — is not a record of anything and
leaving it live invites it back into a sum. Shipped keeps its number. Refuted loses it.

| Mark | Meaning |
|---|---|
| ✅ | **Shipped.** A published resource type in `openapi/2026-08-01.json` for every type the row names |
| ◐ | **Partly landed**, and the cell says which part. ⚠ One EM figure cannot express a row that is half done and half blocked, which is the shape the `Network` row has been in since #23 |
| ⊘ | **Refuted or dropped.** The work is not owed *and the estimate is void* — which is not the same as shipped, and comes off the plan for a different reason and with a different confidence |
| ⛔ | **Blocked on facts outside this repository.** The effort is still owed; the schedule for it is not ours |
| — | **The published type list cannot speak to this row.** ⚠ Silence here is *not* evidence of shipping — see [§ What the type list cannot say](#what-the-type-list-cannot-say) |

⚠ **Every drift this reconciliation found runs the same way: work landed *ahead* of the phase that
planned it, never behind.** One row is the exception and it is called out where it sits — managed
identity, which [01](01-azure-parity-catalogue.md) verdicts M2 while this document schedules it in
phase 2. A one-directional error is a statement about the estimates, not about the tree, and it is why
the totals below are re-derived rather than nudged.

---

## Phase 0 — Prerequisites · ~~1.5~~ **1.1** EM

Small, unglamorous, and every one of them blocks something.

| Item | EM | Blocks | Landed |
|---|---|---|---|
| Repo, Nuke build, CPM, analyzers, `.editorconfig`, CI skeleton | 0.4 | Everything | — |
| Aspire AppHost: Redis, Postgres, NATS, k3s, hosts (ADR-014) | 0.3 | Local development | — |
| ~~⚠ **`Rikarin.Orleans.Streaming.NATs` → Orleans 10**~~ | ~~0.4~~ **0** | ~~ADR-005 — currently on Orleans 9~~ | ⊘ **DROPPED — the work is not owed and the estimate is void.** Orleans ships a first-party `Microsoft.Orleans.Streaming.NATS` built against Orleans 10, so there is no Orleans-9 package of ours to carry forward and ADR-005's fallback goes with it. [02 § the package register](02-technology-decisions.md) records the replacement and names this 0.4 EM as dropped; `Directory.Packages.props` carries the pin, with the version trap that `10.2.2-rc.2.alpha.1` sorts *above* `10.2.2-alpha.1` |
| `CyberCloud.ServiceDefaults` — silo/client builders, health, OTel, Serilog | 0.2 | Every host | — |
| Bootstrap cluster stood up by hand from `deploy/bootstrap/` | 0.2 | Phase 1 | — |

⚠ **Phase 0 is the one phase whose heading equals its own row sum**, so the drop comes straight off:
0.4 + 0.3 + 0.2 + 0.2 = **1.1**. No other phase in this document can take a subtraction that cleanly,
and [§ Running total](#running-total) says why.

**Exit:** `dotnet run` on the AppHost brings up a two-silo cluster with real Redis, Postgres, NATS and
k3s; a hello-world tenant-scoped grain round-trips through both storage tiers; CI is green.

---

## Phase 1 — The spine · 14 EM

Nothing tenant-facing ships. This is the phase people try to shorten and must not.

| Item | EM | Doc |
|---|---|---|
| Core: ids, `Result<T>`, `ResourceId`/`GrainKeys`, error registry | 0.8 | [06](06-tenancy-and-resource-model.md) |
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

**Criterion 4 is met, and it is the only one of the five this reconciliation checked** — deliberately,
because it is the criterion [§ Running total](#running-total) hangs an assumption on. `./build.sh
Architecture`'s **Generated surfaces** gate regenerates every surface from the provider registry and
compares bytes, which is what makes "and drift fails the build" a fact rather than an intention:

```text
✔ Generated surfaces  Enforced  45 resource type(s) over 2 OpenAPI document(s), 14 derived file(s) —
  the cyc verb tree, the .NET SDK, the portal forms, and the Python and Go SDKs (#40) — and 6 file(s)
  of the portal's TypeScript client, all regenerated and compared byte-for-byte
✔ Generated SDK compiles  Enforced  1 api-version file(s) declaring 390 type(s), each compiled on its
  own against CyberCloud.Sdk — 301 partial member(s) accepted as declared-but-not-implemented
```

⚠ **The second line read "251 type(s) … 176 partial member(s)" from 2026-09-15 until #39, and the jump is
not #39's.** That line was quoted from #40's run and no merge after it re-read the gate, while eighteen
resource types landed; the tree #39 branched from already declared 383 types and 294 partial members.
`Resources/deployments` adds seven of each — a collection, its data, its resource, two property bags
and the what-if's content and result; get, get by name, list, create, update, delete and what-if —
and the figures above are re-read from the run of 2026-09-24.

⚠ **The second gate is younger than the first and exists because the first was not enough** (#73): a
byte-comparison proves the emitter is deterministic and proves nothing about whether what it emitted is
valid C#. ⚠ And "compiles" is still short of "packaged" — the 301 partial members are the hand-written
half that does not exist yet ([21 § Generation](21-cli-and-sdks.md)); #79's fourteen duplicate wire
names closed 2026-09-15. The same day added two more rows of the same kind, **Generated Python SDK
compiles** and **Generated Go SDK compiles** (#40), which hand the other two generated SDKs to their
own toolchains and report ○ rather than ✔ when a toolchain is not installed — the quoted lines above
were re-read from a run on that day, when the derived count had grown from 3 to 14 with the two
packages.

⚠ **Criteria 1, 2, 3 and 5 are not marked here, and the reason is the reason for the whole `Landed`
convention:** the published type list can say that `CyberCloud.Sample/widgets` exists, and it cannot say
that killing a silo mid-provision converges. Marking them from the same evidence that settles
criterion 4 would be borrowing a proof.

⚠ **If phase 1 slips, everything slips, and no amount of provider work compensates.** Twenty providers
built on an unfinished manager is twenty copies of the manager's missing half.

---

## Phase 2 — M1: a tenant can log in and run something · 26 EM

| Item | EM | Doc | Landed |
|---|---|---|---|
| Identity: OpenIddict, users/groups/apps/SPs, passkeys, TOTP, sign-up flow, sessions | 4.8 | [11](11-identity.md) | — [01](01-azure-parity-catalogue.md) gives this row a module and no resource type, so nothing in the type list is evidence either way. #88's closing criterion met 2026-09-15: a person signs up (the code on the silo's console — no MTA, #93), signs in with Authorization Code + PKCE, holds a `cyc.api` token, refreshes from an `HttpOnly` cookie and signs out; `PersonOverHttpTests` drives it against the AppHost. #94 closed the path's security items: a code is exchanged once and a replay revokes the first exchange's session, a tenant client is consent-gated and its secret verified, `/userinfo` is served from the session, and sign-up's code issue and the code-verify endpoints carry a per-IP limit; `GrantsOverHttpTests` pins each over the wire. [11 § Protocol](11-identity.md#protocol) lists what is still owed on that path |
| Managed identity + token exchange | 1.2 | [11](11-identity.md) | ⚠ **Not shipped, and the type list says so rather than staying silent:** [01](01-azure-parity-catalogue.md) names `CyberCloud.ManagedIdentity/*` and nothing under it is among the 32. ⚠ **The one row that drifts the other way:** the catalogue verdicts managed identities **M2**, so this row is scheduled a phase *earlier* than the catalogue asks rather than having landed a phase early |
| Gateway: pipeline, auth, rate limits, region proxy, SignalR hubs, LRO endpoints | 4.2 | [10](10-gateway-and-api.md) | — no resource type. ⚠ #68 is an open **blocker**: the deployed gateway registers no `ICallerContextResolver` and 500s on every request, which is a different failure from "not written" and a worse one to read a green table over |
| **Managed Kubernetes** (CAPI + Kamaji + KubeVirt) + node pools + credentials | 4.0 | [09](09-kubernetes-fabric.md), [13](13-compute-vm-containers.md) | ◐ `ContainerService/managedClusters` and `…/agentPools` both published; **the third noun in this row is the one that is owed.** The descriptor writes `kube-secret://{namespace}/{cluster}-kubeconfig#value` and nothing resolves that scheme, so the first call through an attached connection fails on the credential — `charts/managed/kubernetes/conformance.yaml` § `the-cluster-this-creates-is-not-connectable`. #24 is open behind it: no bootable node image for a Kubernetes minor worth offering |
| Vault (OpenBao) | 2.0 | [18](18-security-vault-and-malware-scan.md) | ◐ `KeyVault/vaults` published (#30's prerequisite, 2026-09-23): secrets with versions, content type, enabled/validity and a seven-day soft delete under purge protection; RSA and EC keys generated or imported, with wrap/unwrap, encrypt/decrypt and sign/verify — all as data-plane actions through the gateway, authorized by four ReBAC data-plane roles no control-plane role implies, each item sealed under a per-vault root the platform vault (`CyberCloud.Vault`, OpenBao) holds. ⚠ **It runs only where a silo is configured with `CyberCloud:Vault`, and no topology is yet:** the silo host opts in since the #30 review, the AppHost has no OpenBao, and there a vault opens and then refuses every sealing call (`openbao-on-the-platform-topology`). ⚠ **Half of the row's noun and not its topology:** [18 § Shape](18-security-vault-and-malware-scan.md)'s namespace per tenant, `transit` engine, PKI and JWT auth are not built, certificates are not a type, and the owed list is at [18 § What landed, and what is owed](18-security-vault-and-malware-scan.md) |
| Postgres · Valkey · NATS providers | 3.0 | [12](12-managed-data-services.md) | ✅ all three — `DBforPostgreSQL/servers`, `Cache/redis`, `Messaging/natsClusters`. ⚠ #69 (the seven-day recovery window came back to an `initdb`) closed 2026-09-15 by taking the claims into the platform's custody across the soft delete; the operator re-electing a primary over reattached claims is the half only a cluster with CloudNativePG installed can show — `charts/managed/postgres/conformance.yaml § owed` |
| Container registry (Harbor) | 1.5 | [13](13-compute-vm-containers.md) | ✅ `ContainerRegistry/registries` |
| Network: VPC, subnets, security groups, public IPs, DNS, L4 LB, WireGuard | 6.3 | [14](14-networking.md) | ◐ **3.3 shipped, 3.0 ⛔ blocked outside this repository (#23).** The split is below, and it is the row this reconciliation was asked for by name |
| Object storage (SeaweedFS, S3) | 2.0 | [15](15-storage-blob-file.md) | ✅ `Storage/accounts` and `…/buckets` |
| Monitor workspaces + ingest + platform self-monitoring | 2.5 | [16](16-observability.md) | ◐ `Monitor/workspaces` published. ⚠ Only the first of this row's three nouns is a resource type; ingest and platform self-monitoring are not, so the type list is silent on 2.5 EM's other two thirds rather than confirming them |
| Metering + quota (no invoicing) | 1.8 | [22](22-billing-metering-and-quota.md) | — no resource type |
| Cloud terminal | 1.5 | [19](19-cloud-terminal-and-virtual-desktop.md) | ✅ `Terminal/consoles` |
| Portal M1 subset | 5.0 | [20](20-portal.md) | ◐ no resource type. #88: the portal signs a person in against the identity host and the subscription and resource-group pages list the two scope collections; the `Create` on a resource group makes a resource the person's token authored. #22: the generated form renderer, create/edit, the resource blade and list, the operation view, and the subscription and resource-group pages exist over the generated client; the access page exists over a hand-written `RoleAssignmentsApi`, because #70's address reaches no generated surface — it grants, checks and revokes by name at a subscription, a resource group or a resource. #22 again: the cloud shell exists — `subscriptions/{s}/resourceGroups/{g}/terminal` lists the group's `Terminal/consoles` or renders the generated create form for one, calls `connect`, mints a gateway hub ticket, opens `/hubs/terminal` into an `xterm.js` pane and reconnects with a fresh ticket; what the pane shows once attached is the hub refusing by name, because docs/plan/19's session grain is not built (`charts/managed/cloud-shell/conformance.yaml § owed`). ⚠ Quota/usage waits on an endpoint (docs/plan/22); the access page's *list* shows an empty state naming the collection `GET` — which #86 landed the same day on a sibling branch, so the page has a server to call and does not call it yet; it validates a principal's shape, and the server now refuses one that does not exist (#86); the effective-permissions explorer — docs/plan/20's "why does this user have access" — waits on an HTTP address for a check, and `ICheckGrain.CheckAsync` reaches none; the region/cluster/storage-class/subnet pickers wait on list endpoints |
| `cyc` + .NET SDK + TypeScript packaging | 3.2 | [21](21-cli-and-sdks.md) | ◐ all three surfaces are generated and byte-compared by the **Generated surfaces** gate, the SDK compiles (#73), the TypeScript client exists (#21) and `cyc list` pages (#64). ⚠ Generated is not packaged. #79 (fourteen duplicate wire names) closed 2026-09-15: the SDK nests a class per wire container and a gate refuses a wire name declared twice |
| Platform hardening: supply chain, admission, isolation, log canary | 1.0 | [18](18-security-vault-and-malware-scan.md) | — no resource type. #15 (the admission policy — the third control in doc 18's Secrets row) landed 2026-09-15 as `charts/bundle/cybercloud-admission`, admitted by a real API server by hand and by no test yet; #17 landed the same day — `Build.Licence` reads the artefact, and `install.sh` refuses an image whose tag no longer serves its recorded digest |

*(Sums to ~44; ~18 of it runs in parallel with phase 1's tail and with itself. The 26 is the critical
path, not the total.)*

**The `Network` row, split — because one number could not hold it.** The 6.3 is not an opinion; it is
[14 § Effort](14-networking.md)'s four M1 lines added up, which is what makes the split below arithmetic
rather than a guess.

| Piece of the 6.3 | EM | Landed |
|---|---|---|
| VPC, subnets, security groups, public IPs, dual-stack | 2.5 | ✅ `virtualNetworks`, `…/subnets`, `…/securityGroups` and `publicIpAddresses`, all published. ⚠ `routeTables`, which [14 § Effort](14-networking.md) counts *inside* this 2.5, is **refused** rather than pending — Kube-OVN's `Vpc` carries static routes as an array on the parent, so two `routeTables` children would converge by erasing each other. The refusal and its reasoning are in `src/Providers/CyberCloud.Providers.Network/CyberCloud.Providers.Network/NetworkProvider.cs` |
| L4 load balancers | 0.8 | ✅ `virtualNetworks/loadBalancers`, on HAProxy |
| DNS zones + records + DNSSEC | 1.5 | ⛔ #23. ⚠ **Not an effort problem, and the blocker is structural:** Kube-OVN's in-VPC resolver is `VpcDns`, which lives inside `if config.EnableLb`, and ADR-019 runs Kube-OVN with `ENABLE_LB=false` so Cilium owns the service datapath. A tenant VPC on this platform therefore has no name resolution at all — a fact every type inside a VPC has to be designed around, not a gap to fill later |
| WireGuard VPN | 1.5 | ⛔ #23. WireGuard reads **one** configuration file listing every peer, so a `vpnClients` child writing into its parent's file is `routeTables`' refusal in a second shape. Needs a per-peer CRD from an operator whose existence, maintenance and licence are unchecked, or a parent reconciler that can list its own children |

⚠ **A blocked row is not a saving.** The 3.0 EM above is still owed; what is not ours is the date. It is
marked ⛔ rather than ⊘ precisely so that [§ Running total](#running-total) does not subtract it.

**Exit — the M1 story, end to end, by a design partner with no help from us:**

> Sign up with a passkey → tenant and subscription created → create an in-house Kubernetes cluster
> (~8 min, with visible steps) → create a VPC and a Postgres server in it → get the connection string
> from Vault → open the cloud terminal and `psql` into it using a managed identity → see metrics and
> logs → invite a colleague and grant them Reader on one resource group → do all of it again from
> `cyc` → see the usage accruing.

⚠ **Steps 4–5 run under test since 2026-09-17**, on a k3s the test installs the operator onto:
`test/CyberCloud.Bundle.Cluster.Conformance § M1StoryOnAFreshCluster` creates the VPC, a subnet and
the server through the real resource manager, waits for CloudNativePG's primary, fetches the
credential and runs `SELECT 1` with it from inside the cluster. Two things the sentence says that the
test does not: "in it" — no property of `DBforPostgreSQL/servers` names a network or a subnet, so the
server is merely in the same resource group, and the VPC and subnet are Kube-OVN objects admitted
against open-schema stubs on a lane with no Kube-OVN; and "from Vault" — this type declines the vault
seam by design and the credential's path out is `listKeys` reading the operator's Secret
(`PostgresServers.ClusterJson`). The `psql` is the test's, not the cloud terminal's.

⚠ **Steps 7 and 8 run under test since 2026-09-23 (#43)**, and neither needs a cluster. *Invite a
colleague and grant them Reader on one resource group*: an owner `POST`s an address to
`/tenants/{t}/providers/CyberCloud.Identity/invitations` (a reserved namespace like the resource
graph's, so no resource type and no change to [§ What has landed](#what-has-landed--recounted-2026-09-18)'s
count); `InvitationService` checks `assignRole` on the tenant, the invitation grain creates an
invited user and mails a one-time, seven-day link through the platform's communication service, and
the link opens the identity app's invitation page, where a new person — or one who already has a
user in another organisation, since a user is per tenant — chooses a name and a password and lands as
a member with **no role**; Reader on the group is the existing role-assignment `PUT`, which the
invited user now answers for. `Identity.Host.Tests § InvitationThroughTheGatewayTests` is the route
itself: an owner's `cyc login --device-code` token from the real identity host, the `POST` through
the real gateway (`GatewayComposition`, trusting that host by its issuer) to the real
`InvitationService` and grain, the mail out of a Mailpit Testcontainer, the accept, and the
colleague's own token refused the same `POST` with a `404`. ⚠ It was added after review: until then
the gateway suite substituted the manager, `CyberCloud.Isolation` called the manager in process, and
the HTTP suite created the invitation at the grain, so every half was tested and the route a dev run
is told to `curl` was not. `§ InvitationsOverHttpTests` keeps the link's cases (a second use, an
expired link, a made-up one); `CyberCloud.Isolation § InvitationTests` runs invite → accept → Reader
→ `Check` through the real manager and engine, and pins that a link cannot reactivate a member who
was suspended or removed, or who joined through another link.
*Do all of it again from `cyc`*: `cyc login --device-code` is RFC 8628 against the identity host
([21 § Signing in and out](21-cli-and-sdks.md)), and `cyc logout` revokes;
`Identity.Host.Tests § DeviceFlowOverHttpTests` and `§ DeviceFlowThroughTheSdkTests` run both
against the real host, and the first pins that an approval the device hasn't collected dies with the
sign-in that gave it and with the account (the review found a suspended user still getting tokens).
`§ IdentityHostInItsOwnProcessTests` runs the device flow and an invitation's accept through the
host's own executable, a separate process joined to the test cluster, because every other suite
shares one Orleans type manifest with the silo, and that's how #39's refused type got to master.
⚠ **Owed:** an audit of the reformat commit `e21006d` for further changes in meaning. #43 found two,
the access token's `oi_tkn_id` and `RoleAssignmentService`'s paging, and fixed both; the rest of that
commit was not read. What neither test is: a person in a browser — the device page and the
invitation page are pinned by their Jest specs and by the HTTP suites against their APIs, and the
rehearsal on a dev run by hand is what remains; the portal has no page that sends an invitation
(#22), so on a dev run the invite is `cyc rest` or `curl` against the gateway; and "all of it again"
is only as whole as the steps before it, which still need a cluster somebody prepared (#24/#95).

Plus: three design-partner tenants running for four weeks with no cross-tenant incident; the chaos
invariants green; the load suite meeting the [00](00-vision-and-principles.md) budgets at 10 % of
target scale.

> **2026-09-18 — the last two clauses have suites and first numbers.** `./build.sh Chaos` runs
> `test/CyberCloud.Chaos` and `./build.sh Load` runs `test/CyberCloud.Load` at exactly the 10 %
> this clause names; [23 § The chaos invariants](23-build-ci-and-testing.md) and
> [23 § The load scenarios](23-build-ci-and-testing.md) carry the dated tables. Neither clause is
> met: chaos rows 4 and 5 are ✘ (a resource whose cluster is unreachable reads `Failed`; a gone
> shard surfaces as a serializer exception), row 7 is ○ on a zero that a rolling restart does not
> always produce (the gateway does not retry a request that lands on a leaving silo), and row 6 is
> ○ (no NATS client exists); load rows
> `control-plane-write-p99-ms` (a stall inside the first reminder wave) and
> `reconcile-queue-depth-slope-per-minute` (a queue whose floor is one reminder period deep) are ✘,
> and the two unhosted rows are ○. What each needs is written beside it.

---

## Phase 3 — M2: a catalogue that is a business · 28 EM

| Group | Items | EM | Issue | Landed |
|---|---|---|---|---|
| Data | FerretDB, RabbitMQ, Kafka, ClickHouse | 4.4 | — | ✅ **SHIPPED AHEAD OF PHASE 3 — all four, and the whole row.** `DocumentDB/accounts`, `Messaging/rabbitmqClusters`, `Messaging/kafkaClusters`, `Analytics/clickhouseClusters`, every one published with a provider, a chart and a conformance suite. ⚠ The 4.4 is [12 § Effort](12-managed-data-services.md)'s four M2 lines exactly — 1.2 + 0.8 + 1.2 + 1.2 — so the whole of it is spent, not part |
| Compute | VMs + disks + images, scale sets, container instances | 3.8 | #28 | ◐ `Compute/virtualMachines`, `Compute/disks` and `Compute/images` published (#28, 2026-09-17) on KubeVirt and CDI, with the charts under `charts/managed/virtual-machine`, `charts/managed/disk` and `charts/managed/image` — the VM noun of [13 § Virtual Machines](13-compute-vm-containers.md): a size from a closed set that is what the guest gets and what quota reserves, a root disk cloned from an image in the same resource group, disks by name, a subnet by the Network family's own object name, cloud-init as a vault handle, and `start`/`stop`/`restart` as the first handlers in the tree that write a cluster. ⚠ **The first three nouns of five, and the first family whose webhook half is measured rather than owed**: `CyberCloud.Providers.Compute.KubeVirt.Cluster.Conformance § KubeVirtOnAnEmptyCluster` installs CDI and KubeVirt through `install.sh`, imports an image, provisions a disk and applies a machine that attaches it against the real webhooks — on every merge in the `Cluster` lane of `Test`, never on a PR, for the eight minutes it costs — and the guest reports `Running` under KVM in 46 seconds with the disk populated and mounted, on the k3s-in-Docker lane #95 said had no `/dev/kvm` (it has: WSL2 nests virtualization and the k3s container is privileged). What is not reached is the guest itself — console, cloud-init, the disk inside — `charts/managed/virtual-machine/conformance.yaml § owed`, `the-guest-is-not-reached`; and a cloud-init handle may name only the tenant's own vault prefix, which the review of #28 found nothing enforcing. Scale sets and container instances are not types; `charts/managed/virtual-machine/conformance.yaml § owed`, `scale-sets-are-not-landed` and `container-instances-are-not-landed`, say what each waits for. ⚠ The 3.8 is the row's whole price and no EM is claimed: doc 13 prices the VM noun at 3.0 and what landed is its core |
| Registry | NuGet/npm/Maven feeds | 1.5 | #29 | ◐ `ContainerRegistry/feeds` published (#29, 2026-09-15) with `CyberCloud.Registry.Feeds.Host` serving NuGet v3, npm and Maven over the gateway's bearer tokens, artefacts on the platform's object store, the catalogue in a durable grain. ⚠ The *host* third of [13](13-compute-vm-containers.md)'s "proxy + host + retention"; proxy and retention are not declared, and both are owed at `charts/managed/feeds/conformance.yaml § owed`, together with the two meters the row must emit and does not |
| Storage | File shares, backup vaults, customer-managed keys | 3.0 | #30 | ◐ `Storage/accounts/fileShares` published (#30, 2026-09-15) with the chart under `charts/managed/seaweedfs-fileshare` — a `ReadWriteMany` claim the SeaweedFS CSI driver binds against the account's filer. ⚠ **The first noun of three, and a correction to [15](15-storage-blob-file.md) came with it:** the engine serves no NFS, so the pod half ships and the VM half is owed. Backup vaults are published as `RecoveryServices/vaults` (2026-09-18) and, since 2026-09-24, protect end to end: a PostgreSQL server archives to a bucket of its own on the platform's object store with a vault-held key, `backupNow` takes a point, `recover` creates a new server resource as the caller, and a manager-started pass enforces retention — proven on a real k3s with CloudNativePG and SeaweedFS ([08](08-resource-manager.md) § The manager-started pass, [15](15-storage-blob-file.md) § Backup as a service). Customer-managed keys stay owed (`charts/managed/seaweedfs/conformance.yaml` § owed, `customer-managed-keys`). The same pass took `buckets`' `stats` action off `actions-without-handlers.txt`. ⚠ The k3s-backed lifecycle and silo-kill suites for all three Storage types first ran green on a real API server on 2026-09-15, after #30's review found them skipped on every machine that had tried — the harness now starts k3s 1.35 on a cgroup v1 Docker host — and the first real run found two defects the fake could not: the API server respells a claim's request (`102400Mi` → `100Gi`) and a shared driver cannot pass "delete → gone" with a sibling left behind |
| Network | Application gateway + WAF, NAT, peering, flow logs | 3.6 | #31 | ◐ `Network/virtualNetworks/natGateways` published (#31, 2026-09-15), on the router's own `OvnSnatRule` naming the address `publicIpAddresses` already renders rather than on the `VpcNatGateway` pod [14](14-networking.md) names — and it is the first thing on the platform that attaches a public address to anything. ⚠ **`Network/virtualNetworks/peerings` published (#31, 2026-09-18), on #89's co-owned apply — the first type in the tree that owns no object.** A Kube-OVN peering is two entries on two `Vpc` specs, both atomic lists (the `routeTables` refusal, twice over), and the type writes each network's half as a fragment under one manager named for that network ([09 § A second writer on an object](09-kubernetes-fabric.md)): the networks' labels stay, teardown withdraws and both networks stand, the live `resourceVersion` makes two peerings racing onto one `Vpc` lose loudly. The body is the remote network's name — in the same resource group, for an authorization reason rather than a schema one — and three ranges the reconciler refuses terminally when any two overlap. The shared Docker-free suite grew the co-writer's reading of its ownership assertions (a fake that models the second manager, a world put back after every reset, drift as a slice stripped by hand) and runs the peering at the network's exact count; the cluster-backed half runs a dedicated class on the real k3s, where the co-writer manager reads back off `managedFields`. ⚠ **What is proven is the write and not the routing**: no harness here has a Kube-OVN controller, so the peer ports and the routes wait for the VM lane (#95) — `charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed`, `routing-is-unproven-until-the-vm-lane`, with the rest of what is still owed about peering beside it. ⚠ **`Network/virtualNetworks/applicationGateways` published (#31, 2026-09-23), on HAProxy and Coraza SPOA rather than the Envoy Gateway [14](14-networking.md) named** — the decision and its four readings are in [14 § Application gateway](14-networking.md): no Gateway API controller in the bundle, a controller-owned proxy pod the platform could not put on a tenant subnet, bare-address backends only through an off-by-default Envoy Gateway extension, and HAProxy already the precedent, with Coraza SPOA as the OWASP project's Apache-2.0 WAF for exactly that proxy. Listeners (HTTP, HTTPS from a vault handle under the tenant's own prefix), first-match routing by host and whole-segment path, pools of addresses or `Compute/virtualMachines` resolved through #90's view, HTTP health probes, and a WAF policy (off, detection or prevention; CRS 4.25; paranoia 1–4; exclusions; custom rules in a closed grammar), both images pinned by digest. ⚠ **Traffic is proven on the real k3s, not reasoned**: `ApplicationGatewayTrafficConformance` routes by host and by path to real backend pods through the real reconciler's pod, serves HTTPS from the harness vault, answers a SQL-injection probe 403 in prevention and passes and logs it (`waf-rules:942100`) in detection. What waits for #95's lane is the tenant-network half — flannel ignores the two Kube-OVN annotations — and the rest is owed at `charts/managed/application-gateway/conformance.yaml § owed`: no inbound address, one replica, bodies inspected to HAProxy's buffer, no response inspection, header rewrites, rate limits and ACME. ⚠ **#31 stays open: three nouns of four landed.** Flow logs are measured and owed at `charts/managed/kube-ovn-vpc/conformance.yaml § owed`, `flow-logs-have-nothing-to-render` — the bundle installs no Cilium and so no Hubble, the substrate's CRDs export no flow, Kube-OVN's first flow-shaped feature (v1.16.7's experimental ACL sampling) samples NetworkPolicy ACLs this platform turns off, and [01](01-azure-parity-catalogue.md) files the deliverable under `CyberCloud.Monitor` at M3 rather than here. ⚠ The 3.6 is [14 § Effort](14-networking.md)'s 2.0 + 0.8 + 0.8; what landed is the whole of the 0.8 `NAT gateways, peering` line, and that 0.8 is claimed below. The 2.0 `Application gateway + WAF` line is **not** claimed: its core landed and four of the nouns [14](14-networking.md) lists for it — header rewrites, rate limits, ACME and a public address — are owed rows, and the row has no per-noun split to take a fraction from |
| Observability | App Insights views, OTel collector service, managed Grafana, alerts | 3.0 | #32 | ◐ **All four nouns are published types, and ◐ is for what each still owes.** `Monitor/workspaces/alertRules` 2026-09-15; `Monitor/workspaces/collectors` and `Dashboard/grafanas` 2026-09-17; and `Monitor/workspaces/components` 2026-09-23 — the App Insights-shaped noun: a connection string a pod can `envFrom`, and five views (requests, dependencies, exceptions, application map, transaction) read from the workspace's ClickHouse traces and logs with the workspace's database taken from the platform's index, asserted over telemetry the real collector exported into a real ClickHouse — [16 § Application views](16-observability.md). What stays owed is each chart's `conformance.yaml § owed`: nothing creates a workspace's tables in production (the platform's statements exist and are proved equal to the exporter's), the portal views are #41's, and below. `Monitor/workspaces/alertRules`, in detail: a condition over the workspace's metrics or logs, a severity and an action group naming a `Communication/services` resource, evaluated by one grain per workspace on a reminder — [16 § Alerts](16-observability.md). ⚠ **What ◐ does not mean**: no host registers a real query seam, so every evaluation in this tree runs against the refusing default and moves nothing — same file, `alert-rules-query-seam-is-refusing` — a notification reaches a carrier only where one is registered, which is the `Communication` row's own caveat one row down, and the action group offers the sending module's five channels and not doc 16's five: webhook and the portal inbox are `alert-rules-webhook-and-inbox-not-landed`. ⚠ **Reviewed the same day** (#32 review): a seam that throws no longer ends the pass, the pass stops asking at a budget under Orleans' response timeout with the reads interleaved, and the reconciler judges an enabled rule converged on its spec *and* its reminder row — [16 § Alerts](16-observability.md) |
| Communication | Channels, templates, suppression, delivery receipts | 2.0 | #33 | ✅ **SHIPPED 2026-09-15 (#33)**: `Communication/services`, `Communication/services/channels`, `Communication/services/templates`, `Communication/services/suppressions` — the tenant-facing face of the module that had carried the platform's OTPs since before any provider existed. ⚠ **Three of the four nouns are types and the fourth is not, deliberately**: a delivery receipt is per send, a send is an event rather than desired state, and the receipts come back on the `services` type's `status` action — [17 § `CyberCloud.Communication/services`](17-communication-and-email.md) says why a `messages` type would have had a PUT nothing could apply. ⚠ **The first clusterless family in the catalogue**, and the shared conformance suite grew a clusterless half to say what a green run over it proves. ⚠ **What ✅ does not mean here, and it is to be read literally**: one carrier *client* ships — email over SMTP to a configured relay, since 2026-09-18 (#93), with Mailpit as the AppHost's relay so a sign-up code lands in an inbox — and the other four channels resolve to the module's refusing seam, so an SMS `send` refuses honestly; the relay itself (the warmed pool, PTR, feedback loops, the separate sending IPs) is not deployed and **#93 stays open for it**, for the SMS/WhatsApp/voice carriers and for the receipt ingress — the client was the part of that issue a repository can hold; doc 17's sender-id registration flow has its grain and no resource surface; and the fourth noun has its read half only: `status` renders receipts and no HTTP ingress exists for a carrier to deliver one through, which lands with the first deployed relay because its signature is the callback's only authentication — [17 § The outbound carrier](17-communication-and-email.md) has the email-specific design; `charts/bundle/bundle.yaml § owed` carries all three |
| **Mail** | Postfix/Dovecot/Rspamd, domains, mailboxes, deliverability, minimal webmail | 3.5 | #34 | ◐ `Mail/domains` published (#34, 2026-09-12) with the chart under `charts/managed/mail`, and `Mail/domains/mailboxes` (#34, 2026-09-23) under `charts/managed/mail-mailbox`, with the domain's `dnsRecords` and `verify` actions and the sending gate they decide. ⚠ **Three nouns of five, and half of a fourth** (corrected by the #34 review, which read the first wording — "four of five" — as counting deliverability landed): the Postfix/Dovecot/Rspamd back end, domains and mailboxes are built; of deliverability, the DNS gate, `verify` and the sender lock are, and the outbound pool, its warm-up, PTR, feedback loops, the MTA-STS policy host, DKIM rotation and rate limits are not; webmail is not started. What keeps the row ◐ is mostly not in the type list: the shared front doors, the outbound pool, and a Postfix image nothing publishes — `charts/managed/mail/conformance.yaml § owed` |
| Security | Malware scanning | 1.5 | #35 | |
| Fabric | ⚠ Agent-initiated cluster connections (BYO behind NAT) | 1.5 | #36 | ◐ `ContainerService/connectedClusters` published (#36, 2026-09-15): the agent (`charts/agent`, `CyberCloud.Agent.Host`), the gateway's tunnel endpoint, the tunnel grain and the connection grain's route through it, with the attach flow — create, `listInstallCommand`, first heartbeat → `Succeeded` — pinned end to end in-process. ⚠ **Two halves are not landed and the row stays ◐ for both:** informers do not cross the tunnel (a watch is a stream, a frame has one answer), and **no suite crosses a real NAT** — the M2 exit criterion this row exists for is unproved until the "deliberately hostile BYO cluster" of [09 § Testing the fabric](09-kubernetes-fabric.md) exists. `charts/agent/conformance.yaml § owed` holds both |
| ReBAC | `ListObjects`, Leopard index | 2.2 | #37 | ✅ `ListObjects` landed 2026-09-15 as `IListObjectsGrain` — a scoped reverse walk the resource list now intersects with instead of a `Check` per member. The Leopard index landed the same day as `IMembershipIndexGrain`, keyed by subject object and holding both directions, written by the tuple store as a step of every write and delete; `Check` tests membership against it and the walk reads a subject's closed usersets from it in one read. ⚠ **Owed, and recorded in [07 § The Leopard index](07-rebac-authorization.md)**: the materialization threshold, the roaring bitmap, a tenant-wide rebuild after a schema bump, and the resource-graph access column |
| Billing | Rating, invoicing, PSP, tax service, cost views, budgets | 3.6 | #38 | ◐ `Billing/budgets` published (#38, 2026-09-23) — an amount per month, quarter or year over a resource group or, once the budget itself is granted reader, a subscription, with thresholds on the actual cost and on the linear seven-day forecast that alert through a `Communication/services` resource. Behind it, in `CyberCloud.Billing`: **rating** over a committed, date-versioned price sheet with tiers per subscription per month and free tiers as zero-priced tiers; the **monthly invoice** per tenant billing account — a draft rated from the usage ledger on every read, finalized after the 48-hour late-usage window into an immutable document numbered gap-free per issuer, corrected only by credit note; the **cost query** at `{scope}/providers/CyberCloud.CostManagement/query` by resource, group, type, meter or day, every row filtered by ReBAC behind the one seam; **EU VAT** behind the tax seam; and a **Stripe adapter** behind the PSP seam, tested against `stripe/stripe-mock`. ⚠ **What is not landed, and the row stays ◐ for it** — [22 § What is owed](22-billing-metering-and-quota.md) lists each: nothing yet creates a billing account or attaches a subscription, so the month close — built, and armed by the first attach — closes nothing in production, and no deployment configures the invoice issuer it would need; dunning, the invoice PDF, cost by tag and the export, the PSP's host wiring and webhook endpoint, VIES validation, a debit note for late usage, the portal's cost page (#41), and storage rated on the declared size rather than a measurement. ⚠ The 0.4 claimed below is [22 § Effort](22-billing-metering-and-quota.md)'s *Budgets, alerts, forecast* line, the one line of the four whose every noun shipped. ⚠ Shipped is not fed: a budget reads the usage ledger, which stays empty in production until the sampler has a source ([22 § What is owed](22-billing-metering-and-quota.md), `the-sampler-has-no-source`), so today no production budget shows a figure above zero or alerts |
| Platform | Management groups, deployments (templates), shard pinning | 1.5 | #39 | ◐ **All three nouns have landed; the type list can show one of them.** *Deployments* landed 2026-09-23 as `Resources/deployments` — the one noun of the three that is a resource type — on nested operations built for it: the deployment's own `PUT` is an ordinary write whose operation is the parent, `DeploymentDriver` writes one child per template resource in dependency order through `IResourceManager.WriteChildAsync` *as the deployment's creator*, a child's failure fails the parent naming it, cancellation reaches the child in flight, rollback is recorded and not performed, and `whatIf` diffs a template against current state read as the caller — [08 § Long-running operations](08-resource-manager.md), with what is still owed about it recorded there. The other two landed 2026-09-18 and the type list cannot show either — both are scopes and platform operations, not resource types. *Management groups*: `/tenants/{t}/managementGroups/{name}` and its flat collection, the 26th grain-key shape (`mg/{name}`), `IManagementGroupGrain`, a `managementGroup` ReBAC type at `SchemaVersion` 3, subscriptions assigned by `PUT` with the `parent` edge *relinked* so the chain stays a chain, role assignments and their collection at the new scope, and the generated surfaces through `OpenApiEmitter.ScopePathItems` — [06 § The hierarchy](06-tenancy-and-resource-model.md), with the group move and the group-level lock recorded there as owed; policy at a group, owed here too when #39 landed, came with #46 on 2026-09-23 ([08 § Policy](08-resource-manager.md)). *Shard pinning*: `IShardMapGrain.PinAsync`'s placement half — `TenantCreateRequest.DurableShard` pins before the assignment — with the move refused by name; [05 § The shard map](05-state-and-storage.md) records the move as M3. ⚠ **The row stays ◐ for what each noun still owes, not for a missing noun**: the management-group move, group-level locks and policy; the shard move (M3); and deployments' outputs, conditions and copy loops, nested deployments, a performed rollback, and a what-if for a secret property — each recorded where its noun's section is. The 1.5 is not claimed |
| SDKs | Python, Go | 1.0 | #40 | ◐ both generated from the published document 2026-09-15 into `generated/sdk-python` and `generated/sdk-go`, byte-compared by **Generated surfaces** and each handed to its own toolchain by a gate that reports ○ when that toolchain is absent. ⚠ Generated is not packaged, and neither has a credential type or a retry — [21 § Python and Go](21-cli-and-sdks.md) lists what is owed |
| Portal | Cost analysis, metrics explorer, log search, identity admin | 2.3 | #41 | ◐ **The metrics explorer and log search landed 2026-09-23 (#41), with a resource-graph explorer beside them** — [20 § The pages that are not generated](20-portal.md). Both explorers read a Monitor workspace through three read actions on the workspace type, `queryMetrics`, `listMetricLabels` and `searchLogs`, checked with `read`, answered under the workspace's own `accountID` and database and proved against a real VictoriaMetrics cluster and ClickHouse with a cross-tenant refusal — [16 § Querying a workspace](16-observability.md). The type list confirms nothing here: the explorers are actions and pages, not types. ⚠ The first charts in the portal, and the budget they needed is recorded in [20 § Performance budget](20-portal.md): ECharts is one lazy chunk held to its own 180 KB ceiling because no ECharts build fits 120 KB. What is owed is at `charts/managed/monitor-workspace/conformance.yaml § owed`, from `query-responses-are-undeclared` to `explorers-are-wired-on-a-laptop-only`. ⚠ **Landed is not deployed:** only the AppHost configures the gateway's stores, and only the log half, so the metrics explorer refuses by name everywhere; and the `accountID` it reads under is the workspace GUID folded to 32 bits, which two workspaces can share — #41's review added the ledger that refuses the second workspace at create and refuses its reads, and what is left is `accountid-is-folded-not-allocated`. Cost analysis and identity admin are not part of this landing — the first waits on #38's billing aggregates; the 2.3 is not claimed **Identity admin, three of its five nouns (#41, 2026-09-24)** — no resource type, so the type list can't show it: members, invitations and registered applications for a tenant owner, and the signed-in person's own sessions, over an identity administration API at the gateway under the reserved `CyberCloud.Identity` namespace ([10 § Shape](10-gateway-and-api.md), [11 § Sign-up and tenant creation](11-identity.md)) and a per-tenant directory index, the 32nd grain-key shape. A confidential client's secret is now minted by the platform and kept as a digest, so a client registered from the portal signs people in end to end. ⚠ Groups, MFA administration and sign-in logs are not landed — [20 § The pages that are not generated](20-portal.md) lists each with the rest of what is owed. The other three surfaces of this row are separate branches of #41 **Cost analysis landed (#41, 2026-09-24)** at `subscriptions/{s}/cost` and `…/resourceGroups/{g}/cost`: cost over time by service, resource group, resource or meter for a period in the URL, a stacked daily chart on `@xui/echarts` beside the table that is its accessible copy, a month-end forecast linear on the trailing seven days, a group's budgets with the thresholds that fired, and the tenant's invoices with their lines — [20 § The pages that are not generated](20-portal.md). ⚠ **The chart library is over the route budget by construction**, so it has a ceiling of its own: ECharts' core alone is 129.3 KB gzipped against 120, and the portal's build is 172.8 KB in one deferred chunk against 180, a ceiling #41 proposed and nobody has yet signed off ([20 § Performance budget](20-portal.md)). The metrics explorer, log search and identity admin are not started; what cost analysis still owes is [20 § What is owed](20-portal.md). ⚠ The 2.3 is four pages and no EM is claimed: the cost page is [20](20-portal.md)'s 0.6 less by tag and the per-resource figure on the blade |

⚠ **The `Issue` column is a check on the row above it and it found the same thing twice.** Fourteen
issues carry the M2 milestone — #28 through #41 — and they are these fifteen rows *minus* `Data`. The
one row with no issue filed against it is the one that had already shipped, which is a second,
independent witness to the same fact the type list gives and is worth more than either alone.

⚠ **This phase's rows sum to 38.4 EM against a 28 EM heading, and this document never says why.**
Phase 2 states its own parallelism in a parenthetical — *"sums to ~44; ~18 of it runs in parallel"* —
and phase 3 does not.

⚠ **There are two explanations for the 10.4 EM and this reconciliation cannot choose between them, so
it names both.** (1) A critical-path assumption, unstated here and surviving only in
[§ Running total](#running-total)'s column header — which is how this document read it until
2026-09-06. (2) **The simpler one, and the one with evidence behind it:** the heading is not this
phase's arithmetic at all. [01 § Summary of scope](01-azure-parity-catalogue.md)'s M2 milestone total
is **28**, and its M3 total is **20** — phase 4's heading, to the digit. Two headings taken from the
milestone budget of another document is enough to say these are a **top-down** allocation while the rows
below them are **bottom-up** per-item estimates, in which case the difference was never parallelism and
the two numbers were never the same quantity. ⚠ The third milestone does not match and is not evidence
either way: doc 01's M1 total is **46** against phases 0–2's 1.5 + 14 + 26 = **41.5**, which is its own
disagreement and is recorded in [§ Running total](#running-total). ⚠ The effect on the plan is the same under either reading and is the reason it is
written here: 4.4 EM of finished work still cannot simply be subtracted from the 28, because it is not
established that the 28 ever contained it. Under reading (2) it is not even certain that it should
shrink the 28 at all. Deciding this is a scheduling exercise on one side and a re-estimate on the
other — see [§ Running total](#running-total), which is where the consequence is carried.

**Exit:** 28 resource types; paying customers on self-serve billing; a BYO on-prem cluster in
production behind NAT; the first managed mail domain sending with a clean reputation for 30 days;
median time-to-add-a-managed-service measured and ≤ 2 engineer-weeks.

⚠ **On "28 resource types": there are 45 today**, and twenty-six of them are this phase's — the `Data`
row's four, `Mail/domains` and `Mail/domains/mailboxes`, the `Communication` row's four, `Monitor/workspaces/alertRules`, the
four that merged on 2026-09-15 from four branches that each counted only itself
(`Storage/accounts/fileShares`, `Network/virtualNetworks/natGateways`,
`ContainerService/connectedClusters` and `ContainerRegistry/feeds`), the seven that merged on
2026-09-18 from five branches that each counted only itself again (`Network/virtualNetworks/peerings`,
`RecoveryServices/vaults`, `Monitor/workspaces/collectors`, `Dashboard/grafanas`,
`Compute/virtualMachines`, `Compute/disks`, `Compute/images`), and `Resources/deployments` (#39) and `Billing/budgets` (#38) `Monitor/workspaces/components` (#32) and `Mail/domains/mailboxes` (#34) on 2026-09-23,
each recounted in the same change, and `Network/virtualNetworks/applicationGateways` (#31, 2026-09-24),
recounted when its branch merged master rather than on its own. The other
nineteen are counted, phase by phase, in [§ What has landed](#what-has-landed--recounted-2026-09-23) —
which is also where to see that two of the 45 belong to phase 4 and one is phase 1's deliberately
trivial sample. ⚠ **The count passed the criterion and the criterion is not met**: "28 resource
types" was written as a proxy for a catalogue, sixteen of the 45 are children of families, and the
rows that reached the number are each one noun of several. Read the exit off the rows, not the
total.

---

## Phase 4 — M3: depth · 20 EM

⚠ **This was one prose line of seventeen items and is now a table, because two of the seventeen had
shipped and a comma-separated list has nowhere to say so.** Nothing is removed; the seventeen are the
seventeen. `Issue` is #42's per-item set — filed after #42's own framing was corrected, because
[01](01-azure-parity-catalogue.md) does carry a namespace and an estimate for most of these rows and
the design was never as absent as the line made it look.

| Item | Issue | EM | Landed |
|---|---|---|---|
| Policy engine | #46 | ⚠ none | ◐ **The engine landed 2026-09-23, in the write path, and its addresses reached every generated surface with its review on 2026-09-24 — `cyc policy`, `PolicyClient` in three SDKs, the portal's client; what keeps this row from ✅ is what stays owed.** Definitions, assignments at a management group, a subscription or a resource group, and `policyStates` are scope extensions under `CyberCloud.Policy` — not registry types, for the two reasons [08 § Policy](08-resource-manager.md) gives — and step 5 of every `PUT`, `PATCH`, `DELETE` and action asks the tenant's policy catalog: modify, then deny, then audit, inherited down the tree with exclusions, one grain call a write. The review after the merge made a rule whose test can't hold where its effect runs a `400` rather than a stored no-op, took every api-version's secrets out of what a condition reads, made a `PATCH` conditional on the body it was judged on, and drove modify, audit and the tenant boundary across the real process boundary. What stays owed — a compliance scan of resources that existed before an assignment, policy on scope writes, restore and purge, parameters and `enforcementMode` — is recorded in that section |
| Private endpoints | #47 | 1.5 | |
| Conditional access | #48 | 1.0 | |
| JIT roles | #49 | 0.5 | ◐ **The tuple with an expiry landed 2026-09-23; the eligible-to-active flow around it did not, because nothing in the plan describes one.** `RelationTuple.ExpiresOn`, applied at read time by the grains that hold the tuples, so a grant stops granting at its instant with no write; a check cache that never serves an answer past the earliest expiry that proved it, in any mode; a Leopard index that never closes over an expiring edge; a swept-and-audited end on the tuple store's own reminder; and `expiresOn` on the role-assignment `PUT`, `GET` and collection. The portal's access page sends and shows `expiresOn`, and a `PUT` that brings an end closer needs a minute's notice so a cached answer ends with it (the review's `CacheFence`). What stays owed — the PIM flow (eligibility, justification, maximum duration, approval), the widest-path index, the access column's expiry, `cyc` and the SDKs, the tenant-wide register row — is recorded in [07 § Time-bounded relations](07-rebac-authorization.md) |
| **MariaDB** | — | 0.8 | ✅ **SHIPPED AHEAD OF PHASE 4** — `CyberCloud.DBforMySQL/servers`, on mariadb-operator, with a chart and a conformance suite |
| **OpenSearch** | — | 1.0 | ✅ **SHIPPED AHEAD OF PHASE 4** — `CyberCloud.Search/services`, on the OpenSearch operator |
| **Qdrant** (vector stores) | #50 | ~~0.6~~ ⚠ **void** | ⊘ **REFUTED, not pending, and this is the direction the total is wrong in that nobody notices.** `github.com/qdrant/qdrant-operator` answers 404; what the organisation publishes is `qdrant/kubernetes-api`, the CRD types without the controller that serves them. So the 0.6 EM priced an operator that does not exist. [12 § Qdrant](12-managed-data-services.md), corrected 2026-08-18, is explicit that this is *"the one M3 item with no controller behind it"* — it would have to own clustering, sharding, replica placement, upgrade order and backup itself, in a reconciler, against a StatefulSet — and that **nothing here should be built until that estimate is redone**. The catalogue's reasoning for the row stands, so this is a re-design and not a deletion |
| Container Apps | #51 | 2.5 | |
| Virtual desktops | #52 | 2.0 | |
| CDN / http-cache | #53 | 1.0 † | |
| Resource graph API | #54 | ⚠ none | ◐ **The stream and the projection landed 2026-09-17 and the query 2026-09-18; what keeps this row from ✅ is that the address is not in the generated document.** `CyberCloud.ResourceGraph` publishes `resource-changed` on NATS JetStream from the gateway and the operation grain, and a silo-side projector writes the flattened row — with the access column [07 § ListObjects](07-rebac-authorization.md) promised — into `tenant_{id}.resource_graph` in the region's ClickHouse, idempotent on `(resource_id, version)` against real servers. `POST /tenants/{t}/providers/CyberCloud.ResourceGraph/resources` takes a KQL subset, translated to one parameterised statement with the caller's access ANDed in, and `cyc graph query` is the CLI's side. What stays owed — the address in the generated document, an `id` column, `ago`/`now`/`bin`, a stale membership slice — is recorded in [08 § The resource-graph projection](08-resource-manager.md) |
| Security posture | #55 | 1.5 | |
| GPU pools and fractional sharing | #56 | ⚠ none | |
| Placement policies | #57 | ⚠ none | |
| Region migration | #58 | 1.0 † | |
| Terraform provider | #59 | 1.5 | |
| Full webmail | #60 | 2.0 | |

† Priced **only** in this document's own cut list — **CDN and region migration**, and those two only,
appear nowhere else with a number against them. [01](01-azure-parity-catalogue.md) gives CDN a
namespace, an M3 verdict and no estimate, and [14](14-networking.md)'s `cdnProfiles` row does the same;
region migration is called *budgeted* in [04 § Failure and upgrade](04-orleans-topology.md) and
[25 § R6](25-risks-and-open-questions.md) without a number in either — and doc 04 sends the reader
**here** for it (*"— [24](24-roadmap.md), M3"*), which is what makes this page the only place it is
priced rather than merely the first. Everything else comes from [01](01-azure-parity-catalogue.md),
[12 § Effort](12-managed-data-services.md) or, for full webmail, [17 § Effort](17-communication-and-email.md)'s
*"+2.0 (M3)"* — **except the two rows the paragraph below un-daggers**, whose prices come from
[18](18-security-vault-and-malware-scan.md) and [21 § Effort](21-cli-and-sdks.md) and from nowhere in
01/12/17. ⚠ That exception is written here rather than left to the reader to notice, because the
sentence was still saying "everything else" on 2026-09-06 after the rows it was wrong about had already
been corrected below it — a footnote whose negative half is fixed and whose positive half is not is the
same defect one clause over, in the one paragraph that exists to say where numbers come from.

⚠ **This dagger stood on two more rows until 2026-09-06 and was false on both, which is worth saying
rather than quietly correcting** — provenance is the dagger's entire purpose, so a dagger on a row that
*does* have a source elsewhere is the one kind of error it cannot afford. **Security posture** is priced
outside this document twice: [18](18-security-vault-and-malware-scan.md)'s own heading
*"`CyberCloud.Security/assessments` — posture · M3 · 1.5 EM"* and the `Posture assessments | M3 | 1.5`
line in its § Effort table — and a third time in **#42**, which is the issue this table's `Issue` column
cites as its source, as *"#55 Security posture — `CyberCloud.Security/assessments` · 1.5 EM"*. The
**Terraform provider** is priced in [21](21-cli-and-sdks.md) as *"~1.5 EM even generated"* and again as
`Terraform provider | 1.5 (M3)` in its § Effort table. Both external figures equal the cut-list ones, so
no total in this document moves; what moves is where the numbers came from, and a row whose estimate has
an outside witness is a different thing from a row whose estimate has only this page.

⚠ **Thirteen of the seventeen carry an estimate; they sum to 16.9 EM against a 20 EM heading, and the
other four carry no estimate anywhere** — the policy engine, the resource graph API, GPU pools and
placement policies. That leaves 3.1 EM implied between the four, and #42 already prices the policy
evaluator alone at *"roughly 1.0 + 1.5 EM plus the engine"* when it is built once for its three subjects
and much worse when it is built three times. 1.8 EM of it has now been spent early while 0.6 of it was
never real, and marking the two shipped rows without saying that would make the 20 look better-founded
than it is.

⚠ **Corrected 2026-09-06, and it is the correction that matters more than the paragraph it sits under.**
This section published *"phase 4's headline is the one number in this plan with no derivation underneath
it"*, in bold, and that is false — the derivation is one document away and this reconciliation used that
document for six other rows without reading the table at the end of it.
[01 § Summary of scope](01-azure-parity-catalogue.md) splits the milestone budgets **top-down**:

| Milestone (doc 01) | Providers | Provider EM | Platform EM | Total | This document's heading |
|---|---|---|---|---|---|
| M1 | 12 | 20 | 26 | **46** | phases 0–2 sum to **41.5** — ⚠ the one that does *not* reconcile |
| M2 | +16 | 20 | 8 | **28** | phase 3 — **28**, exactly |
| M3 | +12 | 15 | 5 | **20** | phase 4 — **20**, exactly: 15 + 5 |

⚠ **So the 20 is 15 EM of provider work plus 5 EM of platform work over twelve providers, and what has
no derivation is something else and worse: the mapping.** The seventeen rows on this page are a
**bottom-up** list whose priced thirteen sum to 16.9, against a top-down 15 for providers alone. The
gap is not evidence of slack; the two figures are not measuring the same set. ⚠ And the 3.1 EM this
document called *implied between the four unpriced rows* lands suspiciously near doc 01's **5 EM of M3
platform work** — the policy engine, the resource graph API and placement policies are platform work
rather than catalogue rows, which would put them inside that 5 and outside the +12 providers entirely.
Not resolved here, because resolving it re-estimates two documents; recorded, because 3.1 and 5 being
near each other is either the explanation or a coincidence, and the plan should not go on treating the
question as unasked. GPU pools is the one of the four that does not fit that reading — [01](01-azure-parity-catalogue.md)
puts it **⊂ Compute** and M3, which is a provider row.

**Exit:** compliance-shaped customers can adopt (private endpoints, policy, posture, residency);
multi-region is real for at least two regions; the Terraform provider is published.

---

## What has landed — recounted 2026-09-23

Every ✅ and ◐ above comes from one list, and the list is **recounted here rather than quoted**, because
pinned counts in this tree have gone stale more than once and recently — #81 was four of them, three
sitting in the machinery that gates citation honesty, and #78's review found a pinned `grep` that was
counting its own paragraph. #45 said 22, and 22 was right until #34 published `Mail/domains` on 2026-09-12 without
recounting here — the test this section describes went red and stayed red for three days, which is
exactly the drift it exists to catch; 23 was right until #33 published the four `Communication` types
on 2026-09-15, recounting in the same change, and 27 until #32 published `Monitor/workspaces/alertRules`
the same day, again recounting in the same change; 28 until the merge of 2026-09-15 landed four
more from four branches (#30, #31, #36, #29), each of which had recounted itself to 24 against a
master that had 23 — the merge took none of their numbers and re-ran the command; 32 was right until
the merge of 2026-09-18 landed seven more from five branches (#31, #30, #32, #28 — Compute's three
on one branch), each of which had recounted itself against a master that had 32 — the merge took
none of their numbers and re-ran the command a second time; 39 was right until #39 published
`Resources/deployments` on 2026-09-23, recounting in the same change; and 40 was what this section
said when #31's branch took master on 2026-09-24 and re-ran the command, which printed 45: the table below
already listed 44 types while its **Total**, this sentence and the exit paragraph said 40, 44 or
twenty-two, and master's committed document declared 42 until `./build.sh Generate` rewrote it. 45 is
right. That is worth *establishing* rather than
assuming, and it is cheap to establish twice because two independent producers can be asked for it.

⚠ **#38 and #46 (`issue-46-policy`) conflict textually in seven files when merged**, counted by
`git merge-tree` on 2026-09-24: this file, `ProviderRegistry` and `RegistryAndDriftTests` (both
branches reserve a namespace at the same place in `ProviderRegistry.Build`), `GatewayRoute`,
`DispatchStage`, and the gateway tests' `Fakes` and `GatewayHarness` (both add a constructor parameter
and a route kind at the same line). The hunks read so far are two insertions at one place, so the
resolution keeps both sides, the gateway's route tests say whether it was right, and the count above
is re-run rather than taken from either branch.

⚠ **This branch's third-review commit, merged into master, conflicts in twelve files — and two of
them are a wire-format decision, not prose.** Master moved while the review ran: at `98b5799`
(2026-09-24) it holds #38 up to its second review (`c301aba`), #41's cost page, #46, #34 and #32,
and its own document counts 42 types. Only this commit is left to merge, and `git merge-tree
--write-tree master issue-38-billing` conflicts in `charts/bundle/bundle.yaml`, docs 22 and 24, every
file under `openapi/`, `generated/` and `portal/libs/api` that the budget's scope description reaches,
`Costs.cs` and `CostQueryGrain`. ⚠ **`CostQueryResult`'s `[Id(7)]` is master's `Granularity` (#41), so
`PricedAlone` is `[Id(8)]` here**, and the resolution keeps both and renumbers neither. `CostQueryGrain`
takes master's `Answer(grouping, granularity, …)` and this commit's order — net, decide the 404, price
what the caller may read — and passes both `granularity` and `pricedAlone`. The generated surfaces are
output: the merge runs `./build.sh Generate` and commits what it writes. Every count in this section
moves to the re-run number together, which is 42 on master today and stays 42, because this commit
publishes no type.

⚠ **Four branches recounted to 24 on the same day from the same 23, and none of them was right
after the others merged.** #30 counted `Storage/accounts/fileShares`, #31 `Network/virtualNetworks/natGateways`,
#36 `ContainerService/connectedClusters` and #29 `ContainerRegistry/feeds`, each in its own phase-3
row, this paragraph and the exit paragraph above — and each merged onto a master that already held
the earlier ones. The merge conflicted on the *prose* and not on the *numbers*: the pinned count
under the command, the gate line, "All N" and the **Total** row were changed identically on every
side and would have merged clean at 24, which was wrong by four. It was resolved by re-running the
command, not by picking a side, which is what this paragraph asks for: every count in this
section, the phase-3 row and its Total, the phase-3 exit paragraph, and the gate line quoted under
phase 2's criterion 4 move together. `RoadmapReconciliationTests` fails on the table, the Total and
the pinned number if that is skipped; the prose it cannot see is why the list is written out here.

The document, read directly:

```console
$ grep -o '"x-cybercloud-resource-type": "[^"]*"' openapi/2026-08-01.json \
    | sed 's/.*: "//;s/"$//' | sort -u | wc -l
45
```

and the build, from the other end — `./build.sh Architecture`'s **Generated surfaces** gate regenerates
every surface from `src/CyberCloud.ResourceManager/Registry/` and compares bytes, so its number is the
registry's rather than the document's:

```text
✔ Generated surfaces  Enforced  45 resource type(s) over 2 OpenAPI document(s), …
```

⚠ **What would make this stale, said plainly so it can be checked rather than trusted:** any provider's
`Describe` gaining or losing a type. Because the documents are *output* — `openapi/README.md` is
emphatic that everything there is generated and overwritten — the two numbers cannot drift apart
silently: `./build.sh Generate` moves both, and the gate fails until the regenerated documents are
committed.

⚠ **What could still go stale is this section, and that is the failure #45 actually is** — a
reconciliation done by hand is true on the day it is done, and the last one was true for weeks after it
stopped being. So the recount is no longer only prose: `RoadmapReconciliationTests`, in
`CyberCloud.ResourceManager.Contracts.Tests`, reads the published document and this page and fails when
they disagree — on the set of types, on **each phase row's count against the types that row names**, on
their sum, on the **Total**, and on the number printed under the command above. Publishing a
twenty-fifth type now turns a test red with the roadmap named in the message, rather than leaving a plan
that gets re-planned from memory — as the twenty-fourth did, on 2026-09-15, until this table was
recounted in the same commit.

⚠ **The per-row half of that was prose before it was an assertion, and only for a day.** As first
published this paragraph claimed the per-phase counts were checked when the test compared their *sum*
to 22 and nothing else — a table listing all 22 correct names with phase 2 reading 14 and phase 3
reading 5 passed every assertion in it. The assertion now exists (#45's review); the sentence above is
what it does rather than what it was hoped to do. ⚠ **And "arithmetic" means this table's arithmetic
only.** No EM figure anywhere in this document is machine-checked — not a phase heading, not a row, not
the 69.6–89.1 — and the test cannot see the `Landed` column's *judgement* at all. The date in this
heading is still what says when a person last read the rest.

All 45, against the phase that planned them. ⚠ **Written out in full, with only the `CyberCloud.`
prefix dropped, because this table is machine-checked** — `RoadmapReconciliationTests` reads it and the
published document and asserts the two sets are equal, so an abbreviated `…/subnets` would be a name no
test could match and the check would quietly become a check of nothing.

| Phase | Published types | Count |
|---|---|---|
| 1 — the deliberately trivial provider of exit criterion 1 | `Sample/widgets` | 1 |
| 2 — M1 | `ContainerService/managedClusters`, `ContainerService/managedClusters/agentPools`, `DBforPostgreSQL/servers`, `Cache/redis`, `Messaging/natsClusters`, `ContainerRegistry/registries`, `Network/virtualNetworks`, `Network/virtualNetworks/subnets`, `Network/virtualNetworks/securityGroups`, `Network/virtualNetworks/loadBalancers`, `Network/publicIpAddresses`, `Storage/accounts`, `Storage/accounts/buckets`, `Monitor/workspaces`, `Terminal/consoles`, `KeyVault/vaults` | 16 |
| 3 — M2 | `DocumentDB/accounts`, `Messaging/rabbitmqClusters`, `Messaging/kafkaClusters`, `Analytics/clickhouseClusters`, `Mail/domains`, `Mail/domains/mailboxes`, `Communication/services`, `Communication/services/channels`, `Communication/services/templates`, `Communication/services/suppressions`, `Monitor/workspaces/alertRules`, `Storage/accounts/fileShares`, `Network/virtualNetworks/natGateways`, `ContainerService/connectedClusters`, `ContainerRegistry/feeds`, `Network/virtualNetworks/peerings`, `RecoveryServices/vaults`, `Monitor/workspaces/collectors`, `Dashboard/grafanas`, `Compute/virtualMachines`, `Compute/disks`, `Compute/images`, `Resources/deployments`, `Billing/budgets`, `Monitor/workspaces/components`, `Network/virtualNetworks/applicationGateways` | 26 |
| 4 — M3 | `DBforMySQL/servers`, `Search/services` | 2 |
| **Total** | | **45** |

⚠ **`agentPools` is itself an ahead-of-phase landing that this table cannot show twice.** Phase 2's row
names node pools, so it is counted as phase 2 here — but [01](01-azure-parity-catalogue.md) verdicts
"AKS node pools / autoscaling" as **M2**, ⊂ `managedClusters`. Where the two documents disagree this
table follows doc 24, because doc 24 is the document being reconciled; the disagreement is recorded
rather than resolved, since resolving it is a catalogue change.

### What the type list cannot say

⚠ **Seven of phase 2's fifteen rows have no published type, and that is not one fact but two.** The
distinction matters because one half is an absence of evidence and the other half is evidence.

- **One names a type the catalogue has and this tree has not published** — managed identity
  (`CyberCloud.ManagedIdentity/*`). For it the list *says something*, and what it says is **not
  shipped**. It is not marked —. The Vault row was the second until `CyberCloud.KeyVault/vaults`
  was published on 2026-09-23, and is ◐ rather than ✅ for the reasons its own row gives.
- **Six name no resource type anywhere in [01](01-azure-parity-catalogue.md) and never will** —
  identity, the gateway, metering and quota, the portal subset, `cyc`/SDK packaging and platform
  hardening. A published type is positive evidence; the absence of one, for a row that was never going
  to produce one, is evidence of nothing. Five of the six are marked — for exactly that reason. The
  sixth, `cyc`/SDK packaging, is ◐ on evidence from somewhere else entirely: the **Generated
  surfaces** and **Generated SDK compiles** gates, which say what a type list cannot.

⚠ **Marked — rather than left blank**, because a blank cell in a table full of ticks reads as a tick.
Where an issue tracks one of these rows it is named on the row; where none does, the silence is itself
the finding.

⚠ **Tenancy is the case that looks like a gap and is not.** `CyberCloud.Platform/subscriptions` is an M1
row in the catalogue and is not in the 45 — because tenants, subscriptions and resource groups are
published as *scope paths* (`/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/…`) and
not as typed resources under a provider (#63). Counting it as missing would have been the easy error in
this recount, and it is written down here so the next recount does not make it.

⚠ **The policy engine (#46) is the same case, and the command above was re-run for it on 2026-09-23 and
still answers 39.** `CyberCloud.Policy/policyDefinitions` is the namespace [01](01-azure-parity-catalogue.md)
gave it, and it landed as a scope extension — `{scope}/providers/CyberCloud.Policy/…`, served beside role
assignments and outside the registry — because an assignment sits above a resource group and a rule is a
tree the registry's schema cannot express ([08 § Policy](08-resource-manager.md)). So it adds no row to the
published-types table and moves no count; its phase-4 row says what it did land.

## Running total

| Phase | EM (the phase heading, as planned) | Rows sum to | Landed or dropped | Where the number comes from |
|---|---|---|---|---|
| 0 — Prerequisites | ~~1.5~~ **1.1** | 1.1 | **0.4 ⊘** | Exact. Phase 0's heading *is* its row sum, so the dropped ADR-005 bump comes straight off |
| 1 — Spine | 14 | 14.0 | — | Not reconciled here; only exit criterion 4 was checked |
| 2 — M1 | 26 | 44.0 | **≥ 11.3 ✅** | 3.0 + 1.5 + 2.0 + 1.5 fully shipped rows, plus 3.3 of the `Network` row's split. Conservative: the partly-landed Managed Kubernetes (4.0) and Monitor (2.5) rows have no defensible split and are counted as zero. ⚠ Not conservative enough — the 3.0 row's ✅ means *published*, and #69 is open inside it; see below |
| 3 — M2 | 28 | 38.4 | **7.6 ✅** | The whole `Data` row (4.4), the whole `Communication` row (2.0, 2026-09-15), the `NAT gateways, peering` line of the `Network` row (0.8, 2026-09-18) — the one line of [14 § Effort](14-networking.md) that row has a per-noun split for, and both nouns are published — and the `Budgets, alerts, forecast` line of the `Billing` row (0.4, 2026-09-23), the one line of [22 § Effort](22-billing-metering-and-quota.md)'s four whose every noun shipped — built and tested against a real ledger, and reading an empty one in production until the sampler has a source (`the-sampler-has-no-source`). ⚠ `Billing`'s other 3.2 is counted as zero: rating and invoicing landed without dunning, the PDF or tag views, and no line of them is whole. ⚠ `Mail`'s ◐ is counted as zero: one noun of five is published and the row has no defensible split. `Observability`'s ◐ is counted as zero for the same reason — alerts are one noun of four, and the 3.0 has no per-noun split to read |
| 4 — M3 | 20 | 16.9 priced, 4 items unpriced | **1.8 ✅, 0.6 ⊘** | MariaDB 0.8 + OpenSearch 1.0 shipped; Qdrant's 0.6 void |

**Between 68.4 and 89.1 EM to M3** — where this section said **68.8–89.1** from 2026-09-18 to
2026-09-23, **69.6–89.1** from 2026-09-15 to 2026-09-18, **71.6–89.1** from 2026-09-06 to 2026-09-15,
and **~90** before that. ⚠ **The range is the finding, not a hedge**, and it is narrower
than the old single number was honest.

The arithmetic, once, so it can be checked: the plan's own 89.5 loses phase 0's dropped 0.4 outright,
which gives **89.1**. Against that sit 11.3 + 7.6 + 1.8 = **20.7 EM of rows whose every named type is
published**. Work that is finished takes zero time on *any* path, so it can only make the remaining
critical path shorter — but by **at most** its own size, and by **at least** nothing, and this document
does not say which of its rows were on the critical path in the first place. 89.1 − 20.7 = **68.4** is
therefore the floor and 89.1 the ceiling.

⚠ **That figure — 17.5 until 2026-09-15, 19.5 until 2026-09-18, 20.3 until 2026-09-23, 20.7 since — said "rows that are finished" until 2026-09-06, and this document's own annotations do not
support the word.** [§ How to read the `Landed` column](#how-to-read-the-landed-column) defines ✅ as no
more than *a published resource type in `openapi/2026-08-01.json` for every type the row names* — and
one ✅ row inside it carries a live defect three tables above: **Postgres · Valkey · NATS**, 3.0 EM,
where #69 is open against the first because the seven-day recovery window returns an `initdb`. A
published type is not a working restore, and the same caution that counted Managed Kubernetes (4.0) and
Monitor (2.5) as **zero** should not have skipped a row this page had already qualified. ⚠ **The floor
survives and it is worth saying why rather than leaving it to be re-derived:** subtracting *more* than
is truly finished can only push the result *down*, so 68.4 remains a valid lower bound — it is simply a
weaker one than it looked, and the ✅ column is a claim about the published document rather than about
the feature.

⚠ **Naming what makes it a range is worth more than picking a point inside it.** Phases 2 and 3 quote
26 and 28 while their rows sum to 44.0 and 38.4; phase 2 says in a parenthetical that its 26 is the
critical path, and phase 3 says nothing. ⚠ **And "critical path" is this document's reading of those
headings rather than a fact about them** — [§ Phase 3](#phase-3--m2-a-catalogue-that-is-a-business--28-em)
now records the competing one: 28 and 20 are [01 § Summary of scope](01-azure-parity-catalogue.md)'s M2
and M3 milestone totals exactly, so the headings may be a top-down budget that the bottom-up rows were
never inside. Either way the gap was invisible while nothing had shipped and is load-bearing the moment
anything does, and either way it is the reason 20.7 EM of completed work cannot simply be subtracted.
**Closing the range is a scheduling exercise on the first reading — say which rows are on the path — and
a re-estimate on the second.**

⚠ **A second published total sits one link away and this section did not mention it.**
[01 § Summary of scope](01-azure-parity-catalogue.md) still reads **~94 EM to M3** — 46 + 28 + 20 from
its own milestone table — against the 89.1 here, in the same paragraph that sends the reader to this
document for the sequencing and the cut list. The gap is entirely M1: doc 01's M2 (28) and M3 (20) are
phase 3's and phase 4's headings to the digit, while its M1 total of 46 sits against phases 0–2's
1.5 + 14 + 26 = 41.5. Recorded rather than resolved, exactly as the `agentPools` disagreement above is —
moving either number is a re-estimate of a document this issue did not reconcile. ⚠ **Its months line
is not the failure this one had**, and saying so is the point of checking rather than assuming: doc 01
says *"4–5 engineers for about eighteen months"*, and 94 ÷ 5 = 18.8, so that figure divides. What it
omits is the other end of its own range — 94 ÷ 4 = 23.5 — which is the same understatement of the slow
end, arrived at honestly.

⚠ **And the ceiling is not a ceiling.** Qdrant's 0.6 is out of the 20 as an *estimate*, not as work:
[12 § Qdrant](12-managed-data-services.md) says the row must be re-costed against what it actually
names, and four more phase-4 items — the policy engine, the resource graph API, GPU pools, placement
policies — carry no estimate at all. So the figure is wrong in **both** directions at once, which is the
state a total reaches when it is only ever corrected downward.

At 4–5 engineers that is **roughly 14–22 months** — 68.4 ÷ 5 = 13.7 at the fast end, 89.1 ÷ 4 = 22.3 at
the slow one. ⚠ The old line said *"roughly 18–20 months"* for 89.5 EM, and 89.5 ÷ 4 is 22.4: **the
upper end was already understated by more than two months before any of this reconciliation**, because
it was carried over rather than divided. That is the same failure as the stale phase rows above, in the
one number a reader is most likely to repeat out loud.

The estimate is load-bearing on two assumptions, both stated so they could be checked early, and **both
have now been checked**:

1. **That the operator selections in [12](12-managed-data-services.md) hold up without a fork —
   holding, with one exception that is worse than a fork.** All 23 charts under `charts/managed/` carry
   the `SOURCE` file [R4](25-risks-and-open-questions.md) asks for and every one of them records
   `vendored: none`: nothing upstream has been forked, because nothing upstream has been copied — these
   charts render somebody else's CRDs and were written here. ⚠ The exception is Qdrant, and it fails the
   assumption in a way the assumption did not anticipate: the risk was priced as *"0.5–1 EM per forked
   chart, forever"*, and an operator that **does not exist** is not a fork, it is a reconciler this
   platform would have to own outright.
2. **That phase 1 actually delivers the generation pipeline — delivered.** [§ Phase 1](#phase-1--the-spine--14-em)
   has the two gate lines. ⚠ With the caveat recorded there rather than hidden here: generated,
   byte-compared and compiling is not the same as packaged, and #79 is open.

The original clause — *"if either fails, the number is 30 % worse and the cut list below is how that is
absorbed"* — stands as written, and **neither has failed in the way it was written to catch.** ⚠ That is
the useful half of this reconciliation and it should not be read as reassurance: assumption 1 held for
all twenty-one charts that exist and broke on the one service that has none, in a mode nobody costed,
and the cut list is a weaker backstop than the sentence implies. The arithmetic for that is under
[§ The cut list](#the-cut-list-in-cutting-order), where two `Saves` figures are now struck.

## The cut list, in cutting order

Written now, so that under pressure the decision is a lookup rather than an argument.

| # | Cut | Costs | Saves |
|---|---|---|---|
| 1 | Virtual desktops | A me-too feature nobody has asked for | 2.0 |
| 2 | Container Apps | ⊂ Kubernetes; the tenant can install Knative | 2.5 |
| 3 | Full webmail (keep IMAP + minimal client) | Polish, not capability | 2.0 |
| 4 | CDN / http-cache | Honest anyway — we have no PoPs | 1.0 |
| 5 | MariaDB, OpenSearch, Qdrant | Catalogue breadth. ⚠ **Reduced to Qdrant on 2026-09-06 — two thirds of this row is already built** (`DBforMySQL/servers`, `Search/services`), so cutting them is a *removal*, not a saving: the 1.8 EM is spent and does not come back. And the 0.6 left is Qdrant's void estimate (#50), so what this row saves is a number nobody can currently state | ~~2.4~~ **0.6, and that 0.6 is not trustworthy** |
| 6 | Terraform provider | Enterprise adoption friction. ⚠ Cutting this hurts more than it looks | 1.5 |
| 7 | ~~**The whole Mail module**~~ | ⚠ **Removed from the cut list 2026-08-11.** The reason it was cuttable was "an abuse-desk commitment we may not want"; the desk is now staffed, so the commitment is made and the module is in. Cutting Mail *after* staffing an abuse desk would be paying the operational cost and keeping none of the differentiation | ~~3.5~~ |
| 8 | Security posture | A nice-to-have score | 1.5 |
| 9 | Region migration | One region until someone pays for two | 1.0 |
| 10 | Kafka | Strimzi is the heaviest to operate; NATS covers most needs. ⚠ **The row survives and its saving does not, as of 2026-09-06.** `Messaging/kafkaClusters` is published, so the 1.2 EM is spent — cutting Kafka now removes a shipped resource type from the catalogue and returns **no engineering months at all**. What is still real is the *operational* argument this row was written on, and that is a different decision with a different cost line: it belongs to whoever runs Strimzi, not to this table | ~~1.2~~ **0** |

⚠ **What this list is now worth, added up on 2026-09-06 — because a cut list is a number before it is a
list, and this one had never been totalled.** The nine live rows saved 2.0 + 2.5 + 2.0 + 1.0 + 2.4 + 1.5
+ 1.5 + 1.0 + 1.2 = **15.1 EM** before this reconciliation. Rows 5 and 10 have shipped in part or in
whole, so the same nine rows now save 2.0 + 2.5 + 2.0 + 1.0 + 0.6 + 1.5 + 1.5 + 1.0 + 0 = **12.1 EM**.

⚠ **Set that against what the list was written to absorb and the shortfall is not marginal.** [§ Running
total](#running-total) offers this list as the answer to the plan being *"30 % worse"*, and 30 % of the
old 89.5 EM is 26.9 — so the list could cover 56 % of that contingency when it was written and covers
45 % now. **The cut list has never been able to absorb the overrun it is named as the answer to**, and
shipping catalogue rows makes that steadily worse rather than better, because every row that ships moves
from the *cut* column to the *spent* column and can never move back. ⚠ **This is an argument for cutting
earlier, not for cutting more** — a cut decided after the work is done is not a cut, and rows 5 and 10
are what that looks like on paper.

**Never cut, at any pressure:** the resource manager, ReBAC, the isolation suite, the storage-tier
discipline, the label discipline, the metering *events*, the wire-compatibility gate, the conformance
suite. Every one of them is cheap now and unaffordable to retrofit — which is precisely the property
that makes something look like a good cut under deadline pressure and be the wrong one.

## The three decisions that must be made before phase 2 starts

Each is a business decision with an engineering consequence, and each blocks work if it is left open.
**All three are now answered — two on 2026-08-11, the third on 2026-08-20.**

1. ✅ **Public authoritative DNS: we run it.** ([14](14-networking.md)) Anycast nameservers are in the
   infrastructure plan. The provider is 1.5 EM; ⚠ the *operations* are the cost, and that half is now
   a standing commitment — DDoS absorption, and being the reason a customer's whole business is
   offline when it breaks.
2. ✅ **The abuse desk is staffed, so `CyberCloud.Mail` is built.** ([17](17-communication-and-email.md))
   3.5 EM stays in M2 and comes off the cut list's seventh row. ⚠ The commitment that comes with it is
   operational and starts before the first domain sends: `abuse@` monitored by a human **with the
   authority to suspend a tenant within the hour**, feedback loops registered per outbound IP, RBL
   monitoring alerting to on-call, and a ~4-week warm-up on every new address. Skipping the warm-up
   gets the IP blocked in a day. And the cheap thing that is easy to forget:
   **the platform's own transactional sending IPs are separate from tenant sending IPs**, so a
   tenant's reputation problem cannot take down our OTPs.
3. ✅ **No LINBIT support contract. The platform runs LINSTOR and DRBD unsupported, the way Cozystack
   does.** (ADR-011) ⚠ The licences were never the question — GPL-2/GPL-3 restrict distribution, not
   use, so *running* LINSTOR and DRBD as a service is fine either way, and ADR-011's own footnote said
   so before this row was written. What was being priced is **support and indemnity on a synchronous
   block-replication layer sitting underneath customer data**, where a bad failover is a data-loss
   event rather than a slow page. That is a purchase, not an obligation, and the answer is no. What
   changed to make it affordable is [ADR-020](02-technology-decisions.md) — **Talos Linux as the node
   OS**, so DRBD is a signed system extension rather than a module built from source on each node at
   boot. See [§ The replicated-storage switch](#the-replicated-storage-switch), which is what is left
   of this row: the contract question is closed, the *when do we replicate* question is not.

### The replicated-storage switch

⚠ **This section used to be *The LINBIT decision, deferred*, and the decision it deferred is now
made — see item 3 above.** What survives is the half that was never about a contract: **the platform
ships with single-replica local storage on and replicated storage off, and something has to say when
that flips.**

**Where it stands today.** `charts/bundle/openebs-localpv/` is the bundle's default storage
class: one replica, node-local, no DRBD and no kernel module. That is deliberate and it is what makes
[09 § phase 0](09-kubernetes-fabric.md) possible at all — phase 0 installs the platform onto *an
existing cluster we did not build*, and a storage component that hard-requires a kernel extension
fails on any host that is not ours.

**What being on the default costs, so it is a decision and not an omission.** LINSTOR is what
[15 § Block storage](15-storage-blob-file.md) and [13 § Virtual Machines](13-compute-vm-containers.md)
name for KubeVirt disks, and `kubevirt-csi` in [09](09-kubernetes-fabric.md) is specified against it.
Running without it means accepting **no replication** — a node loss loses that node's volumes, and the
VMs on them do not come back. That is acceptable for design partners, who are on manual contracts
([22 § Effort](22-billing-metering-and-quota.md)) so that the blast radius of a storage incident is a
conversation rather than a claim. It is **not** acceptable at GA. The alternative alternative, Ceph, is
not cheaper: [15 § Object storage](15-storage-blob-file.md) already warns it "is an order of magnitude
more operational work — a Ceph cluster is a full-time role".

**The trigger, written down so it is not missed:** before any customer who is *paying* has data on
block storage that must survive a node loss. Throwing the switch is not one commit, and the parts are
listed in `charts/bundle/openebs-localpv/component.yaml` § the replicated stage rather than
here, because that is the file somebody edits to do it.

⚠ **The one part of the switch that is not a Kubernetes change.** Replicated storage means DRBD on the
node, and DRBD on the node means [ADR-020](02-technology-decisions.md)'s Talos machine configuration
carries the `drbd` system extension. That is a **reprovision of the node**, not a package install —
Talos has no package manager and no shell. So the switch has a lead time measured in node reboots, and
the cheapest time to have decided it is before the fleet is built.
