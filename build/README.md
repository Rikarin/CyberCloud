# `build/` — Nuke, the single entry point for every build action

```
build/
├── _build.csproj
├── Build.cs                  # partial: target graph
├── Build.Compile.cs
├── Build.Test.cs
├── Build.Generate.cs         # provider registry → OpenAPI → CLI → SDK → portal forms (ADR-012)
├── Build.Charts.cs           # helm lint/package/push, values.schema.json generation
├── Build.Images.cs           # container images, SBOM, cosign signatures
├── Build.Architecture.cs     # the gates in docs/plan/00 § Non-negotiables
└── Build.Licence.cs          # ADR-011 scan over charts + images
```

Nuke is pinned in [`.config/dotnet-tools.json`](../.config/dotnet-tools.json) (`dotnet nuke`) and
`Nuke.Common` in [`Directory.Packages.props`](../Directory.Packages.props). Both must move together.

**Anything CI does, `dotnet nuke <target>` does locally.** A CI step that is a shell script in a
workflow file is a step nobody can reproduce or bisect.

`_build.csproj` is the one project exempted from `TreatWarningsAsErrors` — Nuke's target fields are
assigned by reflection, which trips CS0649 and CA1822 across the whole build definition. See
[`Directory.Build.targets`](../Directory.Build.targets).

See [docs/plan/23](../docs/plan/23-build-ci-and-testing.md).
