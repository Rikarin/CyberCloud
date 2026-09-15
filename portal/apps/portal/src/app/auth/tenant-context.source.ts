import { Injectable, inject } from '@angular/core';
import { TenantContextSnapshot, TenantContextSource } from '@cybercloud/shell';
import { PlatformApi } from '../api/platform-api';
import { ScopeCollections } from '../api/scope-collections';

/**
 * The tenant a token names, as the platform describes it: `GET /api/tenants/{tid}` for the name
 * and `GET /api/tenants/{tid}/subscriptions` for the switcher — the two calls the callback page
 * and the reload path make right after a token is accepted.
 *
 * The two go out together. Neither depends on the other, and a person waiting on the sign-in
 * callback waits on the slower of the two rather than the sum.
 */
@Injectable({ providedIn: 'root' })
export class PlatformTenantContextSource implements TenantContextSource {
  private readonly api = inject(PlatformApi);
  private readonly scopes = inject(ScopeCollections);

  async load(tenantId: string): Promise<TenantContextSnapshot> {
    const [tenant, subscriptions] = await Promise.all([
      this.api.getTenant(tenantId),
      this.scopes.allSubscriptions(tenantId)
    ]);

    return { displayName: tenant.value.name, subscriptions };
  }
}
