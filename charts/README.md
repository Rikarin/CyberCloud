# `charts/` — Helm charts we own or have forked

```
charts/
├── platform/         # Cyber Cloud itself: silo, gateway, identity, ingest, worker, portal
├── bundle/           # what we install into a managed cluster: operators, CNI, CSI, monitoring
├── managed/          # one chart per managed service — the catalogue
└── tenant-cluster/   # Cluster API + Kamaji + KubeVirt templates for an in-house cluster
```

## A managed-service chart

```
charts/managed/postgres/
├── Chart.yaml
├── values.yaml           # annotated (ADR-010) — the schema source
├── values.schema.json    # GENERATED — checked in, diffed in CI
├── templates/
├── SOURCE                # upstream repo + commit, if forked
└── conformance.yaml      # what the conformance suite asserts for this type
```

**The annotated `values.yaml` is the single description of a managed service's configuration
surface.** `Build.Charts` generates `values.schema.json` from it; `Build.Generate` turns that into
the resource type's OpenAPI body, the CLI flags, the SDK model and the portal form. A chart whose
generated schema differs from the checked-in one fails CI.

## Forking discipline

Upstream charts are a starting point, not a dependency. Where a chart is close, fork it here with
the upstream repo and commit recorded in a `SOURCE` file. A drifting vendored chart with no
provenance is how a platform ends up unable to upgrade Postgres.

## Licences are a build gate, not a footnote

`Build.Licence` scans the chart set and the container images in the platform bundle and fails on any
SSPL/BUSL/AGPL image outside an allow-list with a written reason — ADR-011. Valkey not Redis,
OpenBao not Vault, FerretDB not MongoDB, OpenSearch not Elasticsearch.

See [docs/plan/03 § charts](../docs/plan/03-repository-layout.md) and
[docs/plan/12](../docs/plan/12-managed-data-services.md).
