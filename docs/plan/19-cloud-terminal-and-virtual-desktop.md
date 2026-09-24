# 19 — Cloud Terminal and Virtual Desktop

Two products from the brief that share a substrate: a container running in the tenant's subscription
with a persistent home, reached from the browser.

## `CyberCloud.Terminal/consoles` · M1 · 1.5 EM

Azure Cloud Shell's shape. It is an M1 provider despite not being infrastructure, for two reasons: it
is the thing that makes the portal feel like a cloud rather than a CRUD app, and it exercises the
SignalR path, the per-user PVC path and the managed-identity path before anything expensive depends on
them.

### Architecture — and one correction to the brief

```
Browser (xterm.js in the portal)
   │  POST …/consoles/{name}/connect, then SignalR /hubs/terminal?ticket=…  — JSON hub protocol, bytes as base64
Gateway pod
   │  connect: relayed to IClusterActionGrain on a silo, which applies the pod
   │  hub: grain calls in (Attach · Send · Resize), a grain observer back (Output · Ended)
ITerminalSessionGrain   (no storage, one per shell pod, keyed by the pod's UID)
   │  pods/attach over WebSocket (v4.channel.k8s.io), allowed by the cluster connection grain
Pod  {name}-shell   in the console's namespace, in the console's cluster
   └─ PVC  {name}-home   5 GB, retained 90 days after last use
```

