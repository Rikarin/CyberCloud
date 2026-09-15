import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TENANT_CONTEXT_SOURCE } from '@cybercloud/shell';
import { lazyTenantContextSource } from '../app.config';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';

/**
 * The seam between the shell's sign-in and the app's client, driven the way `app.config.ts`
 * wires it: the lazy factory, the generated `getTenant`, and the subscriptions collection, into
 * the shape `TenantContextStore.loadFromToken` takes.
 */
describe('TENANT_CONTEXT_SOURCE — the app’s answer to "which tenant is this token"', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TENANT_CONTEXT_SOURCE, useFactory: lazyTenantContextSource }
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the tenant and its subscriptions together, by the token’s tid', async () => {
    const snapshot = TestBed.inject(TENANT_CONTEXT_SOURCE).load(TENANT);

    // The dynamic import and the two calls it makes need a turn each.
    let tenant = http.match(r => r.url === `/api/tenants/${TENANT}`);
    for (let i = 0; i < 20 && tenant.length === 0; i++) {
      await new Promise(resolve => setTimeout(resolve, 5));
      tenant = http.match(r => r.url === `/api/tenants/${TENANT}`);
    }
    expect(tenant).toHaveLength(1);
    const subscriptions = http.expectOne(r => r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions`);
    expect(tenant[0].request.headers.has('Authorization')).toBe(false);

    tenant[0].flush({ id: `/tenants/${TENANT}`, name: 'Contoso', type: 'CyberCloud.Resources/tenants' });
    subscriptions.flush({
      value: [
        {
          id: `/tenants/${TENANT}/subscriptions/0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43`,
          name: 'Default',
          type: 'CyberCloud.Resources/subscriptions'
        }
      ]
    });

    await expect(snapshot).resolves.toEqual({
      displayName: 'Contoso',
      subscriptions: [{ id: '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43', tenantId: TENANT, displayName: 'Default' }]
    });
  });
});
