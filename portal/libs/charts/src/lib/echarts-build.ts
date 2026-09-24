import { BarChart } from 'echarts/charts';
import { GridSimpleComponent, LegendPlainComponent, TooltipComponent } from 'echarts/components';
import * as core from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

/**
 * The ECharts the portal ships: the core, the bar chart, a plain grid and legend, the tooltip and
 * the canvas renderer — what the cost page draws, and nothing it doesn't.
 *
 * ⚠ **Only ever reached through `import()`** — `CHART_ENGINE`'s default loader in `chart-engine.ts`.
 * A static import from anything a route reaches eagerly would put this in that route's chunk. The
 * build keeps it a chunk of its own, fetched the first time a chart renders, and
 * `scripts/bundle-budget.mjs` measures it against the chart-library ceiling rather than the route one.
 *
 * ⚠ **Measured, 2026-09-24, bundled alone with esbuild, minified and gzipped at level 9**: this set is
 * 173.8 KB; with `LineChart`, the full `GridComponent` and the scrolling legend it is 185.2 KB; the core
 * and the canvas renderer alone are 129.3 KB. The Angular production build emits this set as a
 * 172.8 KB chunk, and that is the number `bundle-budget.mjs` holds to the ceiling. No ECharts build fits
 * the 120 KB route budget, which is why the library has a ceiling of its own (docs/plan/20 § Performance
 * budget, where the 180 KB is still a proposal). Every addition here is paid for in it.
 *
 * ⚠ **A chart type or a component not registered here draws nothing and says little**: ECharts
 * warns in development and skips the series in production. A page that adds a `line` or a
 * `dataZoom` adds it here, and re-measures.
 */
core.use([BarChart, GridSimpleComponent, LegendPlainComponent, TooltipComponent, CanvasRenderer]);

export { core };
