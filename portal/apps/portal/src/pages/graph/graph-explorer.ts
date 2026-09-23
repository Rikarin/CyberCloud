import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { XuiButton } from '@xui/button';
import { XuiInput } from '@xui/input';
import { XuiTable, XuiTd, XuiTh, XuiTr } from '@xui/table';
import { GraphColumn, GraphPage, ResourceGraphApi } from '../../app/api/resource-graph';
import { NeedsTenant, PageState, PageStatus, activeTenantId, failure } from '../shared/page-state';

/** The rows the page has, across every page it has fetched for the current query. */
interface Results {
  readonly query: string;
  readonly columns: readonly GraphColumn[];
  readonly rows: readonly Readonly<Record<string, unknown>>[];
  readonly skipToken: string | null;
}

/** How many rows one request asks for. */
export const GRAPH_PAGE_SIZE = 50;

/**
 * Starting points, each a query the subset accepts — so the first thing a person runs works, and
 * shows them the shape of what else will.
 */
export const GRAPH_SAMPLES: readonly { readonly label: string; readonly query: string }[] = [
  {
    label: $localize`:@@graph.sample.all:Every resource`,
    query: 'resources | project name, type, location, resourceGroup | order by name asc'
  },
  {
    label: $localize`:@@graph.sample.byType:Count by type`,
    query: 'resources | summarize count() by type | order by count_ desc'
  },
  {
    label: $localize`:@@graph.sample.untagged:Untagged in production groups`,
    query: "resources | where resourceGroup startswith 'prod' and isempty(tags.env) | project name, type"
  },
  {
    label: $localize`:@@graph.sample.failed:Not succeeded`,
    query: "resources | where provisioningState != 'Succeeded' | project name, type, provisioningState"
  }
];

/**
 * The resource graph explorer: a KQL box over #54's query address, and the rows it answers as a
 * table — docs/plan/08 § The resource-graph projection, which the portal's list pages were always
 * meant to read (docs/plan/20 § Information architecture, "Resource list").
 *
 * ⚠ **A subset, and the platform says which.** The translator accepts the KQL `KqlSubset` lists
 * and refuses everything else with a `400` that names the token and the supported set; this page
 * shows that sentence as it came, because it is the documentation. The caller's access is ANDed in
 * by the platform — two people running the same query see different rows, and neither sees a
 * resource they could not open.
 *
 * ⚠ **Paged by the platform's token, never by its link.** `ResourceGraphApi` reads `$skipToken`
 * off `nextLink` and sends the same query again with it; "Load more" appends, and a new query
 * starts over. A token belongs to the query it came with — the platform refuses it with another.
 */
