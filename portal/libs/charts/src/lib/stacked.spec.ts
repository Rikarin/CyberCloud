import { MAX_KEYS, OTHER, daysBetween, stackByDay, stackedBarOption } from './stacked';

/** The pivot behind every stacked daily chart, and the palette rule it keeps. */
describe('stackByDay', () => {
  const days = daysBetween(new Date('2026-09-01T00:00:00Z'), new Date('2026-09-04T00:00:00Z'));

  it('lists every day of the range, the empty ones included', () => {
    expect(days).toEqual(['2026-09-01', '2026-09-02', '2026-09-03']);
  });

  it('gives each key a series over the days, most expensive first, and a missing day zero', () => {
    const stacked = stackByDay(
      [
        { day: '2026-09-01', name: 'dev', amount: 0.25 },
        { day: '2026-09-01', name: 'prod', amount: 2.4 },
        { day: '2026-09-03', name: 'prod', amount: 0.1 },
        // Outside the range: ignored rather than stretching the axis.
        { day: '2026-09-09', name: 'prod', amount: 100 }
      ],
      days
    );

    expect(stacked.series).toEqual([
      { name: 'prod', data: [2.4, 0, 0.1] },
      { name: 'dev', data: [0.25, 0, 0] }
    ]);
  });

  it('keeps eight series at most, folding everything past the seventh into Other', () => {
    const rows = Array.from({ length: 10 }, (_, i) => ({ day: '2026-09-02', name: `k${i}`, amount: 10 - i }));

    const stacked = stackByDay(rows, days);

    expect(stacked.series).toHaveLength(MAX_KEYS + 1);
    expect(stacked.series.at(-1)).toEqual({ name: OTHER, data: [0, 3 + 2 + 1, 0] });

    // Eight keys fit the palette exactly and nothing is folded.
    expect(stackByDay(rows.slice(0, 8), days).series.map(s => s.name)).not.toContain(OTHER);
  });

  it('stacks every series on one axis and leaves colour to the theme', () => {
    const option = stackedBarOption(stackByDay([{ day: '2026-09-01', name: 'prod', amount: 1 }], days), 'EUR');

    expect(option).toMatchObject({
      xAxis: { type: 'category', data: days },
      series: [{ type: 'bar', stack: 'cost', name: 'prod', data: [1, 0, 0] }]
    });
    expect(option).not.toHaveProperty('color');
  });
});
