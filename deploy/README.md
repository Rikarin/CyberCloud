# `deploy/` — how Cyber Cloud itself is installed

```
deploy/
├── bootstrap/          # what you run on the FIRST cluster, by hand, once
├── platform/           # helmfile/kustomize for the platform chart per environment
└── managed-cluster/    # the bundle applied to a cluster the platform adopts or creates
```

## `bootstrap/` answers the chicken-and-egg

The platform manages clusters, but something has to run the platform. The decision is already made:
Cyber Cloud is **installed on an existing cluster by hand, manages a second one, and moves onto that
second one once it is boring**. `deploy/bootstrap/` is that hand-installation.

It is kept honest by being **the same thing CI uses to stand up e2e**. A bootstrap procedure that
only exists in a runbook is a bootstrap procedure that does not work.

## What does not live here

Chart *sources* are in [`charts/`](../charts). This directory is composition and environment
configuration over them — values files, helmfile/kustomize overlays, and the ordering.

See [docs/plan/03 § deploy](../docs/plan/03-repository-layout.md) and
[docs/plan/09 § Bootstrap](../docs/plan/09-kubernetes-fabric.md).
