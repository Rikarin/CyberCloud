import { InjectionToken } from '@angular/core';
import { SubscriptionRef } from '../context/tenant-context';

/** What the platform says about the tenant a token names: its display name and its subscriptions. */
export interface TenantContextSnapshot {
  readonly displayName: string;
  readonly subscriptions: readonly SubscriptionRef[];
}

/**
 * Where the shell asks for a tenant's context once it holds a token for it.
 *
 * The shell knows the tenant from the token's `tid` and nothing else; the display name and the
 * subscriptions are `GET /api/tenants/{tid}` and `GET /api/tenants/{tid}/subscriptions` through
 * the generated client — which lives in the app, not here, because `libs/shell` cannot depend on
 * `apps/portal`. The app provides this token in `app.config.ts` over its `ScopeCollections`
 * client; the shell's sign-in flow calls it after every token acquisition that finds
 * `TenantContextStore` unresolved, which is the callback page once and the reload path's
 * refresh once.
 */
export interface TenantContextSource {
  load(tenantId: string): Promise<TenantContextSnapshot>;
}

/**
 * ⚠ **No default.** A default that answered an empty subscription list would render a signed-in
 * portal as "no subscriptions" and read as a permissions problem; `AuthFlow.ensureContext` throws
 * naming this token when the app forgot to provide it.
 */
export const TENANT_CONTEXT_SOURCE = new InjectionToken<TenantContextSource>('cc.tenantContextSource');
