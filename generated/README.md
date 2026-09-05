# `generated/` — ADR-012's other three surfaces

Everything in this directory is **generated and checked in**. Do not edit it. `./build.sh Generate`
overwrites it, and `./build.sh Architecture` fails on any difference between what is here and what the
generator produces.

⚠ That comparison is **byte-for-byte, which proves the emitter is deterministic and proves nothing
about whether the output is valid**. For `sdk/` there is a second gate that does — `Generated SDK
compiles`, issue #73 — and the "What is not here" section below says why it is a Roslyn compilation
in the build rather than a `.csproj`.

| Directory | Surface | Consumed by |
|---|---|---|
| `cli/{api-version}.json` | The `cyc` verb tree — groups, commands, verbs, flags, aliases, exit codes | The hand-written `cyc` host ([21](../docs/plan/21-cli-and-sdks.md) § `cyc` — the CLI) |
| `sdk/{api-version}.cs` | `{Type}Data` / `{Type}Resource` / `{Type}Collection` and the `Operation<T>` signatures | The hand-written half of the .NET SDK ([21](../docs/plan/21-cli-and-sdks.md) § Generation) |
| `forms/{api-version}.json` | Portal form schemas — one field per property, with its xUI control | `libs/resource-forms`' schema-renderer ([20](../docs/plan/20-portal.md)) |

## Why these are generated *from the OpenAPI document* and not from the registry

[21](../docs/plan/21-cli-and-sdks.md) § Generation: `Build.Generate` walks *"the provider registry →
OpenAPI 3.1 → the SDK's models, clients and pollers"*. One hop, deliberately — the compatibility gate
on `openapi/` (a breaking change to a published api-version fails CI) therefore protects all four
surfaces at once. A CLI generated straight from the registry could describe a flag the published
contract does not have, and no gate would notice.

The corollary: **these three are not diffed for compatibility.** They are a function of a document that
already was.

## Why checked in rather than under `artifacts/`

[23](../docs/plan/23-build-ci-and-testing.md) § The architecture gates asks that OpenAPI, CLI, SDK and
forms *"regenerate byte-identically from the registry"*. A byte comparison needs a previous copy to
compare against, and `artifacts/` is gitignored — on a fresh clone there would be nothing there, every
run would report "new", and the gate could only ever pass. `openapi/` is tracked for exactly this
reason and these three follow it.

They are a **separate root** from `openapi/` because [10](../docs/plan/10-gateway-and-api.md) § Shape
makes the gateway serve `openapi/` as files. Nothing here is served to anyone.

## What is not here

- **The `cyc` host itself.** This directory describes the verbs; parsing them, rendering `--output
  table`, caching tokens and `cyc rest` are hand-written.
- **The SDK's hand-written half** — credential types, pipeline policies, convenience methods, tests
  ([21](../docs/plan/21-cli-and-sdks.md) § Generation). Every generated type is `partial` so that half
  extends these in place rather than wrapping them.
- **A `.csproj` for `sdk/`** — but it is compiled anyway, and that changed with issue #73. The
  clients name `Response<T>`,
  `Operation<T>`, `WaitUntil` and `AsyncPageable<T>`, which are `CyberCloud.Sdk`'s own shapes and
  arrive with the hand-written half, and every operation is a `partial` declaration whose
  implementing half that same hand-written code owes — so a project including this file fails on
  `CS8795` before it fails on anything real. **Two further things make a project the wrong tool
  here:** the emitted namespace carries no api-version, so `<Compile Include="sdk/*.cs" />` would be
  `CS0101` on every type the day a second api-version is published; and a project would compile only
  what its glob matched, which is how an older api-version stops being checked. So
  `build/Build.Architecture.cs`'s **`Generated SDK compiles`** gate hands **each file, on its own**,
  to Roslyn against the real `CyberCloud.Sdk`, accepting `CS8795` and nothing else. It is the C#
  equivalent of the `pnpm typecheck:api` that has always covered the TypeScript client.

  ⚠ **The row above it compares BYTES, and byte-identical is not valid.** That distinction is not
  theoretical: `sdk/2026-08-01.cs` was shipping `CS0101` (a duplicated enum name), `CS0246` (an
  action's enum referenced and never declared), seventeen `CS0102`s over fourteen duplicated property
  names (from flattening a nested body) and 110 `CS9035`s (`= new()` for a body whose members are
  `required`) — green under every gate in this repository, because nothing had ever handed the file
  to a compiler. Those counts are Roslyn's over the pre-fix file, not a build log's: `CS0102` is one
  per redeclaration rather than per name, and `CS9035` is one per unset required member per `new()`.
- **The portal's TypeScript client.** It exists (issue #21) and is generated from the same document
  by the same run, but it is written to `portal/libs/api/` rather than here — [03](../docs/plan/03-repository-layout.md)
  § Assembly graph rules, rule 6 gives the generator that directory, and `Build.Architecture` enforces
  it by reading the head of every file *there*. Writing it here and copying it across would make that
  gate inspect a copy, and the copy step would be the one part of the chain nothing checked. The
  `Generated surfaces` gate compares it byte-for-byte alongside the three above.
- **Python, Go and the Terraform provider.**
  [21](../docs/plan/21-cli-and-sdks.md) § Other SDKs schedules them for M1–M3.
