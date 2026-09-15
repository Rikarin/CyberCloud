import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { ScopeCollections, displayNameOf, lastSegmentOf } from './scope-collections';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
const V = `api-version=${apiVersion}`;

/**
 * The hand-written seam for the two scope collections, against the contract's § 6: the address,
 * the envelope, and `$skipToken` paging — never `nextLink` followed as a URL.
 */
describe('ScopeCollections — GET /tenants/{tid}/subscriptions and …/resourceGroups', () => {
  let scopes: ScopeCollections;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()]
    });
    scopes = TestBed.inject(ScopeCollections);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('walks every page by $skipToken and answers the switcher’s shape', async () => {
    const all = scopes.allSubscriptions(TENANT);
    await Promise.resolve();

    const first = http.expectOne(r => r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions`);
    expect(first.request.params.get('$top')).toBe('1000');
    expect(first.request.params.get('api-version')).toBe(apiVersion);
    expect(first.request.params.has('$skipToken')).toBe(false);
    first.flush({
      value: [{ id: `/tenants/${TENANT}/subscriptions/s-1`, name: 'One', type: 'CyberCloud.Resources/subscriptions' }],
      // ⚠ An absolute link on an origin the portal must never send its token to. Only the token
      // is taken from it.
      nextLink: `https://evil.example/tenants/${TENANT}/subscriptions?${V}&$skipToken=after-s-1`
    });
    await new Promise(resolve => setTimeout(resolve, 0));

    const second = http.expectOne(r => r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions`);
    expect(second.request.params.get('$skipToken')).toBe('after-s-1');
    second.flush({
      value: [
        {
          id: `/tenants/${TENANT}/subscriptions/s-2`,
          name: 's-2',
          type: 'CyberCloud.Resources/subscriptions',
          properties: { displayName: 'Two' }
        }
      ]
    });

    await expect(all).resolves.toEqual([
      { id: 's-1', tenantId: TENANT, displayName: 'One' },
      { id: 's-2', tenantId: TENANT, displayName: 'Two' }
    ]);
    http.expectNone(r => r.url.startsWith('https://evil.example'));
  });

  it('addresses the resource-group collection under its subscription', async () => {
    const page = scopes.listResourceGroups(TENANT, 's-1', { top: 5 });
    await Promise.resolve();

    const request = http.expectOne(
      r => r.method === 'GET' && r.url === `/api/tenants/${TENANT}/subscriptions/s-1/resourceGroups`
    );
    expect(request.request.params.get('$top')).toBe('5');
    request.flush({ value: [] });

    await expect(page).resolves.toMatchObject({ status: 200, value: { value: [] } });
  });

  it('reads the id from the address and the label from properties or name', () => {
    const item = {
      id: `/tenants/${TENANT}/subscriptions/0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43`,
      name: 'Acme Production',
      type: 'CyberCloud.Resources/subscriptions'
    };

    expect(lastSegmentOf(item)).toBe('0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43');
    expect(displayNameOf(item)).toBe('Acme Production');
    expect(displayNameOf({ ...item, properties: { displayName: 'Default' } })).toBe('Default');
    expect(displayNameOf({ ...item, properties: { displayName: '' } })).toBe('Acme Production');
  });
});
