/**
 * `libs/charts` — metric, log and cost views over `@xui/echarts`. docs/plan/03 § `portal/` gives this
 * library its job; the pages are docs/plan/20 § The pages that are not generated.
 *
 * | Page | State |
 * |---|---|
 * | Cost analysis | ✅ **Landed (#41)** — `pages/cost` draws its daily breakdown with {@link stackByDay} and {@link stackedBarOption}, through {@link provideCharts} |
 * | Metrics explorer | A query builder over the hot-tier pre-aggregates (docs/plan/16), plus pinning to dashboards, which needs dashboards to exist |
 * | Log search | `@xui/code-block` and a results grid over ClickHouse. ⚠ docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan" — the preview is a server-side estimate this library cannot fake |
 * | Network topology | `@xui/node-graph` over the VPC/subnet/peering graph from docs/plan/14. Not a chart library problem; the data shape is the work |
 *
 * ⚠ **`echarts` is a dependency now, and it is never in a route chunk.** It arrived with the first
 * chart (#41) as `echarts@6.1.0` beside `@xui/echarts@3.0.0`, whose peer range `^5.5.0 || ^6.0.0` it
 * satisfies. The full package is several times the 120 KB route budget in docs/plan/20 § Performance
 * budget, so the portal ships a tree-shaken build (`lib/echarts-build.ts`: bar, grid, legend,
 * tooltip, canvas) that only `import()` reaches — {@link CHART_ENGINE}'s default — and that
 * `scripts/bundle-budget.mjs` measures as a chunk of its own. `portal/pnpm-workspace.yaml` says why
 * `tslib` is overridden to one version for it.
 *
 * ⚠ One dependency note that is a decision, not a detail. `@xui/echarts@2.2.x` peered
 * `"@angular/cdk": "22.0.6"` — an exact version, where most `@xui/*` packages peer the `22` major
 * range. At `@xui/echarts@3.0.0` that peer is `"22"` (measured 2026-09-15), so the CDK moved to
 * 22.1.4 with the rest of the framework (#87) for the reason portal/README.md § The Angular pin
 * gives, and the first chart moved nothing.
 */

export { CHART_ENGINE, provideCharts } from './lib/chart-engine';
export { MAX_KEYS, OTHER, daysBetween, stackByDay, stackedBarOption } from './lib/stacked';
export type { DailyAmount, StackedDays } from './lib/stacked';

/** A metric series as the hot tier returns it — docs/plan/16. */
export interface MetricSeries {
  readonly name: string;
  /** Epoch milliseconds. Ascending, gap-free at the series' own resolution. */
  readonly timestamps: readonly number[];
  /** Parallel to `timestamps`. `null` is a real gap, not a zero — plotting a gap as zero invents a value. */
  readonly values: readonly (number | null)[];
  readonly unit: string;
}
