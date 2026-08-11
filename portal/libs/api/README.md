# `libs/api` — @generated, DO NOT EDIT

**This directory is empty on purpose and has no hand-written files in it, ever.**

`Build.Generate` owns it — docs/plan/03 § Assembly graph rules, rule 6: "`portal/libs/api` has no
hand-written files; the generator owns the directory." `Build.Architecture` enforces that as a
build gate: every file here must carry a generator banner (`DO NOT EDIT`, `@generated` or
`auto-generated`) in its first 2 KB, which is why this README opens with one.

## What the generator emits here

The TypeScript client for the public REST API, from the OpenAPI document that
`Build.Generate` produces from the provider registry — docs/plan/03 § `portal/`: "GENERATED
TypeScript client from OpenAPI — never hand-edited". The same document also drives the CLI flags
and the SDK models, which is what keeps the four surfaces from drifting.

When it runs, it needs to write:

- `package.json` — name `@cybercloud/api`, private, `type: module`, `main`/`types` pointing at
  `src/index.ts`. ⚠ It must carry a banner key (for example `"//": "@generated — DO NOT EDIT"`)
  because JSON cannot hold a comment and the architecture gate reads the first 2 KB of every file.
- `src/index.ts` and the operation/model modules beneath it.

Once it does, add `"@cybercloud/api": ["libs/api/src/index.ts"]` to `portal/tsconfig.base.json`
`paths`. The mapping is deliberately absent until then, so that an import of a client that does not
exist fails at compile time rather than resolving to an empty module.

## What the portal may assume about it

Nothing privileged. `portal/README.md` § Rules: "The portal has no privileged path. It calls the
same public REST API as the CLI, with the same token. There is no `/internal` the portal uses and
the SDK does not."
