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
├── Build.Licence.cs          # ADR-011 scan over charts + images
├── Build.Portal.cs           # pnpm install/lint/test/build, performance budget, axe
├── Build.E2E.cs              # ─┐
├── Build.Chaos.cs            #  ├ against a real deployment; nightly and weekly, not per-PR
├── Build.Load.cs             # ─┘
├── Build.Publish.cs          # NuGet, npm, charts, `cyc` binaries per RID
├── ArchitectureFacts.cs      # ⚠ not a Build partial — see below
├── CoverageReport.cs         # ⚠ likewise: Cobertura in, per-assembly line rates out
└── TargetPreconditions.cs    # ⚠ likewise: "blocked here, and here is what to install"
```

`ArchitectureFacts.cs`, `CoverageReport.cs` and `TargetPreconditions.cs` are the files here that are
**not** partials of `Build`, and the exception is deliberate. `Architecture` reads compiled assemblies through `System.Reflection.Metadata`; the record
and the `ICustomAttributeTypeProvider` that does it are ordinary types with their own lifetime, and
folding them into the target's partial would mix "what the gates read" with "what the gates decide"
in one 700-line file. `CoverageReport.cs` splits `Test` along the same seam, and gains the same
thing: what a report says is checkable without running a build. The rule that still holds is the one
that matters: **one partial per target, named after it** — no target's logic lives anywhere but its
own `Build.<Target>.cs`.

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
                      ├──► E2E            (stub)
                      ├──► Chaos          (stub)
                      ├──► Load           (stub)
                      └──► Images ────────┐ (stub)
Charts (stub) ────────────────────────────┴──► Licence (stub)
Portal (stub)

Publish (stub) ──► Test, Generate, Architecture, Portal, Licence
```

Everything except `Clean`, `Restore`, `Compile` and `Test` is wired with its real dependencies but
logs "not implemented yet — tracked in …" and succeeds. A stub that fails the build is worse than no
stub: it trains everyone to ignore a red target.

Three of those edges are missing on purpose, and each is the first thing a reader goes looking for:

| Missing edge | Why |
|---|---|
| `E2E` `Chaos` `Load` → a deploy | There is no `Deploy` target. They run "against a real deployment" (docs/plan/23 § Build) — standing staging nightly, a deployed candidate pre-release. The deployment is an input to the run, not something the graph produces, and as an edge it would mean every local `./build.sh E2E` tried to deploy something. They depend on `Compile`, which builds the suites in `test/` and the `cyc` they drive. |
| `Portal` → `Generate` | `Generate` emits `portal/libs/api` and the resource forms, but those are generated **and committed**, and `Generate`'s job in the graph is to fail on drift rather than to feed a later target. The edge would drag `Compile` in behind it and make the .NET SDK a prerequisite for running `eslint`. |
| `Publish` → `E2E` `Chaos` `Load` | A release *is* gated on all three (docs/plan/23 § Test layers, "green before release"), but they run against the deployed candidate. The order is gate → deploy → suites → publish, the deploy in the middle is not a target, so `release.yml` owns that sequencing and this edge would invert it. |

## `Test` runs the test hosts directly

`Test` invokes `dotnet run --project <test project> -- <MTP args>`, **not** `dotnet test`. Test
projects are Microsoft.Testing.Platform hosts (`OutputType=Exe`, xunit.v3, no VSTest adapter), and
`dotnet test` puts a runner-selection step in front of them that can pick VSTest and abort the run
with an error about `testhost.deps.json` — nothing to do with the tests. Running the host directly
removes the choice, puts the failing assertion and its source line on stdout, and passes the exit
code through.

Three guards, all of which have been shown to fire:

* `--minimum-expected-tests 1` — a project that discovers zero tests exits 8 and fails the build,
  instead of reporting success for having done nothing.
* A test project on disk but absent from `CyberCloud.slnx` fails `Test` with a message naming the
  project and the `dotnet sln … add` command to fix it. `Compile` builds the solution, so an
  unlisted project is never built; without this the failure is a missing-`.dll` error that names
  the wrong problem.
* A project under `test/` that no target claims fails `Test` with the same shape of message. See
  below — discovery is split by owning target, and this is what stops a suite falling between them.

