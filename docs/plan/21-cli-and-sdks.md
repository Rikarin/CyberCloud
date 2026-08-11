# 21 — CLI and SDKs

Both are generated from the provider registry (ADR-012). Neither is hand-maintained per resource type,
because 100 resource types × 2 surfaces × N versions is not a thing humans keep correct.

## `cyc` — the CLI

Modelled on `az`, per the brief. .NET 10, `System.CommandLine` 2.0.10, single-file AOT-published per
RID so there is no runtime prerequisite.

### Grammar

```
cyc <group> <subgroup...> <verb> [--flags]

cyc login [--device-code | --service-principal --tenant T --client-id C --certificate P]
cyc account list | set --subscription S | show
cyc group create --name prod --location eu-central
cyc postgres server create --name main --resource-group prod \
      --version 18 --replicas 2 --size s1.large --cluster my-cluster
cyc postgres server list --resource-group prod --output table
cyc postgres server connection-string show --name main --resource-group prod
cyc aks create --name prod --node-count 3 --node-size c1.large --wait
cyc vault secret set --vault v1 --name db-password --value-from-stdin
cyc shell                                  # attach to the cloud terminal
cyc resource list --tag env=prod --output json
cyc rest --method GET --uri /tenants/…     # the escape hatch for anything not yet a verb
```

**Groups are generated from the provider registry**, with an alias table for the short forms people
expect (`aks` → `containerservice managed-cluster`, `postgres` → `dbforpostgresql server`). The alias
table is the *only* hand-maintained part of the CLI's surface and it is small.

### Decisions

| Decision | Rationale |
|---|---|
| `--output table\|json\|yaml\|tsv\|none` | `table` for humans, `json` for scripts. `tsv` because `cut` exists and people use it |
| `--query <JMESPath>` | Azure CLI's convention; a huge productivity feature and a well-specified language |
| `--wait` / `--no-wait` | Every LRO. `--wait` streams the operation's progress array ([08](08-resource-manager.md)) — this is what makes a nine-minute cluster creation bearable in a terminal |
| Exit codes | `0` ok · `1` client error · `2` usage · `3` auth · `4` server · `5` timeout. Documented, stable, so CI can branch on them |
| Token cache | OS keychain (DPAPI / Keychain / libsecret). ⚠ **Never a plaintext file** — that is how CI credentials leak into container images |
| Config | `~/.cyc/config` with named profiles; every setting also an env var (`CYC_SUBSCRIPTION`, …) for CI |
| Completion | bash, zsh, fish, pwsh — generated |
| Telemetry | **Opt-in, off by default, and asked once.** Opt-out telemetry in a developer tool is a trust cost that is never worth the data |
| Update check | Once a day, non-blocking, never auto-installs |

⚠ **`cyc rest` matters more than it looks.** A generated CLI always lags the API by a release; without a
raw escape hatch the answer to "how do I call the new endpoint" is "wait". With it, the CLI is never a
blocker.

### Extensions

~~`cyc extension add <name>` loads a NuGet-packaged command group into an `AssemblyLoadContext`.~~ This
is how a provider ships CLI verbs that are not schema-shaped (`cyc shell`, `cyc postgres connect` which
actually spawns `psql`), and how third parties extend the CLI without a fork.

⚠ **DEFECT 2026-08-12 — the mechanism above contradicts § `cyc` above it, and the goal survives while
the mechanism does not. Needs a decision; nothing is built.**

§ `cyc` requires `cyc` to be **single-file AOT-published per RID**, and `cli/cyc/cyc.csproj` implements
that with `IsAotCompatible` and `EnableAotAnalyzer`. A NativeAOT binary has no JIT and cannot load a
managed assembly at run time. Measured on osx-arm64, SDK 10.0.302, rather than reasoned about:

| Probe | JIT | NativeAOT |
|---|---|---|
| `AssemblyLoadContext.Default.LoadFromAssemblyPath` | loads, `GetTypes()` == 1 | `PlatformNotSupportedException` |
| a custom collectible `AssemblyLoadContext` | loads | `PlatformNotSupportedException` |
| `Assembly.LoadFrom` | loads | `PlatformNotSupportedException` |
| `AssemblyLoadContext.Default.LoadFromStream` | loads | `PlatformNotSupportedException` |
| invoking a method on the loaded type | the plugin's code runs | `PlatformNotSupportedException` |

The plugin file was present on disk in every AOT case, so this is a refusal and not a missing file. It
throws — it does not degrade, and there is no narrow case that works.

⚠ **It fails earlier than run time.** `TreatWarningsAsErrors` (Directory.Build.props) plus
`EnableAotAnalyzer` means the call does not compile: adding one `LoadFromAssemblyPath` to `cli/cyc`
fails an ordinary `dotnet build` with `error IL2026`. The mechanism cannot be written down in this
project, let alone shipped.

