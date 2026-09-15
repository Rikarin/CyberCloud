import { Component, PLATFORM_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { FakeTokenEndpoint, MemoryCookieJar, unsignedJwt } from '@cybercloud/shell/testing';
import { TenantContextStore } from '../context/tenant-context';
import { AccessTokenStore } from './access-token-store';
import { AUTH_NAVIGATE } from './auth-flow';
import { AuthSession } from './auth-session';
import { authGuard } from './auth.guard';
import { CookieJar } from './cookies';
import { IDENTITY_ISSUER } from './identity-issuer';
import { TENANT_CONTEXT_SOURCE } from './tenant-context-source';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
/** The same tenant as an address spells it — what `AuthSession` answers from the claim above. */
const TENANT_ADDRESS = '7f3a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';

@Component({ selector: 'cc-guarded', template: '<span i18n="@@test.guarded">guarded</span>' })
class Guarded {}

/**
 * The guard is what makes a reload land back on the page: the store is empty, the identity
 * host's refresh cookie is not, and one refresh tells the two apart before anyone is redirected.
 */
describe('authGuard', () => {
  let navigated: string[];
  let endpoint: FakeTokenEndpoint | null;
  let loads: string[];

  function configure(platform: 'browser' | 'server' = 'browser'): void {
    navigated = [];
    loads = [];
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'subscriptions', canActivate: [authGuard], component: Guarded }]),
        { provide: PLATFORM_ID, useValue: platform },
        { provide: IDENTITY_ISSUER, useValue: 'http://localhost:5101' },
        { provide: CookieJar, useClass: MemoryCookieJar },
        { provide: AUTH_NAVIGATE, useValue: (url: string) => navigated.push(url) },
        {
          provide: TENANT_CONTEXT_SOURCE,
          useValue: {
            load: async (tenantId: string) => {
              loads.push(tenantId);
              return { displayName: 'Contoso', subscriptions: [] };
            }
          }
        }
      ]
    });
  }

  afterEach(() => endpoint?.restore());

  it('noTokenTriesARefreshBeforeRedirecting', async () => {
    configure();
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: { access_token: unsignedJwt({ sub: 'u-1', tid: TENANT }), token_type: 'Bearer', expires_in: 600 }
    });

    const allowed = await TestBed.inject(Router).navigateByUrl('/subscriptions');

    expect(allowed).toBe(true);
    expect(endpoint.calls.map(c => c.form.get('grant_type'))).toEqual(['refresh_token']);
    expect(navigated).toEqual([]);
    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(true);
    // The reload path loads the context the same way the callback does.
    expect(loads).toEqual([TENANT_ADDRESS]);
    expect(TestBed.inject(TenantContextStore).activeTenant()?.displayName).toBe('Contoso');
  });

  it('redirects to /authorize with the requested URL as the return path when the refresh is refused', async () => {
    configure();
    endpoint = new FakeTokenEndpoint({ status: 400, body: { error: 'invalid_request' } });

    const allowed = await TestBed.inject(Router).navigateByUrl('/subscriptions');

    expect(allowed).toBe(false);
    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toMatch(/^http:\/\/localhost:5101\/authorize\?/);
    const pkce = (TestBed.inject(CookieJar) as MemoryCookieJar).read('cyc-pkce');
    expect(pkce?.split('.').slice(2).join('.')).toBe('/subscriptions');
    expect(loads).toEqual([]);
  });

  it('passes on a live token without touching the identity host', async () => {
    configure();
    endpoint = new FakeTokenEndpoint({ status: 500, body: {} });
    TestBed.inject(AuthSession).accept({
      access_token: unsignedJwt({ sub: 'u-1', tid: TENANT }),
      token_type: 'Bearer',
      expires_in: 600
    });

    expect(await TestBed.inject(Router).navigateByUrl('/subscriptions')).toBe(true);
    expect(endpoint.calls).toEqual([]);
    expect(navigated).toEqual([]);
    expect(loads).toEqual([TENANT_ADDRESS]);
  });

  it('on the server passes without a token, a refresh, or a redirect — docs/plan/20 § SSR', async () => {
    configure('server');
    endpoint = new FakeTokenEndpoint({ status: 500, body: {} });

    expect(await TestBed.inject(Router).navigateByUrl('/subscriptions')).toBe(true);
    expect(endpoint.calls).toEqual([]);
    expect(navigated).toEqual([]);
    expect(loads).toEqual([]);
    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(false);
  });
});
