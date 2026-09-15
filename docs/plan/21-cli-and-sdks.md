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

**`cyc extension add` installs an executable; `cyc <name>` runs it as a child process.** The `git-*` /
`kubectl-*` shape, with the discovery half narrowed — see § The trust boundary. This is how a provider
ships CLI verbs that are not schema-shaped (`cyc postgres connect`, which actually spawns `psql`) and
how third parties extend the CLI without a fork.

```
cyc extension add --source ./cyc-mytool     # copies into ~/.cyc/extensions, hashes it, warns once
cyc extension list                          # name, state, path, sha256 — the only discovery surface
cyc extension remove mytool
cyc mytool --whatever                       # runs ~/.cyc/extensions/cyc-mytool as a child process
```

~~`cyc extension add <name>` loads a NuGet-packaged command group into an `AssemblyLoadContext`.~~
**DECIDED 2026-08-12: out of process. AOT stays.** The struck-through mechanism contradicted § `cyc`
above it, forty-six lines away, and the two could not both be true.

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
throws — it does not degrade, and there is no narrow case that works. ⚠ **It fails earlier than run
time.** `TreatWarningsAsErrors` plus `EnableAotAnalyzer` means the call does not compile: adding one
`LoadFromAssemblyPath` to `cli/cyc` fails an ordinary `dotnet build` with `error IL2026`.

**Where the mistake came from.** `az` is the model for this CLI (§ `cyc`, first line), and an `az`
extension *is* loaded in-process — `pip install` into `~/.azure/cliextensions`, then a Python `import`
that merges commands into the host's command table. That works because `az`'s host is already an
interpreter, so it gets dynamic loading for free. `AssemblyLoadContext` is that model transliterated
into .NET, and the transliteration is what breaks. `git`, `kubectl` and `gh` all use out-of-process
executables instead — and `kubectl` *abandoned* a manifest-based in-process design for that convention.

⚠ **What the decision costs, in the plan's own terms.** An extension can add a new group and nothing
else: it cannot add a flag to an existing generated verb, because the generated verb is compiled into
a binary that will not load its code. No out-of-process model can do that. `cyc shell` is unaffected —
it spawns a terminal either way, and is unbuilt for its own reasons
([19](19-cloud-terminal-and-virtual-desktop.md)).

#### The trust boundary

**Only what `cyc extension add` installed into `~/.cyc/extensions` runs. `PATH` is never searched.**

This is the one place `cyc` departs from the convention it otherwise copies, and the reason is the
credential. `git` and `kubectl` run anything named `git-foo` / `kubectl-foo` that turns up anywhere on
`PATH`. Under that rule the set of programs that can spend the user's cloud budget equals the set of
programs in *any* writable directory on `PATH` — a set the user never chose, cannot enumerate and
cannot revoke. `gh` is the precedent taken instead: an owned install directory, an explicit install
step.

The precedents differ and **none of them sandboxes**: `kubectl`/`krew` verifies a sha256 from a
manifest and prints "not audited for security"; `gh` says extensions are "not verified, signed, or
endorsed by GitHub"; `az` verifies a digest for indexed installs, **no** digest for `--source`, and
uniquely lets an extension *replace a built-in* behind a runtime warning.

What the choice costs, stated rather than glossed:

| Cost | |
|---|---|
| **No zero-install extensions** | Dropping `cyc-foo` on `PATH` does nothing at all. Every extension is an explicit `cyc extension add`, and a distribution that wants one installed has to say so |
| **Install is the trust decision, and there is no second one** | An installed extension is an ordinary child process running as the user. It can read the keychain by running `cyc account get-access-token`, exactly as the user can. `cyc extension add` says so once, where it is actionable, and names the specific thing being handed over |
| **The recorded hash catches an accident, not an adversary** | The sha256 is re-verified on every invocation, which catches a binary replaced without the index being rewritten. It does **not** stop somebody who can write the directory, because they can rewrite `index.json` in the same breath |
| **Permissions are checked on Unix and not on Windows** | A group- or world-writable `~/.cyc` or `~/.cyc/extensions` is refused — that is the `PATH` model wearing a different hat. `FileSystemInfo.UnixFileMode` says nothing about a Windows ACL, so on Windows the model rests on the profile directory's default ACL and this check does not run |
| **The index is the authority, not the directory listing** | A `cyc-foo` copied into the install directory by hand is refused *and reported*, rather than run or silently ignored |