The last two run before the "no test projects, nothing to run" early return, and cover every test
project rather than only the per-PR ones, because `Test` is the only one of the four suite-running
targets that runs on every PR and so the only one positioned to notice.

## Which target runs which test project

`Directory.Build.props` § Project role detection decides what builds as an MTP host. It does **not**
decide what runs per-PR: docs/plan/23 § Test layers puts E2E and Chaos on nightly and Load on
weekly, against a real deployment. `Build.Test.cs` § `SuiteOwning` maps each project to its owning
target, with one arm per props rule so a rule cannot be added without naming the target that runs
it.

| Project | Target | Runs |
|---|---|---|
| `*.Tests`, `*.Conformance`, `CyberCloud.Isolation` | `Test` | Every PR |
| `CyberCloud.E2E` | `E2E` | Nightly + pre-release |
| `CyberCloud.Chaos` | `Chaos` | Nightly |
| `CyberCloud.Load` | `Load` | Weekly + pre-release |

⚠ These were one list until the split, and `Test` would have run all of them on every PR — observed
against three empty projects: `./build.sh Test` logged `running 3 test project(s)`. That is the
nightly and weekly suites inside a 3-minute per-PR budget, against no deployment.

`Directory.Build.props` § Test runner separately configures `dotnet test` to select MTP, so a
developer or IDE running it by hand gets the same answer as CI. ⚠ Those two properties must live in
`Directory.Build.props`, not `Directory.Build.targets` — see the comment there.

## The coverage floor can say three things, and one of them is "I don't know"

docs/plan/23 § Test layers puts the floor at **≥ 70 % lines per shipping project**. `Test` can come
back with any of three answers, and the third is the one worth designing for:

| | Means |
|---|---|
| ✔ | every shipping project is at or above the floor |
| ✘ | these projects are below it, at these rates |
| ○ | **nothing here measured anything** — a warning locally, a hard failure on CI |

The ○ exists because `dotnet-coverage` 18.9.0 fails by **exiting 0 and writing `<packages />`**.
Nothing in the exit code says so, and the one line it prints goes to stdout in the middle of the
test output. Enforce a floor against that report and every project reads 0 %: a red build that tells
you to write tests when the truth is that the profiler never loaded.

`Build.Test.cs` § `CoverageCollectionIsAvailable` used to answer "can we collect here?" from a
platform allow-list — everything except macOS/arm64 was assumed to work. Measured, all four cells,
with the same three-line probe:

| Platform | Result |
|---|---|
| `osx-arm64` | `<packages />`, exit 0 |
| `linux-x64`, no libxml2 | `<packages />`, exit 0, plus one hint naming libxml2 |
| `linux-x64`, libxml2 installed | `line-rate="0.667"` — **the only cell that works** |
| `linux-arm64`, libxml2 installed | `<packages />`, exit 0, and no mention of libxml2 at all |

The package explains all four: `tools/net8.0/any/` carries native profilers for `ubuntu/x64`,
`alpine/x64` and `macos/x64` only, and both its `arm64` directories hold a *Windows* DLL. So the
allow-list was wrong on two of the three platforms it waved through, and the honest question is not
"which platform is this?" but "does it work here?".

It is now the second question. `Test` builds a three-line assembly into `artifacts/coverage-probe/`,
collects over it, and requires a line rate **strictly between 0 and 1** — one covered line and one
uncovered one, so the probe cannot pass by accident on the `line-rate="1"` an empty report reports.
It costs about two seconds and is skipped-then-cached across runs. Two guards behind it:

* A probe that wrongly says **no** skips collection, leaves no report, and lands on ○ — which fails
  CI. The safe direction.
* A probe that wrongly says **yes** is caught after the fact: `EnforceCoverageFloor` refuses to read
  a floor out of reports that between them name **no assembly at all**. That is the backstop that
  runs against the real suites rather than a hello-world.

`.github/workflows/gate.yml` asks the same question one layer up — it installs libxml2, then runs
`.github/scripts/assert-coverage-profiler.sh` before the suites, so CI fails in fifteen seconds
rather than after a full test run. The two probes are deliberately built from the same three lines,
so a disagreement between them is a real disagreement about the machine.

