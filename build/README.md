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

## The entry point

`./build.sh <Target>` (or `build.cmd` on Windows) — [docs/plan/23](../docs/plan/23-build-ci-and-testing.md)
§ Build. `./build.sh --help` lists every target and parameter.

**Anything CI does, `./build.sh <Target>` does locally.** A CI step that is a shell script in a
workflow file is a step nobody can reproduce or bisect.

`build.sh` runs `dotnet run --project build/_build.csproj` rather than the `nuke` global tool,
because the global tool prompts on the console when it does not recognise the directory:

```
$ dotnet nuke --help < /dev/null
Could not find .nuke directory/file. Do you want to setup a build? [y/n] (y):
Failed to read input in non-interactive mode.
```

`.nuke/parameters.json` is committed, which is what actually stops that. Nuke cannot start without
it at all — with `.nuke/` moved aside, `./build.sh Compile` dies with
`Could not locate '.nuke' directory/file while walking up from …`.

Target graph:

```
Clean
Restore ──► Compile ──┬──► Test
                      ├──► Generate       (stub)
                      ├──► Architecture   (stub)
                      └──► Images ────────┐ (stub)
Charts (stub) ────────────────────────────┴──► Licence (stub)
```

`Generate`, `Charts`, `Images`, `Architecture` and `Licence` are wired with their real dependencies
but log "not implemented yet — tracked in …" and succeed. A stub that fails the build is worse than
no stub: it trains everyone to ignore a red target.

## `Test` runs the test hosts directly

`Test` invokes `dotnet run --project <test project> -- <MTP args>`, **not** `dotnet test`. Test
projects are Microsoft.Testing.Platform hosts (`OutputType=Exe`, xunit.v3, no VSTest adapter), and
`dotnet test` puts a runner-selection step in front of them that can pick VSTest and abort the run
with an error about `testhost.deps.json` — nothing to do with the tests. Running the host directly
removes the choice, puts the failing assertion and its source line on stdout, and passes the exit
code through.

Two guards, both of which have been shown to fire:

* `--minimum-expected-tests 1` — a project that discovers zero tests exits 8 and fails the build,
  instead of reporting success for having done nothing.
* A test project on disk but absent from `CyberCloud.slnx` fails `Test` with a message naming the
  project and the `dotnet sln … add` command to fix it. `Compile` builds the solution, so an
  unlisted project is never built; without this the failure is a missing-`.dll` error that names
  the wrong problem.

`Directory.Build.props` § Test runner separately configures `dotnet test` to select MTP, so a
developer or IDE running it by hand gets the same answer as CI. ⚠ Those two properties must live in
`Directory.Build.props`, not `Directory.Build.targets` — see the comment there.

## Why the analyser exemptions are where they are

`_build.csproj` is the one project exempted from warnings-as-errors, because Nuke's target fields are
assigned by reflection. That exemption needs **three** separate things, and each was verified by a
build that failed without it:

| Where | What | Because |
|---|---|---|
| [`Directory.Build.targets`](../Directory.Build.targets) | `TreatWarningsAsErrors=false`, `NoWarn=CA1515;CA1822;CS0649` | the compiler-warning half |
| [`_build.csproj`](_build.csproj) | `CodeAnalysisTreatWarningsAsErrors=false`, `NoWarn=…;CA1707` | `CodeAnalysisTreatWarningsAsErrors` is a **separate** switch covering the `CAxxxx` family, and the root props sets it to `true` |
| [`.editorconfig`](.editorconfig) (this directory) | `IDE1006`, `IDE0005` → `none` | the root `.editorconfig` sets these to `severity = error`, which applies **regardless** of `TreatWarningsAsErrors` and is not suppressed by `NoWarn` |

If you are chasing a build error in `build/` that "should already be exempted", it is almost
certainly in the third row.

`<NukeTelemetryVersion>` in `_build.csproj` is the repository-level answer to Nuke's
"Press &lt;Enter&gt; to create awareness cookie" telemetry notice; `build.sh` also exports
`NUKE_TELEMETRY_OPTOUT=1`. Both live in Nuke.Build.dll, not Nuke.Common.dll.

See [docs/plan/23](../docs/plan/23-build-ci-and-testing.md).
