/**
 * The windows the explorers offer, relative to now.
 *
 * Relative rather than absolute because both pages are "what is happening" pages first; an absolute
 * window arrives by clicking a histogram bar, which narrows to that bucket.
 */
export const timeRanges = [
  { key: '15m', ms: 15 * 60_000, label: $localize`:@@monitor.range.15m:Last 15 minutes` },
  { key: '1h', ms: 60 * 60_000, label: $localize`:@@monitor.range.1h:Last hour` },
  { key: '6h', ms: 6 * 60 * 60_000, label: $localize`:@@monitor.range.6h:Last 6 hours` },
  { key: '24h', ms: 24 * 60 * 60_000, label: $localize`:@@monitor.range.24h:Last 24 hours` },
  { key: '7d', ms: 7 * 24 * 60 * 60_000, label: $localize`:@@monitor.range.7d:Last 7 days` },
  { key: '30d', ms: 30 * 24 * 60 * 60_000, label: $localize`:@@monitor.range.30d:Last 30 days` }
] as const;

export type TimeRange = (typeof timeRanges)[number];

/** An explicit window: where it starts and ends. */
export interface Window {
  readonly from: Date;
  readonly to: Date;
}

/** The window a relative range covers, ending at `now`. */
export function windowOf(range: TimeRange, now: Date): Window {
  return { from: new Date(now.getTime() - range.ms), to: now };
}

/** An instant as the platform's `SchemaFormat.DateTime` takes it: RFC 3339, UTC. */
export function stamp(instant: Date): string {
  return instant.toISOString();
}