@Component({
  selector: 'cc-graph-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButton, XuiInput, XuiTable, XuiTr, XuiTh, XuiTd, NeedsTenant, PageStatus],
  host: { class: 'block p-6' },
  template: `
    @if (tenantId() === null) {
      <cc-needs-tenant />
    } @else {
      <h1 class="text-lg font-semibold" i18n="@@graph.heading">Resource graph</h1>
      <p class="text-foreground-muted mt-1 text-sm" i18n="@@graph.intro">
        Every resource you can read in this tenant, queried with a subset of KQL.
      </p>

      <form class="mt-4 flex flex-col gap-3" (submit)="onRun($event)">
        <label class="text-sm font-medium" for="cc-graph-query" i18n="@@graph.query">Query</label>
        <textarea
          xuiInput
          id="cc-graph-query"
          rows="4"
          class="font-mono text-sm"
          spellcheck="false"
          [value]="query()"
          (input)="onQuery($event)"
        ></textarea>

        <div class="flex flex-wrap items-center gap-2">
          <button
            xuiButton
            color="primary"
            size="sm"
            type="submit"
            [disabled]="query().trim().length === 0 || busy()"
            [loading]="busy() && results() === null"
            i18n="@@graph.run"
          >
            Run query
          </button>
          <span class="text-foreground-muted ml-2 text-xs" i18n="@@graph.samples">Try:</span>
          @for (sample of samples; track sample.query) {
            <button xuiButton variant="ghost" size="sm" type="button" (click)="useSample(sample.query)">
              {{ sample.label }}
            </button>
          }
        </div>
      </form>

      <cc-page-status class="mt-4 block" [state]="status()" />

      @if (results(); as results) {
        @if (results.rows.length === 0) {
          <p class="text-foreground-muted mt-4 text-sm" data-empty i18n="@@graph.empty">No rows.</p>
        } @else {
          <xui-table class="mt-4" striped [attr.aria-label]="tableLabel">
            <xui-tr>
              @for (column of results.columns; track column.name) {
                <xui-th role="columnheader" class="flex-1" [attr.data-column]="column.name"
                  >{{ column.name }} <span class="text-foreground-muted text-xs">{{ column.type }}</span></xui-th
                >
              }
            </xui-tr>
            @for (row of results.rows; track $index) {
              <xui-tr>
                @for (column of results.columns; track column.name) {
                  <xui-td role="cell" class="flex-1" truncate
                    ><span class="text-sm">{{ cell(row[column.name]) }}</span></xui-td
                  >
                }
              </xui-tr>
            }
          </xui-table>
        }

        <p class="text-foreground-muted mt-2 text-xs" data-count>
          {{ countLabel() }}
        </p>

        @if (results.skipToken !== null) {
          <button
            xuiButton
            variant="outline"
            size="sm"
            type="button"
            class="mt-2"
            [loading]="busy()"
            [disabled]="busy()"
            (click)="onMore()"
            i18n="@@graph.more"
          >
            Load more
          </button>
        }
      }
    }
  `
})
export class GraphExplorer {
  private readonly api = inject(ResourceGraphApi);
  protected readonly tenantId = activeTenantId();

  protected readonly samples = GRAPH_SAMPLES;
  protected readonly query = signal(GRAPH_SAMPLES[0]?.query ?? '');
  protected readonly results = signal<Results | null>(null);
  protected readonly status = signal<PageState<unknown>>({ kind: 'idle' });
  protected readonly busy = computed(() => this.status().kind === 'loading');

  protected readonly countLabel = computed(() => {
    const results = this.results();
    if (results === null) return '';
    return results.skipToken === null
      ? $localize`:@@graph.count.all:${results.rows.length}:count: rows.`
      : $localize`:@@graph.count.more:${results.rows.length}:count: rows so far; there are more.`;
  });

  protected readonly tableLabel = $localize`:@@graph.table:Query results`;

  private generation = 0;

  constructor() {
    effect(() => {
      this.tenantId();

      untracked(() => {
        this.generation++;
        this.results.set(null);
        this.status.set({ kind: 'idle' });
      });
    });
  }

  protected onQuery(event: Event): void {
    this.query.set((event.target as HTMLTextAreaElement).value);
  }

  protected useSample(query: string): void {
    this.query.set(query);
  }

  protected async onRun(event: Event): Promise<void> {
    event.preventDefault();
    this.results.set(null);
    await this.fetch(this.query().trim(), null);
  }

  protected async onMore(): Promise<void> {
    const results = this.results();
    if (results?.skipToken != null) await this.fetch(results.query, results.skipToken);
  }

  /** A cell as text: a tag map or an array as JSON, a missing value as a dash. */
  protected cell(value: unknown): string {
    if (value === null || value === undefined || value === '') return '—';
    return typeof value === 'object' ? JSON.stringify(value) : String(value);
  }

  private async fetch(query: string, skipToken: string | null): Promise<void> {
    const tenantId = this.tenantId();
    if (tenantId === null || query.length === 0) return;

    const generation = this.generation;
    this.status.set({ kind: 'loading' });

    try {
      const page: GraphPage = await this.api.query(tenantId, query, GRAPH_PAGE_SIZE, skipToken);
      if (generation !== this.generation) return;

      const previous = skipToken === null ? null : this.results();
      this.results.set({
        query,
        columns: page.columns,
        rows: [...(previous?.rows ?? []), ...page.rows],
        skipToken: page.skipToken
      });
      this.status.set({ kind: 'idle' });
    } catch (error) {
      if (generation === this.generation) this.status.set(failure(error));
    }
  }
}
