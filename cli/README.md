# `cli/` — `cyc`

The Cyber Cloud command-line interface. .NET 10, `System.CommandLine` 2.0.10, single-file
AOT-published per RID, kept out of [`src/`](../src) because it **ships separately** — it is packaged,
signed and released on its own cadence.

See [docs/plan/21](../docs/plan/21-cli-and-sdks.md) § `cyc`.

## `cyc`, not `cc`

`cc` is the POSIX name of the system C compiler (`/usr/bin/cc`) and `CC` is the standard `make`
variable, so a CLI called `cc` shadows a toolchain on every Unix box that installs it. Config lives
in `~/.cyc/`, the environment prefix is `CYC_*`.

⚠ The platform's *other* uses of `cc` — the NATS subject prefix `cc.{tenant}.…` and the Redis hash
tag `{cc:t:<id>}` — are unrelated and are not renamed.

## The project is `cyc.csproj`

Not `CyberCloud.Cli.csproj` with an `AssemblyName` override. `Directory.Build.props` § "Assembly and
namespace naming" derives the assembly name from the project name, and
`build/Build.Architecture.cs` § `ShippingAssemblyPaths` looks for
`artifacts/bin/<project>/<config>/<project>.dll` — so an override would leave every architecture
gate reporting "no built assembly" for a project that built fine. The root namespace is
`CyberCloud.Cli`.

## The verb tree is generated; the host is not

`Build.Generate` walks the provider registry → OpenAPI → `generated/cli/{api-version}.json`
(ADR-012). That file describes the groups, the resource types, their aliases, the verbs, every flag
with its `choices`, `jsonPointer`, `repeated`, `secret` and `immutable`, the wait flags and the exit
codes. **`cyc` reads it; nothing in `cli/` restates it.**

**Embedded at build time, built into a command surface at run time.** The `.csproj` takes
`generated/cli/*.json` as embedded resources and `VerbTreeCatalog` reads them at start-up. Three
reasons, in order of weight:

1. A published `cyc` is one self-contained file per RID. There is no directory beside it to read a
   `.json` out of.
2. `--api-version` selects between trees, and every published api-version is kept forever
   ([10](../docs/plan/10-gateway-and-api.md) § API versioning). A compiled-in tree could only be the
   one version it was compiled from.
3. `CliEmitter`'s own remarks rule out the alternative: *"A generator that emitted C# command classes
   instead would fuse the two and make every CLI behaviour change a generator change."*

⚠ **The alias table is generated.** docs/plan/21 § Grammar calls it *"the only hand-maintained part
of the CLI's surface"*; that is no longer true — the registry carries `shortName`, the emitter puts
it in the tree, and `CommandTree` adds it. There is no alias list in this directory and
`GeneratedSurfaceTests` checks there is not.

## It owns no HTTP and no OAuth

Every request `cyc` makes — including `cyc rest` — goes through `CyberCloudPipeline`, so it is
authenticated, retried and correlated by the same code the SDK's own callers use. **Raw means
untyped, not unauthenticated.**

Every OAuth grant, discovery, JWKS, PKCE, refresh with reuse detection, and the token cache including
the OS keychain implementations are [`src/CyberCloud.Sdk`](../src/CyberCloud.Sdk)'s. `cyc login`
parses flags, chooses a credential, prints the user code, opens a browser and reports — there is no
HTTP call in it.

⚠ **No credential is ever written to `~/.cyc/config`.** `CycConfigFile.Set` refuses a
credential-shaped key. The refresh token goes to the OS keychain, through the SDK.

## Exit codes

Stable, documented, and asserted against the generated tree's own `exitCodes` table.

| Code | Meaning |
|---|---|
| `0` | ok |
| `1` | client error — a `4xx` that is not an auth failure, or a request that never left |
| `2` | usage — an unknown group, verb, flag, api-version or `--query` |
| `3` | auth — no usable credential, or `401`/`403` |
| `4` | server — a `5xx`, after the pipeline's retries |
| `5` | timeout — `--timeout` elapsed |

## stdout is the answer; stderr is everything else

Progress, prompts, the device code, warnings and every failure go to stderr. `--output json` writes
one valid document to stdout or writes nothing at all, **including when the command fails** — a CLI
that prints a human error into a JSON stream breaks every script that consumes it.

## Assembly graph

`cyc` references `CyberCloud.Sdk` and `CyberCloud.Analyzers`. It references no provider
implementation assembly and no host.

## Publishing

```
dotnet publish cli/cyc/cyc.csproj -r <rid> -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=full
```

`IsAotCompatible` is set in the project, so the trim, single-file and AOT analysers run on every
ordinary build: a reflective call added here fails `./build.sh Compile` rather than failing `cyc` at
run time on a user's machine.
