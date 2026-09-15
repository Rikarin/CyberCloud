# `deploy/vm-lane/` — the nodes k3s-in-Docker cannot be

[docs/plan/23 § The lane that needs a kubelet](../../docs/plan/23-build-ci-and-testing.md) proved
nineteen of twenty bundle components on a k3s under Docker Desktop on 2026-09-15 (#2). Three things
stay for real nodes, and this directory is how to make them:

| Needs a real node because | Component |
|---|---|
| A CNI-less node with the OVS kernel modules and a `kube-ovn/role=master` label | `kube-ovn` |
| `/dev/kvm`, which Docker Desktop's VM does not expose to a container | KubeVirt **guests** (the operator runs anywhere) |
| A block device to replicate | LINSTOR / DRBD (#19) |

`New-CyberCloudLab.ps1` creates one control-plane and two worker Hyper-V VMs on Ubuntu 24.04 with
nested virtualization on, a blank data disk per worker, and k3s started **without** flannel so
kube-ovn is the CNI from the first pod. It writes `out/kubeconfig.yaml` with the context
`cybercloud-lab`; `charts/bundle/install.sh --context cybercloud-lab` is the next command.

⚠ **It must run from an elevated PowerShell, and that is the one step a remote session cannot do.**
Hyper-V refuses an unelevated caller, and UAC on the dev host prompts on the secure desktop. Everything
else — the Ubuntu cloud image (verified against `SHA256SUMS`), the seed ISOs, the SSH key, the token
— the script makes or fetches itself under `%USERPROFILE%\CyberCloudLab`. `qemu-img` is the one tool it
refuses to fetch unpinned: install QEMU for Windows first, or drop a verified `qemu-img.exe` where
the script says.

⚠ **Not `deploy/bootstrap/`.** That installs Cyber Cloud onto a cluster and is what an operator runs
when the platform is the broken thing. This makes a throwaway cluster to install *other people's
operators* onto and prove they run; it is on no repair path and ships in no image.

What this lane has and has not done is recorded where the bundle records it:
`charts/bundle/bundle.yaml § owed`, rows `kube-ovn-needs-a-node-this-lane-cannot-give` and
`virtual-machines-need-a-node-with-kvm`. Neither has been run yet — the script is the preparation,
written the day the Docker lane landed, and its first run is #95.
