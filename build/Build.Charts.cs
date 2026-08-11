// Charts — docs/plan/23 § Build, row `Charts`:
// "helm lint, generate values.schema.json from annotated values, fail on drift, package".

partial class Build
{
    void BuildCharts()
        => NotImplementedYet(
            nameof(Charts),
            "run `helm lint` over charts/platform, charts/bundle, charts/managed/* and "
            + "charts/tenant-cluster, generate each values.schema.json from the annotated values.yaml, "
            + "fail if the generated schema differs from the checked-in one, then package",
            "docs/plan/23 § Build (row `Charts`) and docs/plan/03 § charts/. Depends on the first "
            + "chart landing under charts/managed/ — the tree is currently a skeleton.");
}
