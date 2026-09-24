import type { EChartsOption } from 'echarts';

/** A metric series as the explorer plots it — one line. */
export interface MetricSeries {
  /** What the legend and the tooltip call it — the label set, spelled. */
  readonly name: string;
  /** Epoch milliseconds, ascending. */
  readonly timestamps: readonly number[];
  /** Parallel to `timestamps`. `null` is a real gap, not a zero — plotting a gap as zero invents a value. */
  readonly values: readonly (number | null)[];
  readonly unit: string;
}

/** One histogram bucket: when it starts and how many records of each severity fell in it. */
export interface HistogramBucket {
  /** Epoch milliseconds. */
  readonly start: number;
  readonly bySeverity: Readonly<Record<string, number>>;
}

/**
 * The severity classes a log histogram stacks, oldest-to-worst, and the token each is drawn in.
 *
 * ⚠ **Colours are token names, resolved on the element**, never literals — docs/plan/20 § Accessibility,
 * i18n, theming: "Theming is tokens". The categorical `--chart-*` ramp is the theme's; the three that
 * mean something — warning, error and fatal — take the intent tokens instead, so an error bar is the
 * same red as an error callout in both themes.
 */
export const SEVERITY_ORDER = ['unspecified', 'trace', 'debug', 'info', 'warn', 'error', 'fatal'] as const;

const SEVERITY_TOKEN: Readonly<Record<(typeof SEVERITY_ORDER)[number], string>> = {
  unspecified: '--chart-8',
  trace: '--chart-7',
  debug: '--chart-6',
  info: '--chart-1',
  warn: '--warning',
  error: '--error',
  fatal: '--error-emphasis'
};

/** A CSS custom property's value on an element, or the fallback when it is not set (a test, a server render). */
export type TokenReader = (token: string) => string;

/**
 * A number as an axis or a tooltip shows it: short, with the unit, and never in exponent notation.
 *
 * `1234567` is `1.23M`; `0.000123` is `0.000123`; a byte unit is binary (`KiB`, `MiB`), because the
 * store's own byte counters are.
 */
export function formatValue(value: number | null, unit = ''): string {
  if (value === null || !Number.isFinite(value)) return '—';

  const bytes = unit === 'bytes';
  const base = bytes ? 1024 : 1000;
  const steps = bytes ? ['', 'Ki', 'Mi', 'Gi', 'Ti', 'Pi'] : ['', 'k', 'M', 'G', 'T', 'P'];

  let scaled = value;
  let step = 0;

  while (Math.abs(scaled) >= base && step < steps.length - 1) {
    scaled /= base;
    step++;
  }

  const digits = Math.abs(scaled) >= 100 || Number.isInteger(scaled) ? 0 : Math.abs(scaled) >= 1 ? 2 : 6;
  const text = Number(scaled.toFixed(digits)).toString();

  if (bytes) return `${text} ${steps[step]}B`;

  return `${text}${steps[step]}${unit.length > 0 ? ` ${unit}` : ''}`;
}

/**
 * The option a time-series line chart is drawn from: a time axis, a value axis, one line per series.
 *
 * ⚠ **Gaps stay gaps.** `connectNulls` is off and a `null` value is passed through, so a scrape the
 * store never received is a break in the line — the property `MetricSeries.values` documents.
 */
export function timeSeriesOption(series: readonly MetricSeries[]): EChartsOption {
  const unit = series[0]?.unit ?? '';

  return {
    animation: false,
    grid: { left: 56, right: 16, top: 16, bottom: 32 },
    tooltip: {
      trigger: 'axis',
      valueFormatter: value => formatValue(typeof value === 'number' ? value : null, unit)
    },
    xAxis: { type: 'time' },
    yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatValue(value, unit) } },
    series: series.map(s => ({
      name: s.name,
      type: 'line' as const,
      showSymbol: s.timestamps.length <= 1,
      connectNulls: false,
      data: s.timestamps.map((t, i) => [t, s.values[i] ?? null])
    }))
  };
}

/**
 * The option a log histogram is drawn from: one stacked bar per bucket, one stack segment per
 * severity that occurs in the window, in `SEVERITY_ORDER`.
 */
export function histogramOption(buckets: readonly HistogramBucket[], token: TokenReader): EChartsOption {
  const present = SEVERITY_ORDER.filter(severity => buckets.some(b => (b.bySeverity[severity] ?? 0) > 0));

  return {
    animation: false,
    grid: { left: 48, right: 16, top: 12, bottom: 28 },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time' },
    yAxis: { type: 'value', minInterval: 1 },
    series: present.map(severity => ({
      name: severity,
      type: 'bar' as const,
      stack: 'severity',
      barMaxWidth: 24,
      itemStyle: { color: token(SEVERITY_TOKEN[severity]) },
      data: buckets.map(b => [b.start, b.bySeverity[severity] ?? 0])
    }))
  };
}

/** Reads tokens off an element, for `histogramOption` in a browser. */
export function tokensOf(element: Element | null): TokenReader {
  return token => {
    if (element === null || typeof getComputedStyle !== 'function') return `var(${token})`;
    const value = getComputedStyle(element).getPropertyValue(token).trim();
    return value.length > 0 ? value : `var(${token})`;
  };
}
