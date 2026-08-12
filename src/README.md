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
`CyberCloud.Telemetry`, `CyberCloud.ServiceDefaults`, `CyberCloud.Analyzers`.

⚠ `CyberCloud.Analyzers` breaks the naming convention's twin, the single-TFM rule: it targets
`netstandard2.0`, because that is what a Roslyn analyzer must target to be loadable by every
compiler host. It is referenced with `OutputItemType="Analyzer"`, never as an ordinary reference —
copy the `ItemGroup` from `CyberCloud.Core.csproj` into any new assembly that should be policed.


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
4. Nothing references a `*.Application` assembly except its own host, which that assembly names with
   `[assembly: OwningHost("…")]`.
5. The gateway references no provider *implementation* assembly, only `.Contracts` and `.Application`.
7. Every edge between two modules is declared in [`module-layering.txt`](../module-layering.txt), and
   the declaration is acyclic. A module is an assembly name truncated to its first two dotted
   segments, so `CyberCloud.Identity` and `CyberCloud.Identity.Contracts` are one.

Rule 6 is about `portal/libs/api` and is the one rule here that is not about an assembly.

⚠ Rules 2 and 4 read the declared `ProjectReference` set as well as the compiled `AssemblyRef` table,
and rule 7 exists at all, because each was a hole found by constructing a violation and watching the
gate stay green. docs/plan/03 § Assembly graph rules records what each one was.

See [docs/plan/03](../docs/plan/03-repository-layout.md) and
[docs/plan/00 § Layer discipline](../docs/plan/00-vision-and-principles.md).
