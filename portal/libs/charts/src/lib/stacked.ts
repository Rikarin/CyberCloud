import type { EChartsOption } from 'echarts';

/** One cell of a daily breakdown: what one key cost on one day. */
export interface DailyAmount {
  /** The UTC day, `yyyy-MM-dd`. */
  readonly day: string;
  /** The key — a resource group, a type, a resource path, a meter. */
  readonly name: string;
  readonly amount: number;
}

/** A daily breakdown shaped for a stacked bar chart: one series per key over a fixed run of days. */
export interface StackedDays {
  /** Every day of the range, in order — including days with no cost, which draw as empty. */
  readonly days: readonly string[];
  /** One series per key, largest total first, with anything past the palette folded into {@link OTHER}. */
  readonly series: readonly { readonly name: string; readonly data: readonly number[] }[];
}

/**
 * The name of the series every key past the palette is folded into.
 *
 * ⚠ **Eight series, never nine.** `@xui/echarts`' token theme has eight categorical colours in a
 * fixed order and says a ninth series "belongs in an Other bucket or a second chart, not in a
 * generated hue" — ECharts would otherwise cycle back to the first colour and two keys would look
 * like one. So seven keys keep their own series and the eighth slot is this one.
 */
export const OTHER = 'Other';

/** How many keys keep a series of their own; the rest share {@link OTHER}. */
export const MAX_KEYS = 7;

/**
 * Pivots daily rows into stacked series over `days`.
 *
 * Keys are ranked by their total over the range, so the series a reader sees first is the one
 * that cost most, and it keeps its colour when the range changes. A day the rows don't name draws
 * as zero; a row for a day outside `days` is ignored rather than stretching the axis.
 */
export function stackByDay(rows: readonly DailyAmount[], days: readonly string[]): StackedDays {
  const totals = new Map<string, number>();
  for (const row of rows) totals.set(row.name, (totals.get(row.name) ?? 0) + row.amount);

  const ranked = [...totals.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([name]) => name);
  const kept = ranked.length > MAX_KEYS + 1 ? ranked.slice(0, MAX_KEYS) : ranked;
  const folded = ranked.length > kept.length;

  const index = new Map(days.map((day, i) => [day, i]));
  const series = new Map<string, number[]>(kept.map(name => [name, days.map(() => 0)]));
  if (folded)
    series.set(
      OTHER,
      days.map(() => 0)
    );

  for (const row of rows) {
    const at = index.get(row.day);
    if (at === undefined) continue;

    const target = series.get(row.name) ?? series.get(OTHER);
    if (target !== undefined) target[at] = round2(target[at] + row.amount);
  }

  return { days, series: [...series.entries()].map(([name, data]) => ({ name, data })) };
}

/** Every UTC day from `from` (inclusive) to `to` (exclusive), as `yyyy-MM-dd`. */
export function daysBetween(from: Date, to: Date): string[] {
  const days: string[] = [];
  const cursor = new Date(Date.UTC(from.getUTCFullYear(), from.getUTCMonth(), from.getUTCDate()));

  while (cursor < to) {
    days.push(cursor.toISOString().slice(0, 10));
    cursor.setUTCDate(cursor.getUTCDate() + 1);
  }

  return days;
}

/**
 * The option for a stacked daily bar chart.
 *
 * Colours are not set here: the chart's `tokens` theme supplies them in series order, which is
 * what keeps this chart in step with the app's light and dark themes.
 */
export function stackedBarOption(stacked: StackedDays, currency: string): EChartsOption {
  return {
    tooltip: { trigger: 'axis', valueFormatter: value => `${Number(value).toFixed(2)} ${currency}` },
    legend: { bottom: 0 },
    grid: { left: 64, right: 16, top: 24, bottom: 56 },
    xAxis: { type: 'category', data: [...stacked.days] },
    yAxis: { type: 'value', name: currency },
    series: stacked.series.map(s => ({ type: 'bar', stack: 'cost', name: s.name, data: [...s.data] }))
  };
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}
