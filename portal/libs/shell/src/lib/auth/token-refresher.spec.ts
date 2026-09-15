import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FakeTokenEndpoint, MemoryCookieJar, unsignedJwt } from '@cybercloud/shell/testing';
import { AccessTokenStore } from './access-token-store';
import { AUTH_NAVIGATE } from './auth-flow';
import { AuthSession } from './auth-session';
import { CookieJar } from './cookies';
import { IDENTITY_ISSUER } from './identity-issuer';
import { TENANT_CONTEXT_SOURCE } from './tenant-context-source';
import { REFRESH_LEAD_MS, TokenRefresher } from './token-refresher';

const ISSUER = 'http://localhost:5101';
const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';

const token = (marker: string): string => unsignedJwt({ sub: 'u-1', tid: TENANT, marker });
const answer = (marker: string) => ({
  status: 200,
  body: { access_token: token(marker), token_type: 'Bearer', expires_in: 600 }
});

/**
 * docs/plan/10 § Authentication inputs: "Tokens are short (10 minutes) and scoped to a tenant …
 * a 10-minute token with a refresh flow costs the SDK one line." The refresh token is the
 * identity host's cookie, so what the portal sends is the grant and the client id and nothing
 * else — and what it does with a refusal is give up the chain, because
 * `ISessionGrain.RefreshAsync` has already revoked it (docs/plan/11 § Sessions and revocation).
 */
describe('TokenRefresher', () => {
  let navigated: string[];
  let endpoint: FakeTokenEndpoint;
  let refresher: TokenRefresher;
  let session: AuthSession;
  let tokens: AccessTokenStore;

  beforeEach(() => {
    jest.useFakeTimers();
    navigated = [];
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: IDENTITY_ISSUER, useValue: ISSUER },
        { provide: CookieJar, useClass: MemoryCookieJar },
        { provide: AUTH_NAVIGATE, useValue: (url: string) => navigated.push(url) },
        { provide: TENANT_CONTEXT_SOURCE, useValue: { load: async () => ({ displayName: 'T', subscriptions: [] }) } }
      ]
    });
    refresher = TestBed.inject(TokenRefresher);
    session = TestBed.inject(AuthSession);
    tokens = TestBed.inject(AccessTokenStore);
    endpoint = new FakeTokenEndpoint(answer('first'), answer('second'));
  });

  afterEach(() => {
    endpoint.restore();
    jest.useRealTimers();
  });

  /** Lets the fake `/token` answer and the refresher's `await`s run, under fake timers. */
  const settle = async (): Promise<void> => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  };

  it('refreshesSixtySecondsBeforeExpiry', async () => {
    const now = Date.now();
    session.accept({ access_token: token('initial'), token_type: 'Bearer', expires_in: 600 }, now);
    TestBed.tick();

    expect(refresher.scheduledAt()).toBe(now + 600_000 - REFRESH_LEAD_MS);
    expect(endpoint.calls).toHaveLength(0);

    jest.advanceTimersByTime(600_000 - REFRESH_LEAD_MS - 1);
    expect(endpoint.calls).toHaveLength(0);

    jest.advanceTimersByTime(1);
    await settle();

    expect(endpoint.calls).toHaveLength(1);
    expect(tokens.current()).toBe(token('first'));

    // And the new token is armed in turn, from its own expiry.
    TestBed.tick();
    expect(refresher.scheduledAt()).toBe(Date.now() + 600_000 - REFRESH_LEAD_MS);
  });

  it('theRefreshRequestSendsNoRefreshTokenField', async () => {
    await refresher.refresh();

    expect(endpoint.calls).toHaveLength(1);
    const [call] = endpoint.calls;
    expect(call.url).toBe(`${ISSUER}/token`);
    expect(call.init.credentials).toBe('include');
    expect(call.init.headers).toEqual({ 'Content-Type': 'application/x-www-form-urlencoded' });
    expect(Object.fromEntries(call.form)).toEqual({ grant_type: 'refresh_token', client_id: 'cyc-portal' });
    expect(call.form.has('refresh_token')).toBe(false);
  });

  it('concurrentCallsShareOneRefresh', async () => {
    endpoint.hold();

    const a = refresher.refresh();
    const b = refresher.refresh();
    const c = refresher.refresh();
    expect(endpoint.calls).toHaveLength(1);

    endpoint.release();
    await expect(Promise.all([a, b, c])).resolves.toEqual([true, true, true]);
    expect(endpoint.calls).toHaveLength(1);
    expect(tokens.current()).toBe(token('first'));

    // A call after the first has settled is a new refresh, not the old promise.
    await refresher.refresh();
    expect(endpoint.calls).toHaveLength(2);
    expect(tokens.current()).toBe(token('second'));
  });

  it('aFailedRefreshClearsTheStoreAndStartsSignIn', async () => {
    endpoint.restore();
    endpoint = new FakeTokenEndpoint({
      status: 400,
      body: { error: 'invalid_grant', error_description: 'sign in again' }
    });
    session.accept({ access_token: token('stale'), token_type: 'Bearer', expires_in: 600 });
    expect(tokens.hasToken()).toBe(true);

    await expect(refresher.refresh({ returnTo: '/subscriptions' })).resolves.toBe(false);

    expect(tokens.hasToken()).toBe(false);
    expect(session.account()).toBeNull();
    expect(navigated).toHaveLength(1);
    const url = new URL(navigated[0]);
    expect(`${url.origin}${url.pathname}`).toBe(`${ISSUER}/authorize`);
    // The sign-in resumes where the refresh was asked for, through the PKCE cookie's return path.
    const pkce = (TestBed.inject(CookieJar) as MemoryCookieJar).read('cyc-pkce');
    expect(pkce?.split('.').slice(2).join('.')).toBe('/subscriptions');
  });

  it('treats an unreachable host as a failed refresh rather than a hang', async () => {
    endpoint.restore();
    endpoint = new FakeTokenEndpoint('unreachable');

    await expect(refresher.refresh()).resolves.toBe(false);
    expect(navigated).toHaveLength(1);
  });
});
