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

## What exists, and what is deferred

`CyberCloud.Conformance` and `CyberCloud.Isolation` are built. `CyberCloud.E2E`, `CyberCloud.Chaos`
and `CyberCloud.Load` are not.

⚠ **The conformance suite is split by what it needs, and the deferred half is present by name.**
`ProviderConformanceTests` runs against `Orleans.TestingHost` with in-memory storage and an in-memory
API server; `ClusterBackedConformanceTests` holds the tests that need a k3s container, real
PostgreSQL or a multi-silo cluster, and each one **skips loudly** with a message naming what it needs
and what it would prove. Neither project references `Testcontainers`, deliberately — a package
reference would make the whole suite refuse to run without a Docker daemon.

**Therefore [24 § Phase 1](../docs/plan/24-roadmap.md)'s exit criterion 3 — kill a silo mid-provision,
the resource converges — is written and unrun, and must not be claimed.**

`CyberCloud.Isolation` drives the **real** `ReBacResourceAuthorizer` over the real ReBAC schema, which
no other suite in the repository does. That is where its value is: a double reproduces the rule its
author believed.

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
