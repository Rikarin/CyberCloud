# `test/` — cross-cutting suites

A provider's own unit and integration tests are **siblings** of the provider (ADR-018):
`CyberCloud.Providers.Data.Tests` sits next to `CyberCloud.Providers.Data`. Only suites that span
providers, or that need a deployed platform, live here.

| Project | What it does |
|---|---|
| `CyberCloud.E2E` | drives the public REST API against a real deployment |
| `CyberCloud.Conformance` | the shared provider suite, parameterised — referenced by every provider |
| `CyberCloud.Cluster.Conformance` | the same suite's cluster-backed half: k3s, PostgreSQL, Redis — referenced by every provider's `.Cluster.Conformance` |
| `CyberCloud.Chaos` | silo kills, Redis flush, cluster-connection loss, network partition |
| `CyberCloud.Load` | the docs/plan/00 § quality-bar numbers, as a gate |
| `CyberCloud.Isolation` | the cross-tenant suite: every provider, every verb, wrong tenant → 404 |

## What exists, and what is deferred

`CyberCloud.Conformance`, `CyberCloud.Cluster.Conformance` and `CyberCloud.Isolation` are built.
`CyberCloud.E2E`, `CyberCloud.Chaos` and `CyberCloud.Load` are not.

⚠ **The conformance suite is split by what it needs, and both halves run.**
`ProviderConformanceTests` runs against `Orleans.TestingHost` with in-memory storage and an in-memory
API server, and needs no Docker daemon — it is the per-PR gate for every provider change.
`CyberCloud.Cluster.Conformance` runs the five criteria that a dictionary cannot fail, against a
`k3s` container, a PostgreSQL container and a Redis reminder table: the lifecycle against a real API
server, drift corrected after a real `kubectl delete`, a field conflict with another manager becoming
a named `DriftEvent`, desired state surviving a real serialization round trip, and killing the silo
mid-create still converging.

⚠ `CyberCloud.Conformance` **must never** reference `Testcontainers` — that is the whole reason the
two halves are two projects. It keeps one visible skip
(`ClusterBackedConformanceTests`) naming where the other half lives, because it cannot reference that
assembly and would otherwise have no way to mention it on a Docker-free machine. Every test in the
cluster-backed half skips loudly, by name, when no daemon answers.

**[24 § Phase 1](../docs/plan/24-roadmap.md)'s exit criterion 3 — kill a silo mid-provision, the
resource converges — is met on a machine with Docker**, against real PostgreSQL grain storage and a
real Redis reminder table, for the reference provider, for `CyberCloud.Sample/widgets` and for
`CyberCloud.Cache/redis`. It is **not** met by a run that skipped for want of a daemon, and the skip
says so.

⚠ **A provider joins this half with two class declarations and one `AssemblyInfo` line — plus, if its
objects are custom resources, the CRDs that make them addressable. That last clause was added on
2026-08-12 and it was not optional.** Until then the suite had only ever hosted providers rendering a
core-group `ConfigMap`, and a bare `k3s` serves no REST path for a custom resource: the apply comes
back `404`, `KubeApiClient` maps neither that nor any other non-`409` 4xx, and the raw
`k8s.Autorest.HttpOperationException` escapes the grain call — so **all five assertions fail with
`CodecNotFoundException` and no status code anywhere in the message**. `CyberCloud.Cache/redis` was
the first provider whose object needed a CRD and it went 5-of-6 red, and the third repeated it.
`ClusterConformanceHarness` **derives** the CRDs from `Objects` — which already carries group,
version, kind and plural — serves each and waits for `Established` before the silos start. Derived
rather than declared on purpose: a declared list can be under-declared, and the failure when it is
is the message above. `Objects` cannot be, because the suite fails immediately without it. **Every service in
[docs/plan/12 § The catalogue](../docs/plan/12-managed-data-services.md) renders a custom resource**,
so without this the cluster-backed half was available to the reference provider and to nobody else.

⚠ **The unmapped-4xx half of that is a `CyberCloud.Kubernetes` defect and is still open.**
`KubeApiClient.ApplyAsync` catches a `409` and whatever `IsTransport` matches; a `403`, a `422` from
an admission webhook, a `400` for a malformed object and a `404` for a missing kind are caught by
nothing. `IsTransport`'s own remarks explain why those must not be reported as "cannot reach your
cluster" — which is right — but no branch implements the other half, so they are not reported as
anything.

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
