/**
 * `libs/charts` — metric and log views over `@xui/echarts`.
 *
 * docs/plan/03 § `portal/` gives this library its job, and #41 is the first page to need it: the
 * metrics explorer's time series and the log search's histogram. The pages themselves live in
 * `apps/portal` beside the routes; what lives here is what a second page would reuse — the two
 * chart components, the pure option builders they draw from, and the provider a charting page puts
 * on itself.
 *
 * ⚠ **The engine is lazy twice over.** A page that draws a chart provides `provideCharts()` on its
 * own component, so the shell's import graph does not reach `@xui/echarts`; and ECharts itself is
 * reached only through `CHART_ENGINE`'s `import()`, so it is a chunk of its own that loads on the
 * first chart and never with a route. `echarts-engine.ts` says what the build contains and why the
 * rest is left out. `scripts/bundle-budget.mjs` finds that chunk by name and holds it to docs/plan/20
 * § Performance budget's chart-engine row, 180 KB, because no ECharts build fits the 120 KB every
 * other lazy chunk is held to.
 *
 * ⚠ The two pages this library was sketched for next are still not built: cost analysis waits on
 * docs/plan/22's billing aggregates, and network topology is `@xui/node-graph` rather than a chart.
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