### "Nothing to instrument" is not "nothing tests this"

A project with no executable code and a project no test ever loads look **identical** in a Cobertura
report: neither gets a `<package>` element. Four projects here are the first kind —
`CyberCloud.Providers.{Cache,DBforPostgreSQL,Messaging,Sample}.Application`, each one nothing but a
body-less ABP module declaration. `dotnet-coverage instrument` refuses all four with
`Reason: optimized_or_instrumented`, and the floor used to read that silence as 0 % — for projects
whose tests pass.

`CoverageReport.cs` § `CoverableLines` tells them apart by counting sequence points in the compiled
assembly's portable PDB, **discounting anything that carries `[ExcludeFromCodeCoverage]`**. That
subtraction is the whole measurement: `CyberCloud.Providers.Sample.Application`'s PDB does hold two
sequence points, both inside the `Metadata_…` type Orleans' source generator emits, which the
generator marks `[GeneratedCode]`, `[EditorBrowsable]` and `[ExcludeFromCodeCoverage]`. Count them
and the assembly looks coverable; discount them and it has nothing to cover — which is the answer
`dotnet-coverage` reaches too. Across the whole tree the rule picks out those four and nothing else.

Three things keep it from becoming an exemption list:

* Only projects **already absent** from the report are examined, so it can never lift one over the
  floor — a project with a real number is judged on that number.
* The evidence is a count in a compiled artefact, not a name, a folder, or a list in a file.
* An assembly it cannot read answers `null`, not `0`, and `null` is **not** excused. An unanswered
  question that turns into a pass is the failure the whole file is written against.

## `failed to bind host port` — Docker publishes into macOS's ephemeral range

Symptom, from any of the container-backed suites (NATS, Redis, PostgreSQL), on macOS:

```
failed to bind host port 0.0.0.0:60990/tcp: address already in use
```

Three differently-shaped flakes, one cause, and it is **not** a readiness bug and not something a
retry should paper over. Measured on a Docker Desktop 29.7.2 host:

| | Range |
|---|---|
| macOS ephemeral ports (`sysctl net.inet.ip.portrange.first`/`.last`) | **49152–65535** |
| Docker's host-port allocator, from the VM's `net.ipv4.ip_local_port_range` | **55000–65535** |

Docker's range sits **entirely inside** macOS's. Confirmed by watching four containers started with
`-P` take 55194, 55196, 55197 and 55202 — the allocator walks up from the low end of the VM sysctl,
and every port it hands out is one macOS may give to an outbound socket at the same moment. Under
concurrent container start-up plus connection churn the kernel wins the race between Docker's pick
and Docker's bind. A TOCTOU race, not a timeout.

**This is a host configuration change, and it cannot be fixed from inside this repository.** The two
levers, neither of which is applied here:

1. **Move macOS's ephemeral range off Docker's** (persistent, preferred):

   ```
   sudo sysctl -w net.inet.ip.portrange.first=32768 net.inet.ip.portrange.last=49151
   sudo sysctl -w net.inet.ip.portrange.hifirst=32768 net.inet.ip.portrange.hilast=49151
   ```

   Same 16 384 ports as before, just below Docker's floor, and it survives a reboot from
   `/etc/sysctl.conf`. The cost is that outbound sockets now share the registered-port region, so a
   local service listening in 32768–49151 could collide instead.

2. **Move Docker's allocator below macOS's ephemeral floor**, by setting the VM's
   `net.ipv4.ip_local_port_range` to something like `20000 29999`. Narrower blast radius, but Docker
   Desktop exposes no supported setting for it — it means `nsenter`-ing the VM, and it does not
   survive a Docker Desktop restart.

⚠ **Do not "fix" this by serialising the container suites.** Measured: the five container-backed
suites sum to 163 s of work inside a gate that finishes in ~55 s on an idle machine, because they
overlap almost completely. Full serialisation is roughly **3×** slower and a cap of two is ~1.6×. A
gate people stop running is the same problem wearing a different hat. ⚠ And do not add a retry:
that turns a flaky gate into a slow one that fails at a lower rate, which is strictly harder to
diagnose.

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
