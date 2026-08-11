# `test/` — cross-cutting suites

A provider's own unit and integration tests are **siblings** of the provider (ADR-018):
`CyberCloud.Providers.Data.Tests` sits next to `CyberCloud.Providers.Data`. Only suites that span
providers, or that need a deployed platform, live here.

| Project | What it does |
|---|---|
| `CyberCloud.E2E` | drives the public REST API against a real deployment |
| `CyberCloud.Conformance` | the shared provider suite, parameterised — referenced by every provider |
| `CyberCloud.Chaos` | silo kills, Redis flush, cluster-connection loss, network partition |
| `CyberCloud.Load` | the docs/plan/00 § quality-bar numbers, as a gate |
| `CyberCloud.Isolation` | the cross-tenant suite: every provider, every verb, wrong tenant → 404 |

## Why `CyberCloud.Isolation` is its own project

It is the one suite that must be written by someone who is *trying to break in*, and mixing it with
a provider's happy-path tests dilutes that intent. It asserts **404, never 403** — existence is not
disclosed.

## The conformance suite is what makes the catalogue safe to grow

One xUnit theory every provider must pass: create → 202 → poll → Succeeded → read back → tag → lock
→ delete → gone; create with tenant B's ids → 404; delete while an operation is running → 409;
reconcile after a manual cluster mutation → drift corrected; kill the silo mid-create → resource
still converges. **A provider is not registered in the platform bundle until it passes.**

## Conventions

`Directory.Build.targets` detects these by name and applies the test-project profile (executable
output for xunit v3, relaxed CA1515/CA1707/CA2007). xUnit v3 + `Orleans.TestingHost` + NSubstitute +
Shouldly, with real Redis/Postgres/NATS/`k3s` from Testcontainers rather than mocks — mocking
`IGrainFactory` tests the mock.

See [docs/plan/23](../docs/plan/23-build-ci-and-testing.md).
