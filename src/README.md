# `src/` — the .NET tree

Everything .NET that is part of the platform itself. The `cc` CLI lives in [`cli/`](../cli) instead,
because it ships separately.

**Naming: folder name == assembly name == root namespace**, `CyberCloud.` prefix on everything.
`Directory.Build.props` derives `AssemblyName` and `RootNamespace` from the project file name, so
getting the folder name right is the whole convention.

Tests are siblings (ADR-018): assume `X.Tests` next to every `X`.

## Foundation assemblies that belong here

`CyberCloud.Core`, `CyberCloud.Core.Contracts`, `CyberCloud.Tenancy(.Contracts)`,
`CyberCloud.Authorization(.Contracts)`, `CyberCloud.Kubernetes`, `CyberCloud.Kubernetes.Charts`,
`CyberCloud.ResourceManager(.Contracts)`, `CyberCloud.Metering`, `CyberCloud.Billing`,
`CyberCloud.Telemetry`, `CyberCloud.ServiceDefaults`.

Providers go in [`Providers/`](Providers); hosts go in [`Hosts/`](Hosts).

## The `.Contracts` split is not ceremony

Grain interfaces and wire types go in `*.Contracts`. The gateway, the CLI and the tests reference
only those. A provider implementation assembly is referenced by exactly one host. This is what makes
a rolling silo upgrade possible.

## Assembly graph rules (docs/plan/03 § Assembly graph rules)

Enforced by `Build.Architecture`, failing the build on violation:

1. `CyberCloud.Core` references no Orleans hosting, no `KubernetesClient`, no ABP application layer.
2. No `Providers.*` assembly references another `Providers.*` assembly — not even `.Contracts`.
3. No assembly above `CyberCloud.Kubernetes` references `k8s.Models`.
4. Nothing references a `*.Application` assembly except its own host.
5. The gateway references no provider *implementation* assembly, only `.Contracts` and `.Application`.

See [docs/plan/03](../docs/plan/03-repository-layout.md) and
[docs/plan/00 § Layer discipline](../docs/plan/00-vision-and-principles.md).