**Where the mistake came from.** `az` is the model for this CLI (§ `cyc`, first line), and an `az`
extension *is* loaded in-process — `pip install` into `~/.azure/cliextensions`, then a Python `import`
that merges commands into the host's command table. That works because `az`'s host is already an
interpreter, so it gets dynamic loading for free. `AssemblyLoadContext` is that model transliterated
into .NET, and the transliteration is what breaks: an AOT-compiled host does not get it for free, and
cannot get it at all. `git`, `kubectl` and `gh` all use out-of-process executables instead — and
`kubectl` *abandoned* a manifest-based in-process design for that convention.

**Two resolutions, and this is a user decision:**

| | Keeps | Costs |
|---|---|---|
| **(a) Out-of-process**, `git-*` / `kubectl-*` style: `cyc-<name>` executables discovered by naming convention, invoked as child processes | Single-file AOT, the whole § `cyc` publish model, and the `Azure.Core` decision that depends on it | Rewrites this section. Extensions cannot add flags to *existing* generated verbs, only new groups. `cyc shell` is unaffected — it spawns a terminal either way |
| **(b) Revisit AOT** | This section verbatim | Reopens a decision taken deliberately on 2026-08-11 ([25](25-risks-and-open-questions.md) open question 5), whose stated justification *was* single-file AOT. Costs the no-runtime-prerequisite property |

(a) is the cheaper half to give up and the one the precedents converged on, but it is still a change to
a written plan and it is not the CLI's to make unilaterally.

⚠ **Two decisions downstream of this one, recorded so they are not made by accident if (a) is taken:**

1. **The trust boundary.** Any `PATH`-discovered `cyc-foo` runs with the user's credentials. The
   precedents differ and none of them sandboxes: `kubectl`/`krew` verifies a sha256 from a manifest and
   prints "not audited for security"; `gh` says extensions are "not verified, signed, or endorsed by
   GitHub"; `az` verifies a digest for indexed installs, **no** digest for `--source`, and uniquely
   lets an extension *replace a built-in* behind a runtime warning. `cyc`'s reserved-group list
   (`CommandTree.ReservedGroups`) already refuses a *generated* group that shadows a host command; an
   extension model needs the same rule and it does not have it yet.
2. **Credentials.** ⚠ Do not pass a raw token in the child's environment — it lands in `ps` output and
   in shell history via `env`. `gh`'s design is the one to copy: the token stays in the host's keychain
   and the extension shells back out to `gh auth token`. `cyc` already has the pieces — the SDK owns
   the credential and `cyc account` already exists.

**The unimplemented half is load-bearing on documentation elsewhere.** `VerbTree/VerbTreeCatalog.cs`
gives "`cyc extension add` loads a command group out of an `AssemblyLoadContext`" as the *third* of
three reasons the verb tree is built at run time rather than compiled to C#. The other two reasons —
the emitter/host split, and `--api-version` selecting between trees — are unaffected and still carry
the decision, but that third reason is void and should not be cited again.

## The .NET SDK

Per the brief: shaped like the Azure SDK, because that mental model is widely held and the
conventions are good ones.

```csharp
var credential = new CyberCloudCliCredential();      // or ClientSecret / Certificate / WorkloadIdentity / Interactive
var client = new CyberCloudClient(credential);

SubscriptionResource sub = await client.GetSubscriptionAsync(subscriptionId);
ResourceGroupResource rg = await sub.GetResourceGroups().GetAsync("prod");

var data = new PostgresServerData(AzureLocation: "eu-central")
{
    Version  = PostgresVersion.V18,
    Replicas = 2,
    Sku      = ResourceSku.Parse("s1.large"),
    ClusterId = clusterId,
};

Operation<PostgresServerResource> op =
    await rg.GetPostgresServers().CreateOrUpdateAsync(WaitUntil.Started, "main", data);

await foreach (var progress in op.GetProgressAsync())      // ← ours; Azure's SDK has no equivalent
    Console.WriteLine($"{progress.PercentComplete}% {progress.Message}");

PostgresServerResource server = await op.WaitForCompletionAsync();
```

⚠ **"Source" below means *whose idea*, not whose package.** Every one of these is **implemented
here**, in our own namespace — see the decision that follows the table. Nothing named `Azure.*`
appears in the dependency graph.

