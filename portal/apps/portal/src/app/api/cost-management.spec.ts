import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { apiVersion } from '@cybercloud/api';
import { CostManagementApi } from './cost-management';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
const SUBSCRIPTION = '0f9a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';

/**
 * The two hand-written addresses under `CyberCloud.CostManagement` — `CostQueryAddress` and
 * `InvoiceAddress` on the platform side, pinned by `CostQueryRoutingTests` and `InvoiceRoutingTests`.
 * A page that sent the right body to the wrong path is the failure nothing generated can prevent.
 */
describe('CostManagementApi', () => {
  let api: CostManagementApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()]
    });
    api = TestBed.inject(CostManagementApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('POSTs a query to the scope’s reserved address, with the period as instants and daily only when asked', async () => {
    const answered = api.query(
      { kind: 'resourceGroup', tenantId: TENANT, subscriptionId: SUBSCRIPTION, resourceGroup: 'prod web' },
      new Date('2026-09-01T00:00:00Z'),
      new Date('2026-10-01T00:00:00Z'),
      'resourceType',
      'daily'
    );
    await Promise.resolve();

    const request = http.expectOne(
      r =>
        r.method === 'POST' &&
        r.url ===
          `/api/tenants/${TENANT}/subscriptions/${SUBSCRIPTION}/resourceGroups/prod%20web/providers/CyberCloud.CostManagement/query`
    );
    expect(request.request.params.get('api-version')).toBe(apiVersion);
    expect(request.request.body).toEqual({
      from: '2026-09-01T00:00:00.000Z',
      to: '2026-10-01T00:00:00.000Z',
      groupBy: 'resourceType',
      granularity: 'daily'
    });
    request.flush({ currency: 'EUR', total: 1, filtered: false, rows: [] });

    await expect(answered).resolves.toMatchObject({ currency: 'EUR', total: 1 });

    void api.query(
      { kind: 'subscription', tenantId: TENANT, subscriptionId: SUBSCRIPTION },
      new Date(0),
      new Date(1),
      'day'
    );
    await Promise.resolve();
    const plain = http.expectOne(r =>
      r.url.endsWith(`/subscriptions/${SUBSCRIPTION}/providers/CyberCloud.CostManagement/query`)
    );
    expect(plain.request.body).not.toHaveProperty('granularity');
    plain.flush({});
  });

  it('GETs the tenant’s invoices, and one by its encoded number', async () => {
    const listed = api.invoices(TENANT);
    await Promise.resolve();
    http
      .expectOne(
        r => r.method === 'GET' && r.url === `/api/tenants/${TENANT}/providers/CyberCloud.CostManagement/invoices`
      )
      .flush({ value: [{ number: 'CC-INV-1' }] });
    await expect(listed).resolves.toEqual([{ number: 'CC-INV-1' }]);

    const one = api.invoice(TENANT, 'CC-INV/1');
    await Promise.resolve();
    http
      .expectOne(r => r.url === `/api/tenants/${TENANT}/providers/CyberCloud.CostManagement/invoices/CC-INV%2F1`)
      .flush({ number: 'CC-INV/1' });
    await expect(one).resolves.toEqual({ number: 'CC-INV/1' });
  });
});