⚠ **How the browser gets onto the hub.** A WebSocket carries no `Authorization` header, and the
portal's token never goes in a URL. `connect` returns the session id and the hub path; the portal then
asks the gateway for a short-lived ticket for that hub (`POST /hubs/terminal/ticket`, with the token in
the header), opens `/hubs/terminal?ticket=…`, and calls `Attach` with the session id and the pane's
size — [10 § SignalR](10-gateway-and-api.md#signalr) has the ticket's rules. The three hub methods
are `Attach`, `Send` and `Resize`; the two callbacks are `Output` and `Ended`; bytes travel as base64 on
the JSON hub protocol. **Reconnect from the portal's side is the same `connect` again with a fresh
ticket** — the handler applies the pod rather than creating it, so a live shell answers with the same
session id and the hub replays; a different id means the pod was reclaimed in between, and the pane
says so. ⚠ **`Ended` is the one close that is not reconnected.** The hub sends it when the shell
exited, sat idle past its timeout or was terminated, and a reconnect then would be a `connect` that
starts the very pod the idle reclaim stopped, for a tab nobody is looking at.

⚠ **What exists of this diagram, stated so the picture is not read as more than it is.** All of it,
since issue #22 (2026-09-23): the pod, the `connect` and `terminate` actions, the hub, the ticket, the
portal's pane, and the session grain in the middle, `CyberCloud.ResourceManager § TerminalSessionGrain`,
attaching to the pod through `IKubeClusterConnection.AttachAsync`. ⚠ The grain is the platform's and
not the provider's: it re-asks ReBAC and opens a cluster stream, the two seams
[03 § Assembly graph rules](03-repository-layout.md) rule 8 forbids a provider to name, so `connect`
registers its session through `ActionContext.Terminals` as `listInstallCommand` reaches the agent
tunnel through `ActionContext.Agents`. It runs under test end to end:
`CyberCloud.Gateway.Host.Cluster.Conformance § TerminalOverTheGatewayTests` creates a console on a
real k3s, calls `connect` through the gateway's eight stages and a resource manager composed as the
gateway's is — no cluster connection, the action relayed to the silo — opens `/hubs/terminal` with a
ticket, and asserts an `echo` round-trips, `stty size` reads back a resize, a reconnect is replayed
the ring, an idle shell is reclaimed with its home volume kept, and another person, another tenant and
a revoked role are refused. The silo there reaches k3s through the cluster connection grain, so the
attach takes the production path: the session grain, the dialer, the connection grain's tenancy check,
and a client built from the connection's descriptor. ⚠ The gateway and the silo share one process in
that suite, as every `TestCluster` does, so the crossing has a suite of its own:
`CyberCloud.AppHost.Tests § TerminalOverTheRealHostsTests` runs connect, attach, keystrokes, output and
terminate from a gateway process into the AppHost's two silo processes. Five choices the diagram records, each
against the obvious alternative:

- **`connect` runs on a silo, not in the gateway that received it.** A synchronous action normally
  runs in the process serving the request, and the gateway can't reach a cluster: it composes no
  cluster connection, and the cluster connection grain refuses a client caller because its tenancy
  check can only see a calling grain's tenant. So the gateway's `ActionDispatcher` relays an action on
  a `RequiresCluster` type to `IClusterActionGrain`, a tenant-qualified worker on a silo, which runs
  the handler there as that tenant — the same caller a reconcile pass is. Before the relay existed,
  every such action was refused in a deployed gateway, `connect` included, and every harness passed
  because each handed its dispatcher a direct connection.

- **The arrow back to the gateway is a grain observer, not an Orleans stream.** One producer and one
  consumer, and a terminal needs what a direct call gives: order (each delivery is awaited before the
  next), backpressure (a slow socket slows the read from the kubelet), and a delivery that fails at
  once when the gateway pod holding the socket has gone. A stream would add a provider and a pub-sub
  registration between one grain and one socket — the fan-out the three live-update hubs need and a
  terminal does not. `ITerminalViewer`'s remarks carry the argument.
- **"Kubernetes exec" is an *attach*.** The pod's one process is `bash -l` with a TTY; attaching joins
  it, so `exit` ends the pod (`restartPolicy: Never`) and a dropped stream does not lose the shell's
  state. The protocol is `v4.channel.k8s.io`, because that is what KubernetesClient 19.0.2 dials; v5's
  close frame for standard input is for an exec that must see end-of-file, which a shell does not.
- **The socket is opened where the session is, not in the cluster connection grain.** An attach is a
  stream and a grain method returns a message, and routing every keystroke of every shell on a
  cluster through its one connection activation would make that grain the bottleneck. The connection
  grain *decides* (`AuthorizeAttachAsync`, its tenancy check unchanged) and the calling silo dials
  (`IKubeAttachDialer`). An agent-connected cluster cannot be attached to yet — the tunnel carries one
  response per request.
- **The session belongs to the person who opened it, and that outlives the grain.** `connect` binds
  it to its caller (`ActionContext.Caller`, a fact rather than a decision); every hub call must come
  from that person, and an attach re-asks ReBAC for `connect`, fully consistent. A second person with
  `connect` on the same console gets `409` from `connect` and nothing from the hub. The owner is
  written down twice: in the hot tier, for the next activation, and on the pod as
  `cybercloud.io/session-owner`, stamped when `connect` creates it and never rewritten. A grain with no
  record reads the stamp before it binds anyone. ⚠ Until the second review of #22 the owner lived
  only in the activation, and a silo restart, a rolling deploy or a rebalance handed the
  still-running shell to the next colleague who called `connect`.

⚠ **Only the pod ends a session.** A shell that exited, idled out, never started or was terminated
ends it; failing to *reach* the shell — an attach refused, a cluster that stopped answering, a stream
that keeps dropping — leaves the session waiting, tells the pane, and retries on the next attach or
keystroke. The session id is the pod's UID, so a session ended over a pod that still runs would be
named by every later `connect`; `connect` answers an ended session by deleting its pod and starting a
new one, which is the backstop for a reclaim whose delete never reached the cluster.

What is still owed is in `charts/managed/cloud-shell/conformance.yaml § owed`: no terminal over the
agent tunnel, no idle sweep once a session grain's activation is lost, keystrokes not re-checked, two
narrow gaps in where the owner is recorded (`the-owner-has-two-homes-and-one-race`), and the image and
the managed identity below.

> ⚠ **The brief says "SignalR endpoint which spins up grain with ssh client to the docker".** SSH is
> the wrong transport here and it is worth saying why: it means running `sshd` in the shell image
> (another network listener, another credential, another attack surface), managing host keys and
> user keys, and reaching the pod over the network. **`kubectl exec`'s streaming API does all of it
> already** — authenticated by the cluster connection we already hold, no listener in the container,
> no keys to manage, and a native multiplexed stdin/stdout/stderr/resize protocol. The grain speaks
> the exec protocol directly.
>
> SSH stays relevant for a *different* feature: reaching a **VM** ([13](13-compute-vm-containers.md)),
> where there is no Kubernetes API to exec through. That path is bastion-shaped and is M2.

**The session grain** owns: the attach stream, the resize channel, an idle timer (20 min → delete the
pod, keep the PVC), an output ring buffer for reconnect (64 KiB), and the session's record. ⚠ **Three
things this sentence used to give it that it does not hold, each deliberately.** The pod's *creation*
is `connect`'s, so a shell exists because a person asked and not because a grain decided. The hard cap
(8 h) is the kubelet's `activeDeadlineSeconds`, which holds when nothing of the platform is running.
The *audit record* is a silo log line — who, which console, which cluster, why it ended — because no
audit sink exists (`no-audit-sink`). ⚠ **Its hot-tier state is two facts and nothing else**: whose
session it is and whether it has ended. This paragraph said "no storage at all, because the pod is the
durable half" until the second review of #22 showed what that cost: the pod is durable, and who owns
it was not. A lost activation still loses the ring and the open stream, both reconnect-shaped, and the
next `connect` by the owner re-registers the same session id. It also loses the idle clock, until that
`connect` or the hard cap; a sweeper that does not depend on an activation is
[§ Shared machinery](#shared-machinery)'s second item and is owed.

**A tenant's live shells are capped** (`TerminalSessionLimits.LiveSessionsPerTenant`, ten), counted per
session rather than per console because a session is what costs; slots are leased so a lost session
grain frees its slot. The number is a constant, not a quota — [22](22-billing-metering-and-quota.md)'s
meters reserve from a resource body, and a session is not a write.

**Reconnect** replays the ring buffer. A dropped Wi-Fi connection resuming into a live shell is the
difference between a feature people use and one they do not.

### The image

Per the brief, plus what is actually needed. One image, ~2.5 GB compressed — large, and the right
trade, because a shell that lacks the tool you need is worthless and a lazily-installed tool needs
network egress from a locked-down pod.

| Group | Contents |
|---|---|
| Shells | `bash`, `zsh`, `sh`, `pwsh`, `tmux` |
| Editors | `vim`, `nano`, `emacs` (`-nox`) |
| Cloud | `cyc` (ours), `kubectl` + `kubectx`/`kubens`, `helm`, `k9s`, `stern` |
| IaC | `terraform`¹, `opentofu`, `ansible` |
| Build | `make`, `maven`, `gradle`, `npm`, `pnpm`, `yarn`, `pip`, `uv`, `dotnet` |
| VCS | `git`, `gh`, `glab` |
| DB clients | `psql`, `mysql`/`mariadb`, `redis-cli`/`valkey-cli`, `mongosh`, `clickhouse-client`, `nats` |
| Languages | `dotnet` 10, `node` 22, `python` 3.13, `go`, `java` 21, `ruby`, `rust` |
| Net | `dig`, `curl`, `wget`, `jq`, `yq`, `nc`, `mtr`, `tcpdump`², `ssh`, `rsync` |

¹ ⚠ BUSL — shipping the binary in an image we distribute needs a licence read. `opentofu` (MPL-2.0) is
the safe default and `terraform` is included only if that read clears.
² Requires `NET_RAW`, which the pod does not have by default. It is present and it will fail without
an elevated session — documented rather than silently absent.

**Two variants:** `default` and `minimal` (~400 MB, shells + `cyc` + `kubectl` + editors), because a
40-second cold start for someone who wants to run one command is the wrong trade.

⚠ **Nothing in the repository builds either image yet, and the platform now takes the digest as a
deployment input.** `CyberCloud:Terminal:Images:Default` (and `:Minimal`, falling back to it) is set in
the gateway's and the silo's configuration the way the host images' digests reach `bootstrap.sh`, and
`connect` refuses anything not pinned `@sha256:`. Unset, visible placeholder digests stand and the pod
fails to pull by name. The build route is a decision the repository has not made —
`build/Build.Images.cs` forbids a Dockerfile, and the SDK's container tooling cannot install `psql` or
`kubectl` — and `charts/managed/cloud-shell/conformance.yaml § owed`, `no-image-pipeline`, says so. The
cluster-backed suite runs PostgreSQL's Alpine image by digest through the same setting: `bash`, `stty`
and `psql`, which is what the session machinery needs and nothing this table promises.

### The pod

| Property | Value | Why |
|---|---|---|
| Namespace | The tenant's `cybercloud-shell` namespace, in the subscription's cluster | Per the brief: it runs in the tenant's subscription and is billed to it |
| Identity | The invoking user's managed identity | `cyc` and `kubectl` work with no stored credential — this is the feature |
| Resources | 0.5–2 vCPU, 1–4 GB, ephemeral storage capped | |
| Security | Non-root, read-only root filesystem except `$HOME` and `/tmp`, no privilege escalation, seccomp `RuntimeDefault`, dropped capabilities | |
| Network | ⚠ **Inside the tenant's VPC** — that is the point (reach your database) — with a `NetworkPolicy` denying access to the platform's own namespaces | |
| `$HOME` | PVC, 5 GB, quota-enforced, snapshotted weekly, retained 90 days after last use | Per the brief |
| Egress | Allowed, metered | A shell that cannot `git clone` is not a shell |

⚠ **The identity row is not built, and precisely four pieces are missing** — which is why the M1
story's "`psql` into it using a managed identity" does not yet run from this shell
(`the-shell-identity-cannot-reach-postgres`): the pod mounts no projected service-account token for the
identity host's audience (it sets `automountServiceAccountToken: false` and renders no projection);
nothing binds the console's `identity.principalId` to `(cluster, namespace, {name}-shell)` — the
binding [11 § Managed identity](11-identity.md) step 2 describes, which a provider cannot write; `cyc`
has no token-exchange login and no `postgres connect`; and a PostgreSQL server authenticates with the
password `listKeys` returns, so the route is `cyc` → exchange → `listKeys` → `psql`, every hop of it
after the first needing `cyc`. The exchange itself exists on the identity host.

⚠ **Idle cost is the design constraint.** A million users with an idle shell pod each is a million idle
pods. The pod is deleted after 20 minutes idle and re-created on next connect (~8 s warm image); only
the PVC persists. The portal says "reconnecting" rather than pretending the session never ended.

### Auditing

Every session records: who, when, from where, which subscription, which cluster, duration. **Command
content is not recorded by default** — it is a shell, it contains secrets, and a keystroke log is a
liability. An opt-in per-subscription full-session recording exists for tenants with a compliance
requirement, and it is loud in the UI when it is on.

## `CyberCloud.DesktopVirtualization/workspaces` · M3 · 2.0 EM

Ubuntu with a desktop, in a container, in the browser. From the brief.

```
Browser  →  our client (Guacamole protocol over WebSocket)
         →  guacd  (protocol translation)
         →  Pod: Ubuntu + XFCE + xrdp        ← or a KubeVirt VM for a full desktop
              └─ PVC /home/{user}
```

**Apache Guacamole's `guacd`** for protocol translation (RDP/VNC/SSH → the Guacamole wire protocol),
with **our own web client** rather than Guacamole's Java web app — because their client brings a
Tomcat application, its own auth and its own theme, and we already have a portal.

| Decision | Choice |
|---|---|
| Container or VM | **Both.** A container desktop is cheap and dense and is right for a dev workstation; a VM is right for anything needing a real kernel, GPU, or Windows. Same resource type, a `hostKind` property |
| Session model | Personal (a dedicated desktop per user, persistent) or pooled (M3+, from a template) |
| GPU | ⊂ [13](13-compute-vm-containers.md)'s GPU work; a desktop is the most compelling use of fractional GPU sharing |
| Peripherals | Clipboard, printing and file transfer through Guacamole. ⚠ USB redirection is **not** offered — it needs a native client and is a support burden |
| Audio | Best-effort over RDP. Named as best-effort, because it is the most common disappointment |

**Why M3.** Nothing depends on it, it is the least differentiated item in the catalogue (there are
good products in this space), and its cost is dominated by per-user desktop images and licensing
questions rather than by platform work. It also benefits from the cloud terminal's PVC lifecycle, image
pipeline and idle-reclaim machinery already existing and being proven.

## Shared machinery

Both features need the same four things, and building them once for the terminal is why the desktop is
2.0 EM and not 4.0:

1. **Per-user PVC lifecycle** — create, quota, snapshot, reclaim after N days idle.
2. **Idle reclamation** — a reminder-driven sweeper that deletes compute and keeps state.
3. **Browser ↔ pod streaming** over the gateway with reconnect and backpressure.
4. **Image build and distribution** — a large, layered, signed image, mirrored to each region's
   registry so a cold start is a local pull.
