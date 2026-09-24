/**
 * `libs/charts` — metric, log and cost views over `@xui/echarts`. docs/plan/03 § `portal/` gives this
 * library its job; the pages are docs/plan/20 § The pages that are not generated.
 *
 * | Page | State |
 * |---|---|
 * | Cost analysis | ✅ **Landed (#41)** — `pages/cost` draws its daily breakdown with {@link stackByDay} and {@link stackedBarOption} |
 * | Metrics explorer | ✅ **Landed (#41)** — {@link TimeSeriesChart} over {@link timeSeriesOption} |
 * | Log search | ✅ **Landed (#41)** — {@link LogHistogram} over {@link histogramOption}. ⚠ docs/plan/20: "Needs a query cost preview or someone will run a 400-day scan" — the preview is a server-side estimate this library cannot fake |
 * | Network topology | `@xui/node-graph` over the VPC/subnet/peering graph from docs/plan/14. Not a chart library problem; the data shape is the work |
 *
 * ⚠ **The engine is lazy twice over, and there is one of it.** A page that draws a chart provides
 * {@link provideCharts} on its own component, so the shell's import graph does not reach
 * `@xui/echarts`; and ECharts itself (`echarts@6.1.0`, beside `@xui/echarts@3.0.0`, whose peer range
 * `^5.5.0 || ^6.0.0` it satisfies) is reached only through {@link CHART_ENGINE}'s `import()` of
 * `lib/echarts-engine.ts`, so it is a chunk of its own that loads on the first chart and never with a
 * route. The cost page and the explorers were built on two branches at once, each with its own copy
 * of this seam; they were one at the merge, and the build is the union of what both pages register.
 * `scripts/bundle-budget.mjs` finds that chunk by what only ECharts contains and holds it to its own
 * ceiling; `portal/pnpm-workspace.yaml` says why `tslib` is overridden to one version for it.
 *
 * ⚠ One dependency note that is a decision, not a detail. `@xui/echarts@2.2.x` peered
 * `"@angular/cdk": "22.0.6"` — an exact version, where most `@xui/*` packages peer the `22` major
 * range. At `@xui/echarts@3.0.0` that peer is `"22"` (measured 2026-09-15), so the CDK moved to
 * 22.1.4 with the rest of the framework (#87) for the reason portal/README.md § The Angular pin
 * gives, and the first chart moved nothing.
 */
export { LogHistogram, TimeSeriesChart } from './lib/charts';
export { CHART_ENGINE, provideCharts } from './lib/engine';
export {
  SEVERITY_ORDER,
  formatValue,
  histogramOption,
  timeSeriesOption,
  tokensOf,
  type HistogramBucket,
  type MetricSeries,
  type TokenReader
} from './lib/options';
export { MAX_KEYS, OTHER, daysBetween, stackByDay, stackedBarOption } from './lib/stacked';
export type { DailyAmount, StackedDays } from './lib/stacked';
