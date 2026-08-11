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
   │  SignalR /hubs/terminal  — binary protocol frames
Gateway pod
   │  grain call, streaming
ITerminalSessionGrain   (hot tier, one per active session)
   │  Kubernetes exec  (SPDY/WebSocket) via the cluster connection
Pod  cybercloud-shell   in the tenant's namespace, in the tenant's cluster
   └─ PVC  home-{userId}   5 GB, retained 90 days after last use
```

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

**The session grain** owns: the pod's lifecycle, the exec stream, the resize channel, an idle timer
(20 min → terminate the process, keep the PVC), a hard cap (8 h), an output ring buffer for reconnect,
and the audit record. It is hot-tier because a lost session is a reconnect, not a data loss.

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
