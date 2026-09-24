import { BarChart, LineChart } from 'echarts/charts';
import { GridComponent, LegendPlainComponent, TooltipComponent } from 'echarts/components';
import { connect, init, use } from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';

/**
 * The ECharts build the portal ships: two chart types, three components, one renderer.
 *
 * ⚠ **This file is only ever reached through `import()`**, from `CHART_ENGINE`'s default factory,
 * so it and everything it pulls in land in one lazy chunk of their own that no route chunk and no
 * initial script contains. docs/plan/20 § Performance budget puts a route chunk under 120 KB
 * gzipped; `echarts` whole is several times that, and a static import here would put it inside
 * whichever route first drew a chart. `scripts/bundle-budget.mjs` measures this chunk like any
 * other lazy one, so an addition that crosses the line fails the build and names the file.
 *
 * ⚠ **What is left out is chosen, not forgotten.** The plain legend only — the cost page's stacked
 * breakdown names its keys under the chart, while the explorer's series table is its own legend and
 * never turns this one on; no scrolling legend; no data zoom — the time range picker is the zoom,
 * and it re-queries rather than stretching points the store already thinned; no SVG renderer —
 * canvas is the one the theme is tested against. Each is a line in `use` below and a few KB in the
 * chunk when a page needs it.
 */
use([LineChart, BarChart, GridComponent, LegendPlainComponent, TooltipComponent, CanvasRenderer]);

/** The slice of ECharts `@xui/echarts` calls — `XuiEChartsCore`. */
export const engine = { init, connect };
