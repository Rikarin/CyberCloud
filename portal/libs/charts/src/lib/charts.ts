import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, output } from '@angular/core';
import { XuiEChart } from '@xui/echarts';
import type { ECElementEvent } from 'echarts/core';
import { HistogramBucket, MetricSeries, histogramOption, timeSeriesOption, tokensOf } from './options';

/**
 * A line per series over a time axis — the metrics explorer's chart.
 *
 * ⚠ A canvas says nothing to a screen reader, so the chart carries a label and the page it sits
 * on carries the same numbers as a table (`xui-echart`'s own advice). The chart is the picture;
 * the table is the data.
 */
@Component({
  selector: 'cc-time-series-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiEChart],
  template: `<xui-echart class="h-72" [option]="option()" [aria-label]="label()" />`
})
export class TimeSeriesChart {
  readonly series = input.required<readonly MetricSeries[]>();
  readonly label = input.required<string>();

  protected readonly option = computed(() => timeSeriesOption(this.series()));
}

/**
 * Log records per bucket, stacked by severity — the log search's histogram. A click on a bar
 * emits that bucket's start, which the page narrows its window to.
 */
@Component({
  selector: 'cc-log-histogram',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiEChart],
  template: `<xui-echart class="h-32" [option]="option()" [aria-label]="label()" (chartClick)="onClick($event)" />`
})
export class LogHistogram {
  readonly buckets = input.required<readonly HistogramBucket[]>();
  readonly label = input.required<string>();
  /** A bucket's start, in epoch milliseconds, when its bar is clicked. */
  readonly bucketClick = output<number>();

  private readonly host = inject(ElementRef<HTMLElement>);

  protected readonly option = computed(() => histogramOption(this.buckets(), tokensOf(this.host.nativeElement)));

  protected onClick(event: ECElementEvent): void {
    const value = event.value;
    if (Array.isArray(value) && typeof value[0] === 'number') this.bucketClick.emit(value[0]);
  }
}
