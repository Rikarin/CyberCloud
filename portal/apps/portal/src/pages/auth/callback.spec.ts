import { Component, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter } from '@angular/router';
import {
  AUTH_NAVIGATE,
  AccessTokenStore,
  AuthSession,
  CookieJar,
  IDENTITY_ISSUER,
  PKCE_COOKIE,
  TENANT_CONTEXT_SOURCE,
  TenantContextStore
} from '@cybercloud/shell';
import { FakeTokenEndpoint, MemoryCookieJar, unsignedJwt } from '@cybercloud/shell/testing';
import axe from 'axe-core';
import { AuthCallback } from './callback';

const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
const STATE = 'the-state';
const VERIFIER = 'the-verifier';

@Component({ selector: 'cc-landing', template: '<span i18n="@@test.landed">landed</span>' })
class Landing {}

@Component({ selector: 'cc-callback-host', imports: [RouterOutlet], template: '<main><router-outlet /></main>' })
class Host {}

/**
 * `/auth/callback`: the page the identity host sends the browser back to. What it shows and where
 * it goes are the contract's § 7 — the tenant from the access token's `tid`, the person from the
 * id_token, and every failure inline with a retry rather than in the console.
 */
describe('the sign-in callback page', () => {
  let fixture: ComponentFixture<Host>;
  let router: Router;
  let cookies: MemoryCookieJar;
  let endpoint: FakeTokenEndpoint;
  let navigated: string[];

  beforeEach(() => {
    navigated = [];
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([
          { path: 'auth/callback', component: AuthCallback },
          { path: 'subscriptions/:id', component: Landing },
          { path: '', component: Landing }
        ]),
        { provide: IDENTITY_ISSUER, useValue: 'http://localhost:5101' },
        { provide: CookieJar, useClass: MemoryCookieJar },
        { provide: AUTH_NAVIGATE, useValue: (url: string) => navigated.push(url) },
        {
          provide: TENANT_CONTEXT_SOURCE,
          useValue: {
            load: async (tenantId: string) => ({
              displayName: `Tenant ${tenantId.slice(0, 4)}`,
              subscriptions: [{ id: 's-1', tenantId, displayName: 'Default' }]
            })
          }
        }
      ]
    });
    router = TestBed.inject(Router);
    cookies = TestBed.inject(CookieJar) as MemoryCookieJar;
    cookies.write(PKCE_COOKIE, `${STATE}.${VERIFIER}./subscriptions/s-1`, {
      path: '/auth/callback',
      maxAgeSeconds: 600
    });
    fixture = TestBed.createComponent(Host);
  });

  afterEach(() => endpoint.restore());

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) await new Promise(resolve => setTimeout(resolve, 0));
    await fixture.whenStable();
  }

  it('tenantComesFromTid', async () => {
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: {
        access_token: unsignedJwt({ sub: 'u-1', tid: TENANT, aud: 'cyc.api' }),
        token_type: 'Bearer',
        expires_in: 600
      }
    });

    await router.navigateByUrl(`/auth/callback?code=the-code&state=${STATE}`);
    await settle();

    expect(endpoint.calls[0].form.get('code_verifier')).toBe(VERIFIER);
    const context = TestBed.inject(TenantContextStore);
    expect(context.activeTenant()).toEqual({ id: TENANT, displayName: 'Tenant 7f3a' });
    expect(context.activeSubscription()?.id).toBe('s-1');
    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(true);
    expect(router.url).toBe('/subscriptions/s-1');
    expect(cookies.read(PKCE_COOKIE)).toBeNull();
  });

  it('emailAndNameComeFromTheIdToken', async () => {
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: {
        // The access token deliberately carries neither claim — `AccessTokenClaims.Permitted`
        // keeps `email` and `name` out of it — so the only place they can come from is the id_token.
        access_token: unsignedJwt({ sub: 'u-1', tid: TENANT }),
        id_token: unsignedJwt({ sub: 'u-1', tid: TENANT, email: 'rene@example.com', name: 'Rene' }),
        token_type: 'Bearer',
        expires_in: 600
      }
    });

    await router.navigateByUrl(`/auth/callback?code=the-code&state=${STATE}`);
    await settle();

    expect(TestBed.inject(AuthSession).account()).toEqual({
      tenantId: TENANT,
      subjectId: 'u-1',
      email: 'rene@example.com',
      name: 'Rene'
    });
  });

  it('anErrorParameterRendersInlineAndOffersRetry', async () => {
    endpoint = new FakeTokenEndpoint({ status: 200, body: {} });

    await router.navigateByUrl(
      `/auth/callback?error=access_denied&error_description=${encodeURIComponent('The person declined.')}`
    );
    await settle();

    const host = fixture.nativeElement as HTMLElement;
    expect(router.url).toMatch(/^\/auth\/callback/);
    expect(host.textContent).toContain('Sign-in could not be completed');
    expect(host.textContent).toContain('The person declined.');
    expect(endpoint.calls).toEqual([]);

    // The failed state is a page a person reads, so it passes the same gate as every route.
    const results = await axe.run(host, {
      runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'] },
      rules: { 'color-contrast': { enabled: false } }
    });
    expect(results.violations.map(v => v.id)).toEqual([]);

    const retry = [...host.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Try again');
    expect(retry).toBeDefined();
    retry!.click();
    await settle();

    // A fresh /authorize toward where the person was going — the consumed ticket said where.
    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toMatch(/^http:\/\/localhost:5101\/authorize\?/);
    expect(cookies.read(PKCE_COOKIE)?.split('.').slice(2).join('.')).toBe('/subscriptions/s-1');
  });

  it('refuses a code whose state is not the cookie’s, inline', async () => {
    endpoint = new FakeTokenEndpoint({ status: 200, body: {} });

    await router.navigateByUrl('/auth/callback?code=the-code&state=someone-elses');
    await settle();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The sign-in could not be completed');
    expect(endpoint.calls).toEqual([]);
    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(false);
  });
});
