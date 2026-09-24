import { CostRow } from '../../app/api/cost-management';

/** The periods the picker offers. `custom` reads `from` and `to` from the URL. */
export const periods = ['month', 'lastMonth', '30d', '90d', 'custom'] as const;
export type Period = (typeof periods)[number];

export function isPeriod(value: string | undefined): value is Period {
  return value !== undefined && (periods as readonly string[]).includes(value);
}

/** A half-open UTC range: `from` inclusive, `to` exclusive — the cost query's own terms. */
export interface Range {
  readonly from: Date;
  readonly to: Date;
}

const DAY = 86_400_000;
const HOUR = 3_600_000;

/** The first instant of `at`'s UTC day. */
export function startOfDay(at: Date): Date {
  return new Date(Date.UTC(at.getUTCFullYear(), at.getUTCMonth(), at.getUTCDate()));
}

/** The first instant of `at`'s UTC month, moved by `months`. */
export function startOfMonth(at: Date, months = 0): Date {
  return new Date(Date.UTC(at.getUTCFullYear(), at.getUTCMonth() + months, 1));
}

/** `yyyy-MM-dd` → the first instant of that UTC day, or `null` for anything else. */
export function parseDay(value: string | undefined): Date | null {
  if (value === undefined || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const at = new Date(`${value}T00:00:00Z`);
  return Number.isNaN(at.getTime()) || at.toISOString().slice(0, 10) !== value ? null : at;
}

/**
 * The range a period names, as of `now`.
 *
 * The rolling periods end at the end of today, so today's partial day is a bar of its own. `custom`
 * takes two days, the second inclusive — what a person means by "from the 1st to the 15th" — and
 * answers `null` for a missing, malformed or backwards pair, which the page says rather than asking.
 * ⚠ The platform refuses more than 366 days; this does not pre-empt it, the refusal names the limit.
 */
export function rangeOf(period: Period, now: Date, from?: string, to?: string): Range | null {
  const today = startOfDay(now);

  switch (period) {
    case 'month':
      return { from: startOfMonth(now), to: startOfMonth(now, 1) };
    case 'lastMonth':
      return { from: startOfMonth(now, -1), to: startOfMonth(now) };
    case '30d':
      return { from: new Date(today.getTime() - 29 * DAY), to: new Date(today.getTime() + DAY) };
    case '90d':
      return { from: new Date(today.getTime() - 89 * DAY), to: new Date(today.getTime() + DAY) };
    case 'custom': {
      const start = parseDay(from);
      const end = parseDay(to);
      if (start === null || end === null || end < start) return null;
      return { from: start, to: new Date(end.getTime() + DAY) };
    }
  }
}

/** How far back the forecast's rate is read — the budget grain's `ForecastWindow`. */
export const FORECAST_DAYS = 7;

/**
 * The range the forecast needs: this month so far and the seven days before today, whichever starts
 * earlier, up to now. One `groupBy: day` query over it answers both halves.
 */
export function forecastRange(now: Date): Range {
  const trailing = new Date(startOfDay(now).getTime() - FORECAST_DAYS * DAY);
  const month = startOfMonth(now);
  return { from: trailing < month ? trailing : month, to: now };
}

/** A month-end forecast and the two figures it came from. */
export interface Forecast {
  /** This month so far. */
  readonly actual: number;
  /** What the month will cost at the trailing rate. ⚠ An estimate, and the page labels it one. */
  readonly forecast: number;
  /** The trailing rate, per day, for the sentence that explains the figure. */
  readonly perDay: number;
}

/**
 * The month-end forecast, linear on the trailing seven days — the budget grain's method
 * (`BudgetGrain`'s figures, docs/plan/22 § Cost visibility: "Linear on the trailing 7 days …
 * deliberately simple and labelled an estimate").
 *
 * The month so far, plus the trailing window's spend per hour times the hours left in the month.
 * ⚠ **The window is whole days**, because the rows are: it starts at the UTC midnight seven days
 * before today and runs to `now`, so it spans seven days and today's elapsed hours, and the rate
 * divides by exactly that span. The grain reads the same seven days by the hour; the two agree
 * whenever spend is steady across a day, and differ by at most a part-day's weighting when it isn't.
 *
 * @param days rows of a `groupBy: day` answer over {@link forecastRange}.
 */
export function linearForecast(days: readonly CostRow[], now: Date): Forecast {
  const month = startOfMonth(now);
  const windowStart = new Date(startOfDay(now).getTime() - FORECAST_DAYS * DAY);

  let actual = 0;
  let trailing = 0;

  for (const row of days) {
    const day = parseDay(row.name);
    if (day === null) continue;
    if (day >= month) actual += row.amount;
    if (day >= windowStart) trailing += row.amount;
  }

  const windowHours = Math.max(1, (now.getTime() - windowStart.getTime()) / HOUR);
  const hoursLeft = Math.max(0, (startOfMonth(now, 1).getTime() - now.getTime()) / HOUR);
  const perHour = trailing / windowHours;

  return { actual: round2(actual), forecast: round2(actual + perHour * hoursLeft), perDay: round2(perHour * 24) };
}

/** A breakdown row: one key's total over the range. */
export interface BreakdownRow {
  readonly name: string;
  readonly amount: number;
  /** Of the breakdown's total, 0–100. */
  readonly share: number;
}

/** A daily answer's rows summed per key, most expensive first — the table beside the chart. */
export function breakdown(rows: readonly CostRow[]): BreakdownRow[] {
  const totals = new Map<string, number>();
  for (const row of rows) totals.set(row.name, (totals.get(row.name) ?? 0) + row.amount);

  const sum = [...totals.values()].reduce((a, b) => a + b, 0);

  return [...totals.entries()]
    .map(([name, amount]) => ({
      name,
      amount: round2(amount),
      share: sum === 0 ? 0 : Math.round((amount / sum) * 1000) / 10
    }))
    .sort((a, b) => b.amount - a.amount || a.name.localeCompare(b.name));
}

/**
 * A row's name as a person reads it: a resource path is long and mostly scope the page already
 * shows, so it is cut to `{type}/{name}`; everything else is itself.
 */
export function displayName(name: string): string {
  const at = name.indexOf('/providers/');
  if (at < 0) return name;

  const [, type, ...rest] = name.slice(at + '/providers/'.length).split('/');
  return rest.length === 0 ? name : `${type}/${rest.join('/')}`;
}

/**
 * An amount in its currency, with the currency's own minor unit — two places for EUR, none for JPY —
 * which is `MoneyRounding`'s table, read by `Intl` rather than copied.
 */
export function money(amount: number, currency: string): string {
  if (currency.length !== 3) return amount.toFixed(2);

  try {
    return new Intl.NumberFormat('en', { style: 'currency', currency }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency}`;
  }
}

function round2(value: number): number {
  return Math.round(value * 100) / 100;
}
