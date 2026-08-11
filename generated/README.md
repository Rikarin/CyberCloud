# `generated/` — ADR-012's other three surfaces

Everything in this directory is **generated and checked in**. Do not edit it. `./build.sh Generate`
overwrites it, and `./build.sh Architecture` fails on any difference between what is here and what the
generator produces.

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
- **A `.csproj` for `sdk/`.** The clients name `Azure.Core`'s `Operation<T>`, `Response<T>`,
  `WaitUntil` and `AsyncPageable<T>`, which arrive with that hand-written half; compiling this file
  before it exists would fail for reasons that have nothing to do with the generator. The models
  depend on nothing but the BCL. ⚠ The drift gate does not care either way — it compares bytes — so the
  contract this surface owes is enforced from the day it is generated rather than from the day it
  compiles.
- **TypeScript, Python, Go and the Terraform provider.**
  [21](../docs/plan/21-cli-and-sdks.md) § Other SDKs schedules them for M1–M3.
