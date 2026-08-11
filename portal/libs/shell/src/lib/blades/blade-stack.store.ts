import { Injectable, computed, signal } from '@angular/core';

/**
 * One blade in the stack.
 *
 * docs/plan/20 § Information architecture: "Blades — Stacked, deep-linkable panels."
 * `deep-linkable` is the load-bearing word: a blade's identity has to survive being written into a
 * URL and read back, which is why this is plain serialisable data and not a component instance.
 */
export interface BladeRef {
  /** Stable within a stack; also what the URL segment carries. */
  readonly id: string;
  /** Shown in the blade header and as the back label on the blade above — `@xui/panel-stack`. */
  readonly title: string;
  /** The route the blade's content is lazily loaded from. Route-level splitting, docs/plan/20 § Performance budget. */
  readonly route: string;
  /** The resource this blade is about, when it is about one. Drives the breadcrumb trail. */
  readonly resourceId?: string;
}

/**
 * The blade stack.
 *
 * ⚠ Per-injector, therefore per-SSR-request. See `TenantContextStore` for why that matters — the
 * same reasoning applies to every store in the shell and this one holds resource ids, which are
 * tenant-scoped.
 */
@Injectable({ providedIn: 'root' })
export class BladeStackStore {
  private readonly _blades = signal<readonly BladeRef[]>([]);

  readonly blades = this._blades.asReadonly();
  readonly depth = computed(() => this._blades().length);
  readonly top = computed<BladeRef | null>(() => this._blades().at(-1) ?? null);

  /**
   * Pushing a blade that is already open pops back to it rather than stacking a second copy.
   * Azure's portal does the same thing, and the alternative — two blades for one resource, each
   * with its own idea of the resource's state — is a stale-data bug that looks like a UI bug.
   */
  open(blade: BladeRef): void {
    const existing = this._blades().findIndex((b) => b.id === blade.id);

    if (existing >= 0) {
      this._blades.update((blades) => blades.slice(0, existing + 1));
      return;
    }

    this._blades.update((blades) => [...blades, blade]);
  }

  /** Pop the top blade. A no-op on the root, which is always something to show. */
  close(): void {
    this._blades.update((blades) => (blades.length <= 1 ? blades : blades.slice(0, -1)));
  }

  /** Pop back to a blade already in the stack. Used by the breadcrumb trail. */
  popTo(id: string): void {
    const index = this._blades().findIndex((b) => b.id === id);
    if (index >= 0) this._blades.update((blades) => blades.slice(0, index + 1));
  }

  /** Replace the whole stack — how a deep link restores a trail on a cold load. */
  restore(blades: readonly BladeRef[]): void {
    this._blades.set([...blades]);
  }

  reset(): void {
    this._blades.set([]);
  }
}