#### Credentials across the process boundary

⚠ **No token is put in the child's environment.** It would land in `/proc/<pid>/environ`, in every
grandchild the extension spawns, in a crash dump, and in any CI step that prints its environment.
`gh`'s design is the one copied: the token stays in the OS keychain and the extension asks for one.

`cyc` passes **context** — `CYC_PROFILE`, `CYC_ENDPOINT`, `CYC_SUBSCRIPTION`, `CYC_TENANT`,
`CYC_API_VERSION`, `CYC_OUTPUT`, `CYC_EXTENSION` — and `CYC_EXECUTABLE`, the **absolute path** of the
running `cyc` rather than the word `cyc`, so the extension cannot be steered to a different binary
through `PATH`. The extension re-authenticates by running `$CYC_EXECUTABLE account get-access-token
--output json`.

⚠ **That contract already existed.** `CyberCloudCliCredential` issues exactly that command and
branches on exit code 3 (§ The .NET SDK; `cli/cyc.Tests/SdkContractTests.cs` holds both ends), so a
.NET extension writes `new CyberCloudCliCredential()` and is done. The CLI grows no credential
mechanism of its own — the SDK still owns HTTP and OAuth.

⚠ **The rest of the environment is inherited, deliberately.** A `CYC_CLIENT_SECRET` that CI exported
is visible to the child, and stripping it would be theatre: the extension already runs as the user,
with the user's files and the user's keychain. The narrower rule that *is* kept: `cyc` never
materializes fresh credential material into a place it was not already. Inheriting an exposure is not
the same as creating one.

#### Shadowing, and why it is not `ReservedGroups`

An extension is looked for **only after `System.CommandLine` has already failed to match the verb**.
`cyc login`, `cyc rest` and every generated group reach their own code first, so an extension named
`login` is unreachable rather than dangerous — the property is structural, not a check that could be
got wrong. `cyc extension add` refuses such a name anyway, and `cyc extension list` reports one that
arrived some other way as `shadowed`.

⚠ **The check deliberately does not live where `CommandTree.ReservedGroups` lives.** That one
*throws*, while the root command is built, so a colliding group takes down `cyc --help` along with
everything else (`ReservedGroupTests.RefusingATreeCostsTheWholeCli` pins that blast radius). That is
the right cost for a generated tree, which is ours and whose collision is a build defect. It is the
wrong cost entirely for a name that came out of a directory a user can write: **a file called
`cyc-login` must not be able to disable the CLI.** So the generated tree fails loudly and an extension
is refused quietly.

The reservation is also no longer holding a name open for nothing: `extension` now has a command
behind it.

#### What is deliberately not built

| | Why |
|---|---|
| **No registry, no `--source <url>`** | No document names an extension feed, and a downloader would put a general-purpose HTTP client into a CLI whose stated position is that `CyberCloud.Sdk` owns HTTP. `--source` is a local path; fetching is `curl`'s job. The refusal says so rather than failing with a file-not-found |
| **Extensions are absent from `cyc --help` and from completion** | Listing them would mean an index read on every invocation, including the `cyc account get-access-token` the SDK runs for every uncached token. `cyc extension list` is the discovery surface, as `kubectl plugin list` is |
| **An extension's exit code is passed through unchanged** | § Decisions' six codes are a contract for `cyc`'s own commands. There is no mapping from an arbitrary program's codes onto that table that does not throw information away, so an extension owns its codes the way a `git-` subcommand does. Everything that fails *before* the child starts still uses the table |

**The unimplemented half was load-bearing on documentation elsewhere, and that is now cleared.**
`VerbTree/VerbTreeCatalog.cs` gave "`cyc extension add` loads a command group out of an
`AssemblyLoadContext`" as the *third* of three reasons the verb tree is built at run time rather than
compiled to C#. The other two — the emitter/host split, and `--api-version` selecting between trees —
were always sufficient, and out-of-process extensions add nothing to any tree, so that third reason
stays void and should not be cited again.

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

#### What the emitters agree on

**The OpenAPI document is the shape.** A body is nested on the wire —
`{"location":…,"properties":{"persistence":{"mode":"AOF"}}}` — and each surface renders that nesting
the one way its medium allows, keeping the document's own member name as the wire name at every depth:

