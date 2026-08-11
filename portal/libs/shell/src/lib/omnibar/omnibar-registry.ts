import { Injectable, computed, signal } from '@angular/core';

/** What kind of thing a result is, which is also how the omnibar groups its rows. */
export type OmnibarResultKind = 'resource' | 'action' | 'tenant' | 'subscription' | 'doc';

/** One row in the omnibar. */
export interface OmnibarResult {
  readonly id: string;
  readonly label: string;
  readonly kind: OmnibarResultKind;
  /** A registered `@ng-icons` name — `@xui/omnibar` resolves it. */
  readonly icon?: string;
  /** Secondary line: a resource group, a subscription name, a keyboard hint. */
  readonly description?: string;
  /** Where choosing the row goes. Mutually exclusive with `run`. */
  readonly route?: string;
  /** What choosing the row does, for rows that are commands rather than places. */
  readonly run?: () => void;
}

/**
 * A source of omnibar results. Registering rather than hard-coding is what lets a feature area
 * contribute its own commands without the shell importing it — which would defeat the route-level
 * code splitting docs/plan/20 § Performance budget calls mandatory.
 */
export interface OmnibarSource {
  readonly id: string;
  /**
   * Called on every settled keystroke including the empty query, per `@xui/omnibar`'s provider
   * contract. An empty query is the source's chance to offer its most likely rows.
   */
  search(query: string, signal: AbortSignal): Promise<readonly OmnibarResult[]>;
}

/**
 * The omnibar's result index.
 *
 * ⚠ docs/plan/20 § Information architecture calls the omnibar "**The primary navigation**", with
 * the reasoning: "Deep hierarchies are unnavigable by clicking and everyone who uses a cloud daily
 * uses the search box." That is a statement about priority, and it has two consequences here.
 *
 * First, the omnibar is not a feature of a page — it is mounted by the shell, bound to `mod+k`
 * globally, and available before any route has loaded. Second, its sources federate: a resource
 * search that has to wait for a docs search is a navigation that feels broken, so `search` settles
 * each source independently and merges whatever arrived.
 */
@Injectable({ providedIn: 'root' })
export class OmnibarRegistry {
  private readonly sources = signal<readonly OmnibarSource[]>([]);
  private readonly _recent = signal<readonly OmnibarResult[]>([]);

  /** Shown when the query is empty. `@xui/omnibar` renders these under a "Recent" heading. */
  readonly recent = computed(() => this._recent().slice(0, 8));

  register(source: OmnibarSource): () => void {
    this.sources.update((sources) => [...sources.filter((s) => s.id !== source.id), source]);
    return () => this.sources.update((sources) => sources.filter((s) => s.id !== source.id));
  }

  /**
   * Fan out to every source and merge. A source that throws or times out contributes nothing
   * rather than failing the search — losing the docs results is a worse outcome than showing the
   * resource results alone, and the primary navigation must not have a single point of failure.
   */
  async search(query: string, signal: AbortSignal): Promise<readonly OmnibarResult[]> {
    const settled = await Promise.allSettled(
      this.sources().map((source) => source.search(query, signal)),
    );

    return settled.flatMap((r) => (r.status === 'fulfilled' ? r.value : []));
  }

  /**
   * docs/plan/20 does not say the omnibar remembers anything, and `@xui/omnibar` is explicit that
   * "The palette remembers nothing itself: what counts as recent is the application's call". This
   * is that call — most-recently-chosen, deduplicated, capped.
   */
  remember(result: OmnibarResult): void {
    this._recent.update((recent) => [result, ...recent.filter((r) => r.id !== result.id)].slice(0, 16));
  }
}
