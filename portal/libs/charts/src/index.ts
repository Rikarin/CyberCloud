/**
 * `libs/charts` — metric and log views over `@xui/echarts`. **A stub at M1**, holding the contract
 * and no components.
 *
 * docs/plan/03 § `portal/` gives this library its job. Everything that would live in it is on
 * docs/plan/20 § The pages that are not generated and is explicitly outside the M1 shell:
 *
 * | Page | What it needs before it can be built | EM |
 * |---|---|---|
 * | Cost analysis | The billing aggregates from docs/plan/22 — breakdowns by tag, resource group, service and day, plus forecast and budget models. Charts are the easy half | 0.6 |
 * | Metrics explorer | A query builder over the hot-tier pre-aggregates (docs/plan/16), plus pinning to dashboards, which needs dashboards to exist | 0.6 |
 * | Log search | `@xui/code-block` and a results grid over ClickHouse. ⚠ docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan" — the preview is a server-side estimate this library cannot fake | 0.6 |
 * | Network topology | `@xui/node-graph` over the VPC/subnet/peering graph from docs/plan/14. Not a chart library problem; the data shape is the work | 0.5 |
 *
 * ⚠ One dependency note that is a decision, not a detail. `@xui/echarts@2.2.0` peers
 * `"@angular/cdk": "22.0.6"` — an exact version, where most `@xui/*` packages peer the `22` major
 * range. That is why `portal/package.json` pins `@angular/cdk` to `22.0.6` rather than the 22.x
 * head, even though nothing in the M1 shell imports `@xui/echarts` yet: discovering the pin when
 * the first chart lands would mean moving the CDK underneath a working shell.
 *
 * `echarts` itself is deliberately *not* a dependency yet. It is ~350 KB and would have to be
 * lazily loaded from a route chunk anyway to stay inside the 120 KB route budget in docs/plan/20
 * § Performance budget; adding it now would only put it in the lockfile with nothing importing it.
 */

/** A metric series as the hot tier returns it — docs/plan/16. */
export interface MetricSeries {
  readonly name: string;
  /** Epoch milliseconds. Ascending, gap-free at the series' own resolution. */
  readonly timestamps: readonly number[];
  /** Parallel to `timestamps`. `null` is a real gap, not a zero — plotting a gap as zero invents a value. */
  readonly values: readonly (number | null)[];
  readonly unit: string;
}

export {};
