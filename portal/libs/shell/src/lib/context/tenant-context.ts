import { Injectable, computed, signal } from '@angular/core';

/**
 * A tenant the signed-in principal can act as.
 *
 * docs/plan/06 § — the identifiers here are opaque to the portal; it never derives authority from
 * them, it only reflects what the API already told it.
 */
export interface TenantRef {
  readonly id: string;
  readonly displayName: string;
}

/** A subscription within a tenant. */
export interface SubscriptionRef {
  readonly id: string;
  readonly tenantId: string;
  readonly displayName: string;
}

/**
 * Who the user is acting as, right now.
 *
 * ⚠ This is the single most safety-critical piece of state in the shell. docs/plan/20
 * § Information architecture, on the context bar: "Tenant + subscription switcher, always visible.
 * Getting this wrong means people act in the wrong subscription, which is a real and expensive
 * class of mistake."
 *
 * ⚠ **Why this is an `@Injectable` and not a module-level signal.** A `signal()` declared at module
 * scope is created once per *process*, and under SSR the process serves every user. Two concurrent
 * renders would then share one tenant — the exact leak docs/plan/20 § SSR calls "the worst bug this
 * document can prevent". Angular builds a fresh root injector for every `bootstrapApplication`
 * call, and the server renders each request with its own, so holding the state in DI makes the
 * isolation structural rather than a rule someone has to remember. `apps/portal/src/app/
 * ssr-isolation.server.spec.ts` is the test that keeps it that way.
 *
 * There is deliberately no `providedIn: 'root'` shortcut being avoided here — `providedIn: 'root'`
 * is per-injector, and per-injector is per-request on the server. It is module scope that is fatal.
 */
@Injectable({ providedIn: 'root' })
export class TenantContextStore {
  private readonly _tenants = signal<readonly TenantRef[]>([]);
  private readonly _subscriptions = signal<readonly SubscriptionRef[]>([]);
  private readonly _activeTenantId = signal<string | null>(null);
  private readonly _activeSubscriptionId = signal<string | null>(null);

  readonly tenants = this._tenants.asReadonly();
  readonly activeTenant = computed(
    () => this._tenants().find((t) => t.id === this._activeTenantId()) ?? null,
  );

  /**
   * Only the active tenant's subscriptions are ever offered. A subscription picker that lists
   * another tenant's subscriptions is one mis-click away from the mistake this store exists to
   * prevent.
   */
  readonly subscriptions = computed(() => {
    const tenantId = this._activeTenantId();
    return tenantId === null ? [] : this._subscriptions().filter((s) => s.tenantId === tenantId);
  });

  readonly activeSubscription = computed(
    () => this.subscriptions().find((s) => s.id === this._activeSubscriptionId()) ?? null,
  );

  /**
   * True until the API has told us who we are. The context bar renders a skeleton rather than an
   * empty switcher, because an empty switcher reads as "no subscriptions" and a skeleton reads as
   * "not yet".
   */
  readonly resolved = computed(() => this._activeTenantId() !== null);

  load(tenants: readonly TenantRef[], subscriptions: readonly SubscriptionRef[]): void {
    this._tenants.set(tenants);
    this._subscriptions.set(subscriptions);
  }

  /**
   * Switching tenant clears the subscription rather than guessing at one. Carrying a subscription
   * across a tenant switch is how a person ends up acting in a subscription they did not choose.
   */
  selectTenant(tenantId: string): void {
    if (this._activeTenantId() === tenantId) return;

    this._activeTenantId.set(tenantId);
    this._activeSubscriptionId.set(null);

    const only = this._subscriptions().filter((s) => s.tenantId === tenantId);
    if (only.length === 1) this._activeSubscriptionId.set(only[0].id);
  }

  selectSubscription(subscriptionId: string): void {
    const target = this.subscriptions().find((s) => s.id === subscriptionId);
    if (target === undefined) {
      throw new Error(
        `Subscription ${subscriptionId} does not belong to the active tenant. Refusing to switch — ` +
          'acting in the wrong subscription is the mistake the context bar exists to prevent.',
      );
    }

    this._activeSubscriptionId.set(subscriptionId);
  }
}
