<#
.SYNOPSIS
    Creates the VM lane — three Ubuntu 24.04 Hyper-V VMs running one k3s cluster — for the parts of
    the bundle that k3s-in-Docker cannot honestly host.

.DESCRIPTION
    docs/plan/23 § The lane that needs a kubelet proved nineteen of twenty bundle components on a
    k3s under Docker Desktop (2026-09-15). What stays for real nodes, and why this script exists:

      kube-ovn         — a CNI-less node with the OVS kernel modules and a `kube-ovn/role=master` label
      KubeVirt guests  — /dev/kvm, which Docker Desktop's VM does not expose to containers
      LINSTOR / DRBD   — a real block device to replicate

    One control-plane VM and two workers, nested virtualization ON (KubeVirt), an extra blank disk on
    each worker (LINSTOR), k3s started with `--flannel-backend=none --disable-network-policy` so
    kube-ovn is the CNI from the first pod, cloud-init doing all of it. When it returns, the
    kubeconfig is in .\out\kubeconfig.yaml and `charts/bundle/install.sh --context cybercloud-lab`
    is the next command.

    ⚠ MUST RUN ELEVATED. Hyper-V refuses an unelevated caller with "You do not have the required
    permission" — UAC on this host prompts on the secure desktop, so this cannot be launched from a
    remote Claude session; it is the one step that needs a person at the console. Everything it
    needs is downloaded and checksum-verified by the script itself (the Ubuntu cloud image from
    cloud-images.ubuntu.com against SHA256SUMS; qemu-img from the QEMU Windows build for the
    qcow2 → VHDX conversion).

    ⚠ NOT deploy/bootstrap. That installs Cyber Cloud onto a cluster; this makes a cluster to
    install the *bundle* onto, and is on no repair path (deploy/README.md).

.PARAMETER Workers
    How many worker VMs. Default 2; 1 is enough for kube-ovn and KubeVirt, 2 for LINSTOR replication.

.PARAMETER Switch
    The Hyper-V virtual switch. Default "Default Switch" (NAT to the host, which is all the lane needs).

.EXAMPLE
    PS> .\New-CyberCloudLab.ps1
    PS> $env:KUBECONFIG = "$PWD\out\kubeconfig.yaml"; bash ../../charts/bundle/install.sh --context cybercloud-lab
#>
[CmdletBinding()]
param(
    [int] $Workers = 2,
    [string] $Switch = 'Default Switch',
    [string] $Root = "$env:USERPROFILE\CyberCloudLab",
    [int] $ControlPlaneMemoryGB = 8,
    [int] $WorkerMemoryGB = 8,
    [int] $Cpus = 4,
    [string] $UbuntuRelease = 'noble',
    [string] $K3sVersion = 'v1.35.7+k3s1'
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell. Hyper-V refuses an unelevated caller, and UAC on this host cannot be answered remotely."
}

if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) {
    throw "Hyper-V's PowerShell module is not installed. `Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All` and reboot."
}

New-Item -ItemType Directory -Force -Path "$Root\images", "$Root\vms", "$Root\seed", "$PSScriptRoot\out" | Out-Null

# ── The image: Ubuntu's cloud image, verified against its published SHA256SUMS ─────────────────
$imageName = "ubuntu-24.04-server-cloudimg-amd64.img"
$imageUrl = "https://cloud-images.ubuntu.com/$UbuntuRelease/current/$imageName"
$image = "$Root\images\$imageName"

