import { DOCUMENT } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FakeTokenEndpoint, MemoryCookieJar, unsignedJwt } from '@cybercloud/shell/testing';
import { TenantContextStore } from '../context/tenant-context';
import { AccessTokenStore } from './access-token-store';
import { AUTH_NAVIGATE, AuthFlow, PKCE_COOKIE } from './auth-flow';
import { AuthSession, TENANT_COOKIE } from './auth-session';
import { CookieJar } from './cookies';
import { IDENTITY_ISSUER } from './identity-issuer';
import { codeChallenge } from './pkce';
import { TENANT_CONTEXT_SOURCE, TenantContextSource } from './tenant-context-source';

const ISSUER = 'http://localhost:5101';
const TENANT = '7f3a1c2e4b7d4e3a9c1d2b6f8a7e5d43';
/** The same tenant as an address spells it — what `AuthSession` answers from the claim above. */
const TENANT_ADDRESS = '7f3a1c2e-4b7d-4e3a-9c1d-2b6f8a7e5d43';

/**
 * docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token
 * in memory, refresh in an `HttpOnly` cookie scoped to the identity host … Access token never in
 * `localStorage`." Each test is one sentence of the contract's § 7 the flow has to keep.
 */
describe('AuthFlow — Authorization Code + PKCE from the portal', () => {
  let navigated: string[];
  let cookies: MemoryCookieJar;
  let flow: AuthFlow;
  let endpoint: FakeTokenEndpoint | null;
  const source: TenantContextSource = {
    load: async tenantId => ({
      displayName: 'Contoso',
      subscriptions: [{ id: 's-1', tenantId, displayName: 'Default' }]
    })
  };

  beforeEach(() => {
    navigated = [];
    endpoint = null;
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: IDENTITY_ISSUER, useValue: ISSUER },
        { provide: CookieJar, useClass: MemoryCookieJar },
        { provide: AUTH_NAVIGATE, useValue: (url: string) => navigated.push(url) },
        { provide: TENANT_CONTEXT_SOURCE, useValue: source }
      ]
    });
    cookies = TestBed.inject(CookieJar) as MemoryCookieJar;
    flow = TestBed.inject(AuthFlow);
  });

  afterEach(() => endpoint?.restore());

  const authorizeUrl = (): URL => {
    expect(navigated).toHaveLength(1);
    return new URL(navigated[0]);
  };

  it('theAuthorizeUrlCarriesEveryRequiredParameter', async () => {
    await flow.beginSignIn('/subscriptions');

    const url = authorizeUrl();
    const q = url.searchParams;

    expect(`${url.origin}${url.pathname}`).toBe(`${ISSUER}/authorize`);
    expect(q.get('response_type')).toBe('code');
    expect(q.get('client_id')).toBe('cyc-portal');
    expect(q.get('redirect_uri')).toBe('http://localhost/auth/callback');
    expect(q.get('scope')).toBe('openid profile offline_access cyc.api');
    expect(q.get('code_challenge_method')).toBe('S256');
    expect(q.get('state')).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(q.get('nonce')).toMatch(/^[A-Za-z0-9_-]{43}$/);

    // The challenge is the hash of the verifier the cookie holds — the binding the host checks.
    const [state, verifier, returnTo] = cookies.read(PKCE_COOKIE)!.split('.');
    expect(q.get('state')).toBe(state);
    expect(q.get('code_challenge')).toBe(await codeChallenge(verifier));
    expect(returnTo).toBe('/subscriptions');
  });

  it('theTenantHintIsSentOnlyWhenRemembered', async () => {
    await flow.beginSignIn('/');
    expect(authorizeUrl().searchParams.has('tenant')).toBe(false);

    navigated = [];
    cookies.write(TENANT_COOKIE, TENANT, { path: '/', maxAgeSeconds: 1 });
    await flow.beginSignIn('/');
    expect(authorizeUrl().searchParams.get('tenant')).toBe(TENANT);
  });

  it('theVerifierCookieIsPathScopedToTheCallback', async () => {
    await flow.beginSignIn('/');

    // Sent only to the callback route, gone in ten minutes: a script on any other path of this
    // origin never sees the verifier, and a tab left open does not keep a live one.
    expect(cookies.attributesOf(PKCE_COOKIE)).toEqual({ path: '/auth/callback', maxAgeSeconds: 600 });
  });

  it('aStateMismatchRefusesTheCode', async () => {
    endpoint = new FakeTokenEndpoint({ status: 200, body: {} });
    await flow.beginSignIn('/');

    const outcome = await flow.completeCallback(new URLSearchParams({ code: 'the-code', state: 'not-ours' }));

    expect(outcome.kind).toBe('failed');
    // The code was never sent anywhere — a mismatched state is a login-CSRF or a stale tab, and
    // the exchange is where the damage would be done.
    expect(endpoint.calls).toEqual([]);
    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(false);
    // And the ticket is spent: a second try with the right state finds nothing to match.
    expect(cookies.read(PKCE_COOKIE)).toBeNull();
  });

  it('aSuccessfulExchangeSetsTheStoreTheTenantCookieAndClearsPkce', async () => {
    const accessToken = unsignedJwt({ sub: 'u-1', tid: TENANT, aud: 'cyc.api', exp: 1 });
    const idToken = unsignedJwt({ sub: 'u-1', tid: TENANT, email: 'rene@example.com', name: 'Rene' });
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: { access_token: accessToken, token_type: 'Bearer', expires_in: 600, id_token: idToken, scope: 'cyc.api' }
    });

    await flow.beginSignIn('/subscriptions/s-1');
    const [state, verifier] = cookies.read(PKCE_COOKIE)!.split('.');

    const before = Date.now();
    const outcome = await flow.completeCallback(new URLSearchParams({ code: 'the-code', state }));

    expect(outcome).toEqual({
      kind: 'signedIn',
      returnTo: '/subscriptions/s-1',
      account: { tenantId: TENANT_ADDRESS, subjectId: 'u-1', email: 'rene@example.com', name: 'Rene' }
    });

    // The exchange: a form POST with credentials, the verifier, and the redirect URI the code
    // was issued for. ⚠ No `refresh_token` ever appears on this side; the host keeps it in a cookie.
    expect(endpoint.calls).toHaveLength(1);
    const [call] = endpoint.calls;
    expect(call.url).toBe(`${ISSUER}/token`);
    expect(call.init.method).toBe('POST');
    expect(call.init.credentials).toBe('include');
    expect(call.init.headers).toEqual({ 'Content-Type': 'application/x-www-form-urlencoded' });
    expect(Object.fromEntries(call.form)).toEqual({
      grant_type: 'authorization_code',
      client_id: 'cyc-portal',
      redirect_uri: 'http://localhost/auth/callback',
      code: 'the-code',
      code_verifier: verifier
    });

    const tokens = TestBed.inject(AccessTokenStore);
    expect(tokens.current()).toBe(accessToken);
    expect(tokens.isExpired(before + 600_000 - 31_000)).toBe(false);
    expect(tokens.isExpired(before + 600_000 - 29_000)).toBe(true);

    expect(cookies.read(TENANT_COOKIE)).toBe(TENANT_ADDRESS);
    expect(cookies.attributesOf(TENANT_COOKIE)).toEqual({ path: '/', maxAgeSeconds: 34_560_000 });
    expect(cookies.read(PKCE_COOKIE)).toBeNull();

    // The tenant context came from the app's source, keyed by the token's `tid`.
    const context = TestBed.inject(TenantContextStore);
    expect(context.activeTenant()).toEqual({ id: TENANT_ADDRESS, displayName: 'Contoso' });
    expect(context.activeSubscription()?.displayName).toBe('Default');
    expect(TestBed.inject(AuthSession).account()?.email).toBe('rene@example.com');
  });

  it('nothingTouchesWebStorage', async () => {
    // The lint rule bans the identifiers; this is the behavioural half, as in
    // `access-token-store.spec.ts`: the whole flow, sign-in to exchange, writes to neither storage.
    const setItem = jest.spyOn(Storage.prototype, 'setItem');
    const getItem = jest.spyOn(Storage.prototype, 'getItem');
    const accessToken = unsignedJwt({ sub: 'u-1', tid: TENANT });
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: { access_token: accessToken, token_type: 'Bearer', expires_in: 600 }
    });

    await flow.beginSignIn('/');
    const [state] = cookies.read(PKCE_COOKIE)!.split('.');
    await flow.completeCallback(new URLSearchParams({ code: 'c', state }));
    flow.signOut();

    expect(setItem).not.toHaveBeenCalled();
    expect(getItem).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    jest.restoreAllMocks();
  });

  it('signs out by forgetting the token and leaving for /logout, keeping the tenant hint', async () => {
    const accessToken = unsignedJwt({ sub: 'u-1', tid: TENANT });
    endpoint = new FakeTokenEndpoint({
      status: 200,
      body: { access_token: accessToken, token_type: 'Bearer', expires_in: 600 }
    });
    await flow.beginSignIn('/');
    const [state] = cookies.read(PKCE_COOKIE)!.split('.');
    await flow.completeCallback(new URLSearchParams({ code: 'c', state }));
    navigated = [];

    flow.signOut();

    expect(TestBed.inject(AccessTokenStore).hasToken()).toBe(false);
    expect(TestBed.inject(AuthSession).account()).toBeNull();
    expect(cookies.read(TENANT_COOKIE)).toBe(TENANT_ADDRESS);
    expect(navigated).toEqual([
      `${ISSUER}/logout?client_id=cyc-portal&post_logout_redirect_uri=${encodeURIComponent('http://localhost/')}`
    ]);
  });

  it('answers an error parameter with its description and never calls /token', async () => {
    endpoint = new FakeTokenEndpoint({ status: 200, body: {} });
    await flow.beginSignIn('/');

    const outcome = await flow.completeCallback(
      new URLSearchParams({ error: 'access_denied', error_description: 'The person declined.' })
    );

    expect(outcome).toEqual({ kind: 'failed', message: 'The person declined.', returnTo: '/' });
    expect(endpoint.calls).toEqual([]);
  });

  it('sends the return path through the cookie, and refuses one that leaves the origin', async () => {
    await flow.beginSignIn('//evil.example/phish');
    expect(cookies.read(PKCE_COOKIE)!.split('.').slice(2).join('.')).toBe('/');

    await flow.beginSignIn('/auth/callback?code=stale');
    expect(cookies.read(PKCE_COOKIE)!.split('.').slice(2).join('.')).toBe('/');
  });
});

/**
 * The default `AUTH_NAVIGATE` — the one the app runs, which every test above replaces. It is
 * called after an `await` (the guard's failed refresh, the callback's exchange), where there is
 * no injection context, so everything it needs from the injector has to be resolved when the
 * token is created and nothing when it is called. The first dev run found the other shape:
 * NG0203 in the console, and the portal's empty shell instead of the identity host.
 */
describe('AUTH_NAVIGATE — the default navigator', () => {
  it('resolvesItsDocumentAtCreationSoACallAfterAnAwaitHasNothingToInject', async () => {
    // jsdom's `location` cannot be spied on, so the document is a double with one method on it.
    const assign = jest.fn();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: DOCUMENT, useValue: { defaultView: { location: { assign } } } }
      ]
    });
    const navigate = TestBed.inject(AUTH_NAVIGATE);

    await Promise.resolve();

    expect(() => navigate('http://localhost:5101/authorize')).not.toThrow();
    expect(assign).toHaveBeenCalledWith('http://localhost:5101/authorize');
  });
});
