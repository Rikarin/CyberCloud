import { InjectionToken, Provider, inject } from '@angular/core';
import { XuiECharts, XuiEChartsLoader, provideXuiECharts } from '@xui/echarts';

/**
 * Where a page's charts get ECharts from — the tree-shaken build in `echarts-build.ts`, fetched on
 * the first chart.
 *
 * A token rather than a constant so a spec can hand the chart a recording engine: jsdom has no
 * canvas, and what a page spec asserts is the option the page built, not ECharts' pixels.
 */
export const CHART_ENGINE = new InjectionToken<XuiEChartsLoader>('CHART_ENGINE', {
  providedIn: 'root',
  factory: () => () => import('./echarts-build').then(m => m.core)
});

/**
 * What a component with a chart lists in its own `providers`.
 *
 * ⚠ **Component-level, and `XuiECharts` with it, on purpose.** `@xui/echarts` documents its config
 * as an application provider, and `XuiECharts` is `providedIn: 'root'` — so provided at the root it
 * would pull `@xui/echarts` into the initial bundle for every page, and provided on a route alone it
 * would not be seen, because a root service reads the root injector. Providing both here keeps the
 * whole chart stack in the chunk of the page that draws one, and the loader in `CHART_ENGINE`.
 *
 * `provideXuiECharts` is called for its token and its defaults; the loader is swapped in from
 * `CHART_ENGINE` when the page's injector is built.
 */
export function provideCharts(): Provider[] {
  const defaults = provideXuiECharts({ echarts: () => Promise.reject(new Error('CHART_ENGINE was not read.')) });

  return [
    {
      provide: defaults.provide,
      useFactory: () => ({ ...(defaults.useValue as object), echarts: inject(CHART_ENGINE) })
    },
    XuiECharts
  ];
}
