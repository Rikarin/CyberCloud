# `node-image/` — building the container disk a worker VM boots

`charts/managed/kubernetes-agentpool` renders `nodeImageRepository` and a tag into a
`KubevirtMachineTemplate`. **The tag has to exist.** This directory is how one gets made, because
nobody upstream makes them any more.

## The supply gap, re-read rather than repeated

`charts/managed/kubernetes/conformance.yaml` § owed, `offered-minors-are-out-of-support`, established
this on 2026-08-19. Re-read firsthand on **2026-09-03** through the quay tag API
(`/api/v1/repository/capk/ubuntu-2404-container-disk/tag/`), and nothing has changed:

| Tag | Pushed |
|---|---|
| `v1.34.1` | 2025-09-27 |
| `v1.33.5` | 2025-09-27 |
| `v1.32.1` | 2025-01-22 |
| `v1.31.5` | 2025-01-21 |

`has_additional: false` — that is the whole repository. **Eleven months with nothing pushed**, one
tag per minor rather than one per patch, and the newest minor it offers is 1.34, which Kubernetes
ends on 2026-10-27. The two minors worth spending an immutable api-version on — 1.35 (to 2027-02-28)
and 1.36 (to 2027-06-28) — have no container disk in that namespace or any other under `capk`.

> ⚠ **This is a supply problem and not a pin problem, and that inversion is the point of issue #24.**
> `ManagedClusters.Versions`, `PinnedPatch` and the chart's enum are unchanged on purpose. Offering a
> different minor set is a **new api-version**, `2026-08-01` is immutable, and the compatibility gate
> refuses a narrowing in place — so the order is *image first, api-version second*, and this
> directory is the first half.

## What upstream does and does not automate

Cluster API Provider KubeVirt's `docs/our-image-creation-process.md` points at
`kubernetes-sigs/image-builder`, and CAPK's own `.github/workflows/image_build.yaml` builds **manager
images**. There is no automated container-disk build anywhere in CAPK, which is why the tags above
are frozen: somebody used to run it by hand and stopped.

image-builder itself has everything needed, and it is less machinery than it sounds. Read at tag
**`v0.1.55`** (2026-07-13, commit `7ffb9b7f1f26cd66891874463cc9411e3633325f`) on 2026-09-03:

* `images/capi/Makefile` derives `build-kubevirt-<flavour>` targets from `QEMU_BUILD_NAMES`, which
  lists `qemu-ubuntu-2404`. So the target is **`build-kubevirt-qemu-ubuntu-2404`**.
* Each one runs `packer build … --var 'kubevirt=true'`.
* That flag turns on one post-processor, and the whole of it is
  `packer/qemu/scripts/build_kubevirt_image.sh`: it writes a two-stage Dockerfile whose second stage
  is `FROM scratch` with the qcow2 copied to `/disk/`, and runs `docker build`. **A container disk is
  a scratch image with a disk image in it and nothing else.**
* `packer/config/kubernetes.json` defaults to `kubernetes_semver: v1.36.1`, `kubernetes_series:
  v1.36`, `kubernetes_deb_version: 1.36.1-1.1`. So building 1.36 is the *default*, not an override.

> ⚠ **`packer/qemu/packer.json` is generated, and a recipe that told you to edit it would be wrong.**
> The repository holds `packer.json.tmpl`; the Makefile's `set-ssh-password` prerequisite runs
> `hack/set-ssh-password.sh`, which renders every `*.tmpl` with a **random 16-character SSH
> password**. Two builds of the same inputs therefore differ, so the disk is reproducible in content
> and not bit-for-bit — record the digest, do not expect to re-derive it.

> ⚠ **Overriding the version is three values, not one.** `kubernetes_semver` is what the image
> records, `kubernetes_series` selects the `pkgs.k8s.io` repository, and `kubernetes_deb_version` is
> what apt installs. Setting only the first produces a node whose tag claims one minor and whose
> kubelet is another — quieter and worse than the missing image this directory exists to fix.
> `build.sh` sets all three together and prints them.

## Using it

```bash
./build.sh                                        # resolve every input, build nothing
./build.sh --patch v1.35.7                        # what it would take to build 1.35 instead
./build.sh --repository <registry>/<repo> --build # ~30 min, needs packer + qemu + docker
./build.sh --repository <registry>/<repo> --build --push
```

The default mode is the verification, and it runs today:

* the pinned image-builder ref resolves to a commit;
* that ref's Makefile really has the KubeVirt targets, and `QEMU_BUILD_NAMES` really lists the
  flavour;
* the Kubernetes version the build would produce, read out of that ref's own config rather than
  written here;
* whether the tag already exists in the repository you named;
* which of packer, qemu, ansible, docker are missing locally.

> ⚠ **What the verification does NOT establish, said plainly because the gap is the whole risk.**
> **The packer run has not been executed in this repository.** Every input above is checked on every
> invocation; the build needs a QEMU-capable host, about half an hour and roughly 4 GB, and no lane
> here has one. The steps in `build.sh` are transcribed from the pinned ref's own Makefile and
> post-processor, not invented — but transcribed is not run, and `charts/managed/kubernetes-agentpool/
> SOURCE` already makes the matching point about registry reads: *"a registry read is not a running
> node"*. Neither is a resolved input a booted VM.

> ⚠ **A defect this script's own verification found, worth repeating because it is generic.** The
> Makefile check was first written `printf '%s' "$makefile" | grep -q …`. `grep -q` exits at the
> first match; the Makefile is 74 KB; the writer is killed by `SIGPIPE` and exits 141; and under
> `set -o pipefail` that becomes the pipeline's status. So the check reported *"that ref's Makefile
> has no KubeVirt build targets"* about a Makefile that has three — **intermittently**, depending on
> buffering. Two runs a minute apart disagreed. It is now bash pattern matching with no pipe.

## Hosting is owed

**There is nowhere to push this.** `build.sh` has no default `--repository`, and that absence is the
honest state rather than an omission: this platform has no registry of its own, which is issue #25's
territory. Until there is one, the reachable outcome is a container disk on the machine that built
it, and a container disk that is not in a registry is not a node image — a `KubevirtMachineTemplate`
cannot reference it.

So the sequence, and none of it may be reordered:

1. **A registry.** (#25.)
2. **Build and push a disk for a supported minor.** This directory.
3. **Verify it boots**: a `KubevirtMachineTemplate` against it, a node that joins, and a kubelet
   whose version is the tag's. Nothing here can do that — `charts/managed/kubernetes/conformance.yaml`
   § owed, `a-green-cluster-suite-proves-the-apply-path-only`, is where that limit lives.
4. **Then** a new api-version offering the minor. Not before: an api-version is immutable, and one
   cut against an image that does not boot cannot be withdrawn.

See [docs/plan/13](../../../../docs/plan/13-compute-vm-containers.md) § Managed Kubernetes,
[docs/plan/09](../../../../docs/plan/09-kubernetes-fabric.md) § Kubernetes in Kubernetes, and
`charts/managed/kubernetes/conformance.yaml` § owed, `offered-minors-are-out-of-support`.