| Surface | A container (`/properties/persistence`) | A leaf (`/properties/persistence/mode`) | Wire name |
|---|---|---|---|
| **.NET SDK** | A nested `partial` class, `{Model}Data.PropertiesData.PersistenceData`, held by a property `Persistence` that is `required` or nullable as the document says and never initialised | `Mode` on that class | `[JsonPropertyName("mode")]` — the leaf's own name, correct because the class nests |
| **TypeScript** | An inline object type, `persistence?: { … }` | `mode` on it | The member name itself |
| **`cyc`** | Nothing — a command line has no nesting | `--mode`, and `--persistence-mode` only when a flat name collides | The flag carries the JSON pointer and writes it |
| **Portal forms** | Nothing — one field per property | One field, keyed on the pointer | The field carries the JSON pointer |

**The read envelope is in the document too, and every surface reads it from there** (#85). A
resource is read in Azure's envelope — `id`, `name`, `type`, `location`, `provisioningState`,
`etag`, then the body's `properties`, then `tags` — and until #85 the document described none of the
five the server owns: every `GET` `200` and list element referenced the write body, which said
`additionalProperties: false` over three members, so a client validating a response rejected every
resource it read. Now every type's schema is `allOf` a shared `Resource` component and repeats its
five members as `readOnly` — the shape Azure's own resource-manager specs have — and the `202` every
write returns declares the body it always carried. `DocumentReader` splits the schema once, into the
write body (`Body`) and what it inherits (`Envelope`), and:

| Surface | The write body | The read envelope |
|---|---|---|
| **.NET SDK** | `{Model}Data`, as above | `{Model}Resource` — `Id`, `Name`, `Type`, `ProvisioningState`, `Etag` with their wire names, plus `Data`; `ProvisioningState` is a file-level enum from the same component |
| **TypeScript** | `{Model}Data`, as above | `{Model}Resource extends Resource, {Model}Data`, with `type` narrowed to the literal; `Resource` and `ProvisioningState` emitted once |
| **`cyc`** | The flags | Nothing — no `--etag` on `create`, and that absence is asserted |
| **Portal forms** | The fields | Nothing — no `id` control, likewise asserted |

⚠ **Why one schema with `readOnly` rather than a `{Type}.Resource` the `200` points at.** The
[§ OpenAPI](#openapi) gate treats every changed scalar in a published document as breaking, and the
`$ref` a `200` already carries is a scalar; adding an `allOf`, five properties, a component and a
`202` body is what it calls an addition. The other shape would have needed a new api-version for a
document that was wrong from its first day. What the shape costs: none of the five can be
`required`, because the same schema validates a `PUT` and this platform *refuses* a read-only member
on a write rather than ignoring it (OpenAPI 3.1.1 § Validating readOnly and writeOnly assumes the
opposite). `x-cybercloud-read-required` on `Resource` and on `OperationStatus` carries the read-side
promise instead, and both client emitters type the members as present because of it.
`ServedShapesMatchTheDocumentTests` in the gateway suite validates every served body — a read, a list,
the three `202`s, a running and a failed operation, a scope — against the document emitted from the
same registry.

⚠ **The .NET `{Model}Resource` said `Id` and nothing else until #85**, and the TypeScript one said
`id`, `name`, `type` and `properties`; both were literals in the emitter, and the portal typed
`provisioningState`, `location`, `etag` and `tags` by hand in `resource-verbs.ts`. The hand-written
`ResourceEnvelope` there now extends the generated `Resource`.

⚠ **The .NET row said something else until 2026-09-15, and the difference was a defect.** The SDK
flattened every leaf onto one class, so `/properties/persistence/mode` was `PersistenceMode` with
`[JsonPropertyName("mode")]` — the same wire name as the top-level `mode`, on one type.
`generated/sdk/2026-08-01.cs` carried **fourteen** such duplicates across eight types; it compiled, so
the *Generated SDK compiles* gate (#73) was green over it, and `System.Text.Json` would have thrown on
the first serialisation of each type. The TypeScript client had nested from the start, so the two
generated clients disagreed about the shape of one API's bodies (#79). The gate now reads every
`[JsonPropertyName]` off the checked-in file and refuses a name declared twice by one type, which is
the check a compiler cannot make.

⚠ **The worked example in § The .NET SDK is the brief's sketch, not the emitted shape.** A generated
body is `new PostgresServerData { Location = "eu-central", Properties = new() { … } }` — the
`properties` envelope is a container like any other, because the document says it is. #72 settled
what the server puts inside it — the projected document, member for member, spliced into the read
envelope — and #85 put that envelope into the document.

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
