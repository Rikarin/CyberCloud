# `libs/api` — @generated, DO NOT EDIT

**Nothing in this directory is hand-written, ever — this README included, in the sense that nothing
here describes anything a person may change by editing a file next to it.**

`Build.Generate` owns it — docs/plan/03 § Assembly graph rules, rule 6: "`portal/libs/api` has no
hand-written files; the generator owns the directory." `Build.Architecture` enforces that as a
build gate: every file here must carry a generator banner (`DO NOT EDIT`, `@generated` or
`auto-generated`) in its first 2 KB, which is why this README opens with one.

## What the generator emits here

The TypeScript client for the public REST API, from the OpenAPI document that `Build.Generate`
produces from the provider registry — docs/plan/03 § `portal/`: "GENERATED TypeScript client from
OpenAPI — never hand-edited". The same document also drives the CLI flags, the SDK models and the
portal form schemas, which is what keeps the surfaces from drifting (issue #21).

| File | What it holds |
|---|---|
| `package.json` | Name `@cybercloud/api`, private, `type: module`, `main`/`types` at `src/index.ts`. ⚠ Its banner is a `"//"` key because JSON cannot hold a comment. |
| `tsconfig.json` | What `pnpm typecheck:api` compiles. ⚠ A JSONC comment carries its banner. |
| `src/models.ts` | One interface per resource type and per scope, plus the error shape and every closed set as a string-literal union. |
| `src/transport.ts` | `ApiTransport`, `ApiRequest`, `ApiResponse<T>`, `Page<T>`, `PageRequest`. |
| `src/client.ts` | `CyberCloudApi` — one method per operation, building a path and handing it to the transport. |
| `src/index.ts` | Re-exports the three. |

`portal/tsconfig.base.json` maps `"@cybercloud/api"` at `libs/api/src/index.ts`. That mapping was
deliberately absent while this directory was empty, so that an import of a client that did not
exist failed at compile time rather than resolving to an empty module.

## ⚠ The type-check is a phase of `pnpm verify`, and it has to be

`ng build` compiles what the app reaches, and `portal/eslint.config.mjs` ignores `libs/api/**` on
purpose — so a generated file nothing imports yet is a file nothing checks. That is not
hypothetical: the .NET SDK emitter shipped two defects of exactly this shape into a checked-in
artifact (a duplicate enum, and a property typed with a name nothing declared), green under every
gate in the repository, because nothing there compiled `generated/sdk/*.cs`. `pnpm typecheck:api`
runs `tsc` over this package on every `pnpm verify`, so this surface has a compiler and that one
now has the checks that compiler found.

⚠ **And that one now has a compiler of its own** — issue #73 added `Generated SDK compiles` to
`./build.sh Architecture`, which hands every `generated/sdk/{api-version}.cs` to Roslyn. It found two
further defect families the same day (fourteen duplicate property names and 222 unset required
members), which is the argument for this section rather than against it: the surface with a compiler
is the surface whose defects are known.

## What the portal may assume about it

Nothing privileged. `portal/README.md` § Rules: "The portal has no privileged path. It calls the
same public REST API as the CLI, with the same token. There is no `/internal` the portal uses and
the SDK does not."

⚠ **`ApiTransport` is the seam and the portal fills it.** Nothing generated here opens a socket: the
bearer token, the interceptors, the retry policy and the error mapping are the app's, over Angular's
`HttpClient`. A generated `fetch` would put authentication in a file that is overwritten on every
build and would give the portal a second HTTP client beside the one it already has.