| Convention | Shape borrowed from | Implemented |
|---|---|---|
| A `TokenCredential`-shaped credential | Azure.Core's abstraction — async, token + expiry, chainable | **Ours** |
| `Response<T>` / `NullableResponse<T>` | Azure.Core | **Ours** |
| `Operation<T>` + `WaitUntil` | Azure.Core | **Ours** |
| `AsyncPageable<T>` — `await foreach` over paged lists | Azure.Core | **Ours** |
| `{Type}Resource` / `{Type}Collection` / `{Type}Data` | Azure.ResourceManager | **Ours**, generated (§ Generation) |
| `GetProgressAsync()` | **Nobody's.** Azure's LROs expose no progress; ours do ([08](08-resource-manager.md)) and the SDK should not hide it | **Ours** |
| Retry, `Retry-After` on 429, correlation ids | — | **Ours**, over `Polly` 8.6.5, already in the register |

⚠ **Do we depend on `Azure.Core` or reimplement it?** ~~Decision: depend on it.~~
**DECIDED 2026-08-11: reimplement it. We take the shapes and own the code.**

This row was never a decision — [25](25-risks-and-open-questions.md) listed it as open question 5
under *"Default if unanswered"*, and the default was `Azure.Core`. Asked directly, the answer was no.
The original justification did not survive examination:

| Claim | Verdict |
|---|---|
| "a developer's existing `TokenCredential` implementations transfer directly" | **False.** `DefaultAzureCredential`, `ManagedIdentityCredential` and `AzureCliCredential` authenticate against **Entra**, not our identity server. The table above says `DefaultAzureCredential`-**shaped**, which is the honest word; this paragraph then claimed more than the table did |
| "brings the retry/pipeline/diagnostics machinery for free" | **Weakened.** `Polly` 8.6.5 is already in the register for the fabric's cluster connections. The choice was never Azure.Core versus writing retry from scratch |
| "the cost is a dependency named Azure — a cosmetic objection" | **The weakest objection was the only one listed.** The real costs are trim- and AOT-hostility, coupling our release cadence to Azure's, and pulling `System.Diagnostics.DiagnosticSource`, `System.Memory.Data` and `System.ClientModel` into a CLI that is meant to be one self-contained file |

**The genuine benefit was the *shapes*, and that is what we keep** — `Response<T>`, `Operation<T>` +
`WaitUntil`, `AsyncPageable<T>`, and a `TokenCredential`-shaped credential abstraction, all in our own
namespace, over `Polly` and `HttpClient`. An Azure-SDK user stays instantly productive; nothing named
`Azure.*` appears in the graph. `GetProgressAsync()` was already ours, and now the whole poller is.

⚠ **The decision is also what makes the CLI's own requirement reachable.** § `cyc` above requires
single-file **AOT** publication per RID, and the CLI depends on this SDK
([§ Generation](#generation) — one pipeline, not two). `Azure.Core` is the trim-hostile part of that
graph. Owning the stack means owning the serialization: source-generated `System.Text.Json`
throughout, and an AOT warning becomes a bug we can fix rather than a dependency we must live with.
Those two plan requirements were in direct tension and nothing had noticed, because nothing had built
both halves.

### Generation

`Build.Generate` walks the provider registry → OpenAPI 3.1 → the SDK's models, clients and pollers, via
a Roslyn-based generator we own rather than an off-the-shelf OpenAPI generator. Owning it costs ~0.5 EM
and buys idiomatic output, our `Operation<T>` shape, and no vendored template language.

**Hand-written on top:** the credential types, the pipeline policies, the convenience methods
(`GetConnectionStringAsync`), and the tests. Everything else is regenerated per release and never
edited.

## Other SDKs

| Language | M | How |
|---|---|---|
| **.NET** | M1 | Above |
| **TypeScript** | M1 | Already generated for the portal (`portal/libs/api`); publishing it is packaging, not work |
| **Python** | M2 | Generated; `boto3`-shaped resource clients. The second-most-asked-for language in infrastructure |
| **Go** | M2 | Generated. The language the Terraform provider needs anyway |
| **Terraform provider** | M3 | Generated from the same registry. ⚠ Provider-schema generation is not free — CRUD + import + drift + state upgrades is ~1.5 EM even generated |
| Java, Rust, PHP | P1 | On request |

## OpenAPI

The generated document is the contract, published per api-version at `/openapi/{version}.json`, and it
is a **build artifact that is diffed**: a breaking change to a published version fails CI. The diff
rules are explicit — adding an optional field is fine, removing anything or narrowing a type is not.

That gate is what makes the api-version discipline in [08](08-resource-manager.md) real rather than
aspirational.

## Effort

| Piece | EM |
|---|---|
| Generation pipeline: registry → OpenAPI → emitters (shared with the portal, counted once in [08](08-resource-manager.md)) | — |
| `cyc`: hosting, auth, output formats, JMESPath, `--wait`, config, completion, extensions | 1.5 |
| .NET SDK: generator, credentials, pipeline, `Operation<T>` progress, docs, samples | 1.5 |
| TypeScript packaging | 0.2 |
| Python + Go | 1.0 (M2) |
| Terraform provider | 1.5 (M3) |
| **M1 total** | **3.2** |
