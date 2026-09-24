import { InjectionToken, Provider, inject } from '@angular/core';
import { XuiECharts, XuiEChartsLoader, provideXuiECharts } from '@xui/echarts';

/**
 * Where ECharts comes from — by default the tree-shaken build in `echarts-engine.ts`, loaded on
 * the first chart a page draws.
 *
 * A token rather than a constant so a spec can hand the chart a recording engine: jsdom has no
 * canvas, and the page tests assert what option a chart was given, not what a canvas painted.
 */
export const CHART_ENGINE = new InjectionToken<XuiEChartsLoader>('cc.charts.engine', {
  providedIn: 'root',
  factory: () => () => import('./echarts-engine').then(m => m.engine)
});

/**
 * The providers a page that draws charts puts on its own `@Component` — never on the app.
 *
 * ⚠ **Component-level, and `XuiECharts` is re-provided with the config, on purpose.** `@xui/echarts`
 * declares its loader service `providedIn: 'root'` and reads the config through `inject` when the
 * service is built — so a config provided anywhere below the root would be invisible to the root
 * instance, and a config provided at the root would put `@xui/echarts` in the initial bundle for a
 * shell that draws no chart. Providing both here gives each charting page its own loader over the
 * same `CHART_ENGINE`, and the import graph of a page that draws nothing does not change.
 *
 * ⚠ The config token is not exported by `@xui/echarts`, so it is read off the provider
 * `provideXuiECharts` returns, and the value is rebuilt in a factory so `CHART_ENGINE` is resolved
 * where the page is — which is what lets a test's root-level `CHART_ENGINE` reach a page whose own
 * providers would otherwise shadow it.
 */
export function provideCharts(): Provider[] {
  const shape = provideXuiECharts({ echarts: () => Promise.reject(new Error('replaced below')) });

  return [
    XuiECharts,
    {
      provide: shape.provide,
      useFactory: () => ({ ...(shape.useValue as object), echarts: inject(CHART_ENGINE) })
    }
  ];
}
