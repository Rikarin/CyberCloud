# `cli/` — `cc`

The Cyber Cloud command-line interface. .NET, `System.CommandLine`, but kept out of
[`src/`](../src) because it **ships separately** — it is packaged, signed and released on its own
cadence, and it is the one .NET thing here that sets `IsPackable=true`.

## It is generated, not written

The verb tree, the flags, the help text and the shell completions come from the provider registry
via `Build.Generate` (ADR-012). Hand-writing a verb is how the CLI drifts from the API. Unlike the
portal's forms, **the CLI has no per-type override escape hatch** — if a generated verb is wrong,
the registry entry is wrong.

## It has no privileged path

`cc` calls the same public REST API as the portal and the SDK, with the same token.

## Assembly graph

The CLI references `*.Contracts` and the generated SDK. It references no provider implementation
assembly and no host.

See [docs/plan/21](../docs/plan/21-cli-and-sdks.md).
