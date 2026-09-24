import { formatValue, histogramOption, timeSeriesOption } from './options';

describe('the chart options', () => {
  it('draws one line per series on a time axis, and keeps a gap a gap', () => {
    const option = timeSeriesOption([
      { name: 'up{job="a"}', timestamps: [1000, 2000, 3000], values: [1, null, 3], unit: '' },
      { name: 'up{job="b"}', timestamps: [1000], values: [5], unit: '' }
    ]);

    expect(option.xAxis).toEqual({ type: 'time' });
    const series = option.series as { name: string; connectNulls: boolean; showSymbol: boolean; data: unknown[] }[];
    expect(series.map(s => s.name)).toEqual(['up{job="a"}', 'up{job="b"}']);
    expect(series[0]?.data).toEqual([
      [1000, 1],
      [2000, null],
      [3000, 3]
    ]);
    expect(series[0]?.connectNulls).toBe(false);
    // A lone point has no line to show it, so it gets a symbol.
    expect(series[1]?.showSymbol).toBe(true);
  });

  it('stacks the severities that occur, in severity order, each in its token', () => {
    const option = histogramOption(
      [
        { start: 0, bySeverity: { error: 2, info: 5 } },
        { start: 60_000, bySeverity: { unspecified: 1 } }
      ],
      token => `color(${token})`
    );

    const series = option.series as { name: string; stack: string; itemStyle: { color: string }; data: unknown[] }[];
    expect(series.map(s => s.name)).toEqual(['unspecified', 'info', 'error']);
    expect(series.every(s => s.stack === 'severity')).toBe(true);
    expect(series.find(s => s.name === 'error')?.itemStyle.color).toBe('color(--error)');
    expect(series.find(s => s.name === 'info')?.data).toEqual([
      [0, 5],
      [60_000, 0]
    ]);
  });

  it('formats a value short, with its unit, and a gap as a dash', () => {
    expect(formatValue(1_234_567)).toBe('1.23M');
    expect(formatValue(42)).toBe('42');
    expect(formatValue(0.000123)).toBe('0.000123');
    expect(formatValue(3 * 1024 * 1024, 'bytes')).toBe('3 MiB');
    expect(formatValue(1500, 'req/s')).toBe('1.5k req/s');
    expect(formatValue(null)).toBe('—');
    expect(formatValue(Number.NaN)).toBe('—');
  });
});
