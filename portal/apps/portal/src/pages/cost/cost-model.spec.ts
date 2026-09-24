import { breakdown, displayName, forecastRange, linearForecast, money, parseDay, rangeOf } from './cost-model';

/**
 * The cost page's arithmetic, without a page: the period picker's ranges, the forecast's method and
 * the breakdown beside the chart. `cost-pages.spec.ts` drives the same functions through the page.
 */
describe('cost-model', () => {
  const now = new Date('2026-09-10T12:00:00Z');
  const iso = (d: Date | undefined) => d?.toISOString();

  describe('rangeOf — the period picker', () => {
    it('answers calendar months half-open, in UTC', () => {
      const month = rangeOf('month', now);
      expect([iso(month?.from), iso(month?.to)]).toEqual(['2026-09-01T00:00:00.000Z', '2026-10-01T00:00:00.000Z']);

      const last = rangeOf('lastMonth', new Date('2026-01-15T00:00:00Z'));
      expect([iso(last?.from), iso(last?.to)]).toEqual(['2025-12-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z']);
    });

    it('ends a rolling period at the end of today, so today is a bar of its own', () => {
      const days30 = rangeOf('30d', now);
      expect([iso(days30?.from), iso(days30?.to)]).toEqual(['2026-08-12T00:00:00.000Z', '2026-09-11T00:00:00.000Z']);
    });

    it('takes a custom last day inclusively, and refuses a backwards, missing or impossible pair', () => {
      const custom = rangeOf('custom', now, '2026-08-01', '2026-08-15');
      expect([iso(custom?.from), iso(custom?.to)]).toEqual(['2026-08-01T00:00:00.000Z', '2026-08-16T00:00:00.000Z']);

      expect(rangeOf('custom', now, '2026-08-15', '2026-08-01')).toBeNull();
      expect(rangeOf('custom', now, '2026-08-01', undefined)).toBeNull();
      expect(rangeOf('custom', now, '2026-02-30', '2026-03-01')).toBeNull();
      expect(parseDay('2026-9-1')).toBeNull();
    });
  });

  describe('linearForecast — the budget grain’s method, from day rows', () => {
    // The 1st and 2nd at 10 a day, then two a day from the 3rd to the 9th, and 1 so far today.
    const rows = [
      { name: '2026-09-01', amount: 10 },
      { name: '2026-09-02', amount: 10 },
      ...['03', '04', '05', '06', '07', '08', '09'].map(d => ({ name: `2026-09-${d}`, amount: 2 })),
      { name: '2026-09-10', amount: 1 }
    ];

    it('asks for this month and the seven days before today, whichever starts first', () => {
      const window = forecastRange(now);
      expect([iso(window.from), iso(window.to)]).toEqual(['2026-09-01T00:00:00.000Z', '2026-09-10T12:00:00.000Z']);

      // On the 2nd the trailing week reaches back into August.
      expect(iso(forecastRange(new Date('2026-09-02T06:00:00Z')).from)).toBe('2026-08-26T00:00:00.000Z');
    });

    it('is the month so far plus the trailing seven days’ hourly rate for the hours left', () => {
      // Trailing window: the 3rd 00:00 to the 10th 12:00 is 180 hours carrying 15, so 1/12 an hour.
      // Hours left to 1 October: 492. 35 + 492/12 = 76.
      const forecast = linearForecast(rows, now);

      expect(forecast).toEqual({ actual: 35, forecast: 76, perDay: 2 });
    });

    it('does not let last month’s trailing days into the month’s actual', () => {
      const early = new Date('2026-09-02T12:00:00Z');
      const withAugust = [
        { name: '2026-08-30', amount: 100 },
        { name: '2026-09-01', amount: 4 }
      ];

      expect(linearForecast(withAugust, early).actual).toBe(4);
    });
  });

  it('breaks a daily answer down per key, most expensive first, with shares', () => {
    const rows = [
      { day: '2026-09-01', name: 'prod', amount: 2.4 },
      { day: '2026-09-02', name: 'dev', amount: 0.12 },
      { day: '2026-09-02', name: 'prod', amount: 0.1 }
    ];

    expect(breakdown(rows)).toEqual([
      { name: 'prod', amount: 2.5, share: 95.4 },
      { name: 'dev', amount: 0.12, share: 4.6 }
    ]);
  });

  it('shortens a resource path to its type and name, and leaves anything else alone', () => {
    expect(
      displayName('/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Compute/virtualMachines/web')
    ).toBe('virtualMachines/web');
    expect(displayName('CyberCloud.Compute/virtualMachines')).toBe('CyberCloud.Compute/virtualMachines');
  });

  it('prints money in the currency’s own minor unit', () => {
    expect(money(3.03, 'EUR')).toBe('€3.03');
    expect(money(1200, 'JPY')).toBe('¥1,200');
    expect(money(2.5, '')).toBe('2.50');
  });
});
