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
├── CodeSurface.cs            # ⚠ likewise: every type and member this repository compiles
├── WireContract.cs           # ⚠ likewise: the [Id(n)] manifests under build/wire
├── GeneratedSdkSurface.cs    # ⚠ likewise: generated/sdk/*.cs through Roslyn (issue #73)
├── CoverageReport.cs         # ⚠ likewise: Cobertura in, per-assembly line rates out
└── TargetPreconditions.cs    # ⚠ likewise: "blocked here, and here is what to install"
```

The six files above that are **not** partials of `Build` are a deliberate exception. `Architecture`
reads compiled assemblies through `System.Reflection.Metadata`; the record
and the `ICustomAttributeTypeProvider` that does it are ordinary types with their own lifetime, and
folding them into the target's partial would mix "what the gates read" with "what the gates decide"
in one 700-line file. `CoverageReport.cs` splits `Test` along the same seam, and gains the same
thing: what a report says is checkable without running a build. `GeneratedSdkSurface.cs` is the
newest and the only one that reads **source** rather than metadata — it hands each
`generated/sdk/{api-version}.cs` to Roslyn, because "is this valid C#" is a question only a C#
compiler answers and the `Generated surfaces` row had been answering "are the bytes the same"
instead. The rule that still holds is the one
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

The ○ exists because a collector can fail without saying so. `dotnet-coverage` 18.9.0 did it by
**exiting 0 and writing `<packages />`**: nothing in the exit code says so, and the one line it
prints goes to stdout in the middle of the test output. Enforce a floor against that report and
every project reads 0 % — a red build that tells you to write tests when the truth is that the
profiler never loaded.

### The collector is `coverlet`, because the floor has to be measurable where the code is written

`dotnet-coverage` 18.9.0 carries native profilers for `ubuntu/x64`, `alpine/x64`, `macos/x64` and
Windows and for nothing else; both of its `arm64` directories hold a *Windows* DLL. Measured, all
four cells, with the three-line probe below:

| Platform | Result |
|---|---|
| `osx-arm64` | `<packages />`, exit 0 |
| `linux-x64`, no libxml2 | `<packages />`, exit 0, plus one hint naming libxml2 |
| `linux-x64`, libxml2 installed | `line-rate="0.667"` — **the only cell that worked** |
| `linux-arm64`, libxml2 installed | `<packages />`, exit 0, and no mention of libxml2 at all |

⚠ **So on every Apple Silicon machine the probe said no, `Test` printed ○, and an x64 CI runner was
the first and only place the floor had ever run.** Two projects breached it unnoticed under that
arrangement, which is what a gate nobody can run locally is for. A floor that only CI can see is a
floor the person who could fix it never reads.

`coverlet` has no native profiler: it rewrites IL in the suite's own output directory and puts the
assemblies back afterwards. Measured on `osx-arm64` against a project counted by hand — one class,
three methods, two of them reached by two tests — it reports `line-rate="0.5454"`, which is 6 of 11
sequence-point lines and is the number a person gets with a pencil. libxml2, the profiler matrix and
the x64 pin on the CI job all stop being things this repository has to know about.

⚠ **The tools are not both installed.** `.config/dotnet-tools.json` pins one collector, so every
number in a report has one possible author. Two coverage tools disagreeing by a few points is
survivable; two that silently disagree about *what* they measured is not.

### The probe stays, and now probes the tool that is used

`Build.Test.cs` § `CoverageCollectionIsAvailable` used to answer "can we collect here?" from a
platform allow-list — everything except macOS/arm64 was assumed to work, which was wrong on two of
the platforms it waved through. The honest question is not "which platform is this?" but "does it
work here?", and changing collectors does not change that.

`Test` builds a three-line assembly into `artifacts/coverage-probe/`, collects over it, and requires
a line rate **strictly between 0 and 1** — one covered line and one uncovered one, so the probe
cannot pass by accident on the `line-rate="1"` an empty report reports. It costs about two seconds
and is skipped-then-cached across runs. ⚠ Do not turn it back into a list: an allow-list has to be
updated every time somebody finds a new way for a tool to be missing, and the next way will not be
one that is written down. Two guards behind it:

* A probe that wrongly says **no** skips collection, leaves no report, and lands on ○ — which fails
  CI. The safe direction.
* A probe that wrongly says **yes** is caught after the fact: `EnforceCoverageFloor` refuses to read
  a floor out of reports that between them name **no assembly at all**. That is the backstop that
  runs against the real suites rather than a hello-world, and it matters more under coverlet than it
  did before: the probe instruments a directory holding one assembly, and the suites instrument
  directories holding a hundred.

`.github/workflows/gate.yml` no longer runs a coverage pre-flight step. It used to install libxml2
and then run `.github/scripts/assert-coverage-profiler.sh`, so CI failed in fifteen seconds rather
than after a full test run — worth it when a whole platform's profiler could be missing. coverlet
has no profiler to be missing, and a step asserting something that can no longer fail reads as
coverage nobody has got.

### `coverage-below-floor.txt` — the ratchet, and why it is not an exemption list

Making the floor visible locally turned a gate that had never run into one that ran and was red.
Those reds were not regressions; they had been true for months and nobody could see them. Holding
the measurement hostage to covering every one of them would have stranded the fix behind work nobody
had scheduled, and letting master sit red would have taught everyone to ignore a red `Test`.

So there is a checked-in baseline, modelled on `actions-without-handlers.txt` down to the shape of
its header. A row is a project name and the rate measured the day it was written. `Build.Test.cs`
§ `EnforceCoverageFloor` fails in **four** directions:

| | Fails when |
|---|---|
| the floor | an **unlisted** project is below 70 % |
| the ratchet | a **listed** project is more than half a point **below** its pin |
| the ratchet's other half | a **listed** project is more than half a point **above** its pin — raise it |
| the pruner | a **listed** project **meets** 70 % — delete the row |

The last two are what stop it becoming a permission slip. A list nobody is forced to prune is a list
in which no reader can tell the live rows from the dead ones, and the third rule is why
`actions-without-handlers.txt` is believable.

⚠ **The tolerance is symmetric on purpose, and that is what makes it a band rather than a budget.**
Exact pinning is right for a closed set of action names and wrong for a ratio over every line in a
project: extracting a method moves the number by hundredths, and a build that went red for that is a
build people learn to re-pin without reading. But a one-way tolerance leaks — twenty commits could
walk a project ten points down half a point at a time, each one inside the rules. Failing *upward*
too means the pin tracks whatever the project actually reaches, so the ground under it only ever
rises. Half a point is 1.3 lines on a 255-line project and about 15 on a 3 000-line one, which
tightens the band exactly where a percentage is least forgiving.

⚠ **Every row needs a sentence directly above it**, and the parser refuses one without. A reviewer
cannot answer a review request that is a name and a number, and a list that costs a sentence to
extend is a list that stays short. The file is parsed **before the suites run**, so a typo costs a
second rather than a full test run.

### A line's filename comes from its `<class>`, not its grandparent

Cobertura lists most lines **twice** — once under `<class><lines>` and again under
`<method><lines>` — and only `<class>` carries a `filename`. `CoverageReport.Read` keyed each line
by its grandparent's `filename`, so every method-level line keyed as `(assembly, "", number)`. That
did two wrong things at once: counted each line a second time, and collapsed line 17 of one file
into line 17 of every other. Measured on `CyberCloud.Identity`, the old key reported **1 837 of
2 775 lines, 66.2 %**, for an assembly that is **1 392 of 2 163, 64.4 %** — 1.8 points of inflation
on a 70 % floor, out of a report that was correct.

### A `filename` is relative to *its own report's* `<sources>` root

coverlet writes the deepest directory common to the files it instrumented, so a suite that touched
only `src/` projects writes `…/src/` and one that also touched a host writes the repository root.
Measured over the 71 reports of one run: **56** said the repository root and **15** said `src/`.

Keying on the raw string therefore split one file into two —
`src/CyberCloud.Communication/Providers/ChannelProviders.cs` and
`CyberCloud.Communication/Providers/ChannelProviders.cs` counted as 382 lines rather than 191 — and
the two hit sets were never unioned, so a line covered by one suite read as uncovered because
another suite spelled the path differently. This one **deflated**, hard:

| | split key | resolved key |
|---|---|---|
| `CyberCloud.Communication` | 49.6 % | **70.8 %** |
| `CyberCloud.Communication.Contracts` | 64.0 % | **92.1 %** |
| `CyberCloud.Kubernetes` | 54.6 % | **77.3 %** |
| `CyberCloud.ServiceDefaults` | 59.6 % | **88.4 %** |
| `CyberCloud.Tenancy` | 60.6 % | **86.8 %** |
| `CyberCloud.Silo.Host` | 32.5 % | 32.5 % |

⚠ **Five of the six projects that looked like breaches never were**, which is why
`coverage-below-floor.txt` has one row rather than six. The key is now the resolved absolute path,
and a report declaring more than one `<source>` is refused rather than guessed at — with two roots a
relative filename could belong to either, and picking one would merge some files correctly and split
others, which is this same failure arriving silently a second time.

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

⚠ That paragraph is about the *container-backed* suites and stays true of them. The **cluster**-backed
ones are a different set and they are serialised, because fifteen of the seventeen already serialise
themselves through a lock file whatever the build does — see "The cluster degree is 1" below. That
is not full serialisation bought for green; it is the build agreeing with a constraint that was
already there.

## How many container-backed suites run at once, and how the tree knows which they are

Two separate questions, and getting the second one wrong made the first one unanswerable.

**There are two caps, not one.** A suite that holds a Kubernetes cluster and a suite that holds a
PostgreSQL are both "container-backed" and they are not the same size, so `Build.Test.cs` gives them
separate semaphores: `ContainerBackedSuiteDegree`, derived from the host's CPUs, and
`ClusterBackedSuiteDegree`, which is **1** and is not derived from anything. Cluster-backed suites
take both permits — the cluster one first, always, which is the whole of the deadlock argument — so
the CPU budget still bounds the total.

Counted 2026-09-05 over this tree's own build output, at **73** per-PR suites: **21** can start a
container and **17 of those 21 hold a k3s API server**. The four that do not are
`CyberCloud.{Authorization,ServiceDefaults,Tenancy,Vault}.Tests`.

### The degree is derived from the host

`Build.Test.cs` § `ContainerBackedSuiteDegree` is `Environment.ProcessorCount ÷ 3`, never zero, and
`CC_TEST_CONTAINER_PARALLELISM` overrides it. It used to be the literal **4**, set when there were
68 suites.

⚠ **The obvious calibration for the 3 is not usable, and knowing why matters more than the number.**
"Four starves a suite and three does not", measured 2026-08-19, was taken while the container-backed
set was decided by grepping `.csproj` files — so "degree 4" meant *four mostly-cheap suites in slots
plus up to three k3s clusters outside them*, and it means *at most four container suites* now.
Dividing ten CPUs by a number that measured a different mechanism is arithmetic on a coincidence.

The evidence for three is direct, at the corrected meaning: five full `Test` runs on this ten-CPU
host, four green at 71 suites, and the one loss was `CyberCloud.Tenancy.Tests` failing its collection
fixture on an Npgsql connect timeout — one of the four symptoms the failure message names — which
then passed 131/131 in 13.6 s alone. Contention at the margin, not a degree that does not work. It
is a budget for the *suite*, not for one container: a `.Cluster.Conformance` run holds a k3s API
server, PostgreSQL and Redis plus its own test host. Four would be safer and costs about a third of
the gate's wall clock here; `CC_TEST_CONTAINER_PARALLELISM` is the lever, which is why the failure
message names it.

⚠ **#77 measured three failing and two passing on this host, and the three stays.** On the tree at
`8548ee9`, a derived degree of 3 lost
`CyberCloud.Providers.ContainerRegistry.Cluster.Conformance` — 8/8 alone — while a degree of 2 took
all 73 suites green in 18 m 14 s. Read on its own that table says the divisor is one too large. Read
with the section below it says something else: at `8548ee9` a slot could be spent on a suite holding
a whole k3s, and **nothing stopped a second and a third being held beside it**, so "degree 3" could
mean three concurrent Kubernetes control planes and "degree 2" only made that overlap one slot less
likely. Lowering the divisor would have bought green by narrowing a window. The cluster cap closes
it, and the 3 keeps meaning what it has meant since 2026-08-20.

⚠ **What would make the three stale** is what always has: a change to what one *non-cluster*
container-backed suite costs — a fifth of them, or one of the four gaining a second heavy container.
A new `*.Cluster.Conformance` suite no longer makes it stale, which is the property #77 asked for.

⚠ **What the derivation models is CPU, and it is worth naming the two things it does not.** Memory
is the obvious other candidate, and the tree cannot observe it honestly — on Linux the daemon shares
the host's RAM, while on macOS and Windows it lives in a VM whose allocation a .NET process can only
learn by asking `docker info`, which is a subprocess that hangs when the daemon is unhealthy, inside
the target whose job is to tell a starved host from a broken one. The other is how many k3s clusters
a daemon will hold, which has no API at all — and that one is no longer left to this arithmetic; see
the next section. The environment variable is the lever for what remains. A derivation that is wrong
on some host is fine *because* the override exists and the failure message names it; a constant is
wrong on every host but the one it was measured on, and says nothing.

### The cluster degree is 1, and it is not tuned

`Build.Test.cs` § `ClusterBackedSuiteDegree` is the constant **1**, and unlike every other number in
that file it is neither measured nor derived nor overridable, because it is not a property of the
host. It is the invariant fifteen of the seventeen cluster-backed assemblies already keep among
themselves: `ClusterSlot`, in
[`test/CyberCloud.Cluster.Conformance/Infrastructure/ClusterInfrastructure.cs`](../test/CyberCloud.Cluster.Conformance/Infrastructure/ClusterInfrastructure.cs),
is a lock file taken before the containers and held until the process exits — "however many of them
a run contains, at most one is holding a k3s container at a time". The constant is `build/` finally
being told that permit exists.

⚠ **Which is worth doing even though the permit already works, because two suites are outside it and
both were invisible to the build before #77:**

* `CyberCloud.Kubernetes.Tests` starts its own k3s through `K3sFixture` and takes **no** permit —
  `ClusterInfrastructure`'s remark says so in as many words.
* `CyberCloud.AppHost.Tests` starts a k3s too, through Aspire rather than Testcontainers, on the
  fixed host port **6443**. It takes a machine-wide lock of its own,
  `cybercloud-apphost-local-topology.lock`, which is a **different file** from `ClusterSlot`'s and
  therefore excludes nothing but a second copy of itself. It shipped no `Testcontainers` assembly,
  so the old `StartsContainers` glob called it *cheap*: it ran entirely ungated.

Three disjoint answers to "may I hold a cluster?", so three k3s API servers could be live at once
underneath a cap that said "three container suites". That is the arithmetic #77 measured.

⚠ **There is deliberately no `CC_TEST_CLUSTER_PARALLELISM`.** An override that cannot take effect is
worse than none: raising the degree to 2 would still leave fifteen of the seventeen queued behind
`ClusterSlot`, so the setting would appear to work, change almost nothing, and be believed. A host
that genuinely holds two clusters needs the constant **and** `ClusterSlot`'s permit count moved
together.

⚠ **The cost, in full.** This section read *"the cost is smaller than it looks … the cap does not
lengthen that chain"* until the #77 review, and on the machine that matters that was false. Fifteen
of the seventeen were already serial through `ClusterSlot`, so the cap does not lengthen *that*
chain — but stopping the other two from overlapping it is the same arithmetic as adding their wall
clock to it. `CyberCloud.Kubernetes.Tests` and `CyberCloud.AppHost.Tests` now queue where they used
to run beside it, which is the point rather than a side effect: the overlap is the defect #77
measured, and there is no way to keep the fix and not pay for it. It does hand the container budget
back to the four suites that can use it, and the cluster-backed suites are now ordered first,
because a serial chain about as long as the whole gate has to start at *t* = 0 rather than whenever
a worker happens to reach one.

⚠ **On CI the cost is bigger, and it does not come from this cap at all.** GitHub's hosted
`ubuntu-24.04` runner — which `.github/workflows/gate.yml` pins — has 2 or 4 vCPUs, and
`ContainerBackedSuiteDegree` is `Math.Max(1, ProcessorCount ÷ 3)`, so on either it is **1**: every
container-backed suite was *already* strictly serial there, on master, before #77. The cluster cap
constrains nothing on a runner, because cluster-backed implies container-backed and a container
degree of 1 admits one suite at a time by itself. What changes on CI is the **set**:
`CyberCloud.AppHost.Tests` ships no `Testcontainers` assembly, so master's glob ran it ungated, and
the `or` in `StartsContainers` now puts it into the chain. Up to its own wall clock joins the
critical path — its `.csproj` calls the suite slow and "a large fraction of" the `Test` budget, and
it measured **2 m 43 s** warm on a ten-CPU host, which a cold small runner will not beat.

⚠ **That is a real charge against a real budget and it has not been measured on a runner.**
`gate.yml` gives the `test` job `timeout-minutes: 30`, and `.github/scripts/assert-budget.sh` fails
the pipeline past the **25 minutes** `pr.yml` § `BUDGET_MINUTES` sets — so this can cost a PR rather
than only a wait. **The lever is not the cluster degree.** Letting it track the container degree, so
that the cap "can never serialise more than master did", is worth nothing twice over: on CI it
changes nothing, because the degree there is already 1 and the extra suite comes from the
classifier; on a ten-CPU host it would set the cluster degree to **3**, which is precisely the
three-concurrent-k3s overlap #77 exists to close. If the budget does go red, `assert-budget.sh`
names the job that spent it and docs/plan/23 § CI shape prescribes parallelism or a move to nightly
with a written reason — the move being `CyberCloud.AppHost.Tests`, whose own `.csproj` argues
against it, and the untried parallelism being `MaxDegreeOfParallelism`, which is
`Environment.ProcessorCount` today and pins a worker per *waiting* suite.

### Which suites are container-backed comes from the build output, not from the `.csproj`

⚠ **This used to grep each project file for the word "Testcontainers", and the cap was therefore not
capping what it said it was.** Measured over this tree on 2026-08-20: **28** project files contain
the word, **19** suites actually ship the assemblies, and it was wrong in both directions.

* **Three suites that each hold a whole k3s cluster were invisible to it** —
  `CyberCloud.Providers.{ContainerService,Network,Terminal}.Cluster.Conformance` reach Testcontainers
  through a project reference, so their own file never says the word. They ran ungated, at the
  suite-level degree, *on top of* whatever the semaphore was letting through: a nominal cap of 3 was
  really up to 6. In the run that found this, two of those three were still sitting at **zero tests
  started after four minutes** while every other suite had finished — which is one of the four
  symptoms the failure message names.
* **Twelve suites that start no container were holding slots**, several of them because their
  `.csproj` carries a ⚠ comment explaining that they deliberately do *not* use Testcontainers.
  `CyberCloud.Identity.Tests` says "NO Testcontainers" in capitals and was gated for saying so.

The evidence is now a `Testcontainers*.dll` in the suite's own output directory — the same reasoning
as `CoverageReport.cs` § `CoverableLines` counting sequence points in a PDB rather than parsing a
tool's stdout. A comment cannot fool it, a transitive reference cannot hide from it, and it goes
right on its own the next time somebody adds a container to a suite through a shared fixture. A
suite with no build output is treated as container-backed, which is the direction that costs wall
clock rather than a starved run.

⚠ **Since #77 that degraded case is three times more expensive, and the warning says so.**
`StartsCluster` answers the same missing directory the same safe way, so a suite that lands there is
gated on **both** permits. A cleaned or stale `artifacts/` used to mean "every suite is
container-backed", and the gate then ran at the derived degree — 3 on a ten-CPU host. It now means
every suite is cluster-backed too, and the whole gate is strictly serial. The only symptom either
way is a slow gate, which is the thing people wait out rather than investigate, so the warning names
the cluster verdict and the cost of it explicitly.

⚠ **"Testcontainers" was still too narrow a piece of evidence, and #77 is what that cost.**
`CyberCloud.AppHost.Tests` brings up Redis, PostgreSQL, NATS *and* a k3s through Aspire and ships not
one Testcontainers assembly, so the glob called it cheap — the same failure as the `.csproj` grep it
replaced, one library further along: **a check that answers a narrower question than it appears
to**. `Build.Test.cs` § `StartsCluster` is the answer, and `StartsContainers` is now an `or` over it,
so *cluster-backed implies container-backed* holds by construction rather than by two globs that
happen to agree.

`StartsCluster` looks for `Testcontainers.K3s*.dll` or `Aspire.Hosting.Testing*.dll`. The second is
an inference and is exact on this tree: a suite shipping it starts a `DistributedApplication`, the
only one here is `CyberCloud.AppHost`, and ADR-014 puts a k3s in it. What would make it stale is a
second `DistributedApplication` in this repository with no cluster in it — and the fix then is to
read the app host's resources, not to add a project name to a list.

⚠ **It deliberately does not ask whether a suite takes `ClusterSlot`.** Two of the seventeen do not,
and they are precisely the two whose overlap #77 measured. The evidence has to be the cluster, not
the promise about it.

The literal `Aspire.Hosting.Testing` in `build/` is checked from the other side by
`CyberCloud.AppHost.Tests` § `ClusterBackedGatingTests`, which spells the same name against the type
that actually starts the topology — the defect class `GenerationReportTests` exists for, one
directory over.

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