if (-not (Test-Path $image)) {
    Write-Host "Downloading $imageUrl"
    Invoke-WebRequest -Uri $imageUrl -OutFile $image
    Invoke-WebRequest -Uri "https://cloud-images.ubuntu.com/$UbuntuRelease/current/SHA256SUMS" -OutFile "$Root\images\SHA256SUMS"
    $expected = (Get-Content "$Root\images\SHA256SUMS" | Where-Object { $_ -match "\*$imageName$" }) -split ' ' | Select-Object -First 1
    $actual = (Get-FileHash $image -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { Remove-Item $image; throw "SHA-256 of $imageName is $actual, SHA256SUMS says $expected. Refusing to use it." }
}

# ── qemu-img, for qcow2 → VHDX. Hyper-V cannot read qcow2. ─────────────────────────────────────
$qemuImg = Get-Command qemu-img -ErrorAction SilentlyContinue
if (-not $qemuImg) {
    $qemuDir = "$Root\qemu-img"
    if (-not (Test-Path "$qemuDir\qemu-img.exe")) {
        # ⚠ The stand-alone qemu-img build for Windows is published by Cloudbase; pin and verify it
        # here the day this lane is exercised — the URL and hash are the two facts a reader needs
        # to trust a binary that writes disk images. Until then the script refuses rather than
        # fetching an unpinned binary.
        throw "qemu-img is not on PATH. Install QEMU for Windows (winget install SoftwareFreedomConservancy.QEMU) or put qemu-img.exe under $qemuDir, then run again."
    }
    $qemuImg = "$qemuDir\qemu-img.exe"
} else {
    $qemuImg = $qemuImg.Source
}

$baseVhdx = "$Root\images\ubuntu-24.04-base.vhdx"
if (-not (Test-Path $baseVhdx)) {
    & $qemuImg convert -f qcow2 -O vhdx -o subformat=dynamic $image $baseVhdx
}

# ── One cluster token, one SSH key, minted here and written only under $Root ───────────────────
$token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$sshKey = "$Root\id_ed25519"
if (-not (Test-Path "$sshKey.pub")) { ssh-keygen -t ed25519 -N '""' -f $sshKey -C cybercloud-lab | Out-Null }
$pubKey = (Get-Content "$sshKey.pub").Trim()

function New-SeedIso([string] $name, [string] $userData) {
    # cloud-init's NoCloud source: a FAT or ISO volume labelled `cidata` with user-data and meta-data.
    # Hyper-V VMs boot it as a DVD. Made with the oscdimg that ships in the Windows ADK when present,
    # else with a small FAT VHD, which cloud-init reads the same way.
    $dir = "$Root\seed\$name"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -Path "$dir\meta-data" -Value "instance-id: $name`nlocal-hostname: $name`n" -NoNewline -Encoding ascii
    Set-Content -Path "$dir\user-data" -Value $userData -NoNewline -Encoding ascii
    $iso = "$Root\seed\$name.iso"
    $oscdimg = Get-Command oscdimg -ErrorAction SilentlyContinue
    if ($oscdimg) {
        & $oscdimg.Source -j1 -lcidata $dir $iso | Out-Null
        return $iso
    }
    $vhd = "$Root\seed\$name.vhdx"
    if (Test-Path $vhd) { Remove-Item $vhd }
    $disk = New-VHD -Path $vhd -SizeBytes 64MB -Fixed
    $mounted = Mount-VHD -Path $vhd -PassThru | Initialize-Disk -PartitionStyle MBR -PassThru | New-Partition -UseMaximumSize -AssignDriveLetter | Format-Volume -FileSystem FAT -NewFileSystemLabel cidata -Confirm:$false
    Copy-Item "$dir\*" "$($mounted.DriveLetter):\"
    Dismount-VHD -Path $vhd
    return $vhd
}

function New-Node([string] $name, [int] $memoryGB, [bool] $controlPlane, [string] $serverUrl, [bool] $extraDisk) {
    $vmDir = "$Root\vms\$name"
    New-Item -ItemType Directory -Force -Path $vmDir | Out-Null
    $osDisk = "$vmDir\os.vhdx"
    if (-not (Test-Path $osDisk)) {
        New-VHD -Path $osDisk -ParentPath $baseVhdx -Differencing | Out-Null
        Resize-VHD -Path $osDisk -SizeBytes 80GB
    }

    $k3sArgs = if ($controlPlane) {
        "server --cluster-init --flannel-backend=none --disable-network-policy --disable=traefik --disable=servicelb --disable=metrics-server --tls-san=$name --write-kubeconfig-mode=644 --node-label=kube-ovn/role=master"
    } else {
        "agent --server $serverUrl"
    }

    $userData = @"
#cloud-config
hostname: $name
users:
  - name: cyber
    groups: [sudo]
    shell: /bin/bash
    sudo: ALL=(ALL) NOPASSWD:ALL
    ssh_authorized_keys:
      - $pubKey
package_update: true
packages: [curl, openvswitch-switch, linux-modules-extra-`$(uname -r)]
write_files:
  - path: /etc/modules-load.d/cybercloud.conf
    content: |
      openvswitch
      geneve
      kvm
      kvm_intel
runcmd:
  - modprobe openvswitch || true
  - modprobe geneve || true
  - [ sh, -c, "curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION=$K3sVersion K3S_TOKEN=$token sh -s - $k3sArgs" ]
"@

    $seed = New-SeedIso $name $userData

    if (-not (Get-VM -Name $name -ErrorAction SilentlyContinue)) {
        New-VM -Name $name -Generation 2 -MemoryStartupBytes ($memoryGB * 1GB) -VHDPath $osDisk -SwitchName $Switch -Path "$Root\vms" | Out-Null
        Set-VM -Name $name -ProcessorCount $Cpus -AutomaticStopAction ShutDown -CheckpointType Disabled
        Set-VMFirmware -VMName $name -EnableSecureBoot Off
        # ⚠ Nested virtualization is what gives KubeVirt guests /dev/kvm. It must be set while the VM
        # is off, and it turns dynamic memory off by itself.
        Set-VMProcessor -VMName $name -ExposeVirtualizationExtensions $true
        Set-VMMemory -VMName $name -DynamicMemoryEnabled $false
        if ($seed -like '*.iso') { Add-VMDvdDrive -VMName $name -Path $seed } else { Add-VMHardDiskDrive -VMName $name -Path $seed }
        if ($extraDisk) {
            # A blank 40 GB disk for LINSTOR/DRBD — a real block device, which is the whole reason.
            $data = "$vmDir\data.vhdx"
            if (-not (Test-Path $data)) { New-VHD -Path $data -SizeBytes 40GB -Dynamic | Out-Null }
            Add-VMHardDiskDrive -VMName $name -Path $data
        }
    }

    Start-VM -Name $name
}

# ── Control plane first; its address is what the workers join ─────────────────────────────────
New-Node -name 'cc-cp-1' -memoryGB $ControlPlaneMemoryGB -controlPlane $true -serverUrl '' -extraDisk $false

Write-Host "Waiting for cc-cp-1's address…"
$cpIp = $null
for ($i = 0; $i -lt 60 -and -not $cpIp; $i++) {
    Start-Sleep 5
    $cpIp = (Get-VMNetworkAdapter -VMName cc-cp-1).IPAddresses | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' } | Select-Object -First 1
}
if (-not $cpIp) { throw "cc-cp-1 reported no IPv4 address in five minutes; open its console in Hyper-V Manager." }
Write-Host "cc-cp-1 is $cpIp"

for ($w = 1; $w -le $Workers; $w++) {
    New-Node -name "cc-worker-$w" -memoryGB $WorkerMemoryGB -controlPlane $false -serverUrl "https://${cpIp}:6443" -extraDisk $true
}

# ── The kubeconfig, rewritten to the address a host process can reach ─────────────────────────
Write-Host "Waiting for k3s on cc-cp-1…"
$kubeconfig = $null
for ($i = 0; $i -lt 60 -and -not $kubeconfig; $i++) {
    Start-Sleep 10
    $kubeconfig = ssh -i $sshKey -o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -o ConnectTimeout=5 "cyber@$cpIp" 'sudo cat /etc/rancher/k3s/k3s.yaml' 2>$null
}
if (-not $kubeconfig) { throw "k3s did not come up on cc-cp-1 in ten minutes; `ssh -i $sshKey cyber@$cpIp sudo journalctl -u k3s` says why." }

$out = "$PSScriptRoot\out\kubeconfig.yaml"
($kubeconfig -join "`n") -replace 'https://127\.0\.0\.1:6443', "https://${cpIp}:6443" -replace '\bdefault\b', 'cybercloud-lab' | Set-Content -Path $out -Encoding ascii
Write-Host "kubeconfig written to $out (context cybercloud-lab)."
Write-Host "Next: `$env:KUBECONFIG = '$out'; bash ../../charts/bundle/install.sh --context cybercloud-lab"
