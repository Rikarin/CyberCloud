import { AccessTokenStore } from './access-token-store';

/**
 * docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token
 * in memory, refresh in an `HttpOnly` cookie scoped to the identity host … Access token never in
 * `localStorage`."
 *
 * The lint config bans the `localStorage`/`sessionStorage` identifiers in portal source. This is
 * the behavioural half: it spies on both storages and asserts the store's whole lifecycle touches
 * neither. A lint rule catches the name; this catches a write that arrives through an alias, a
 * helper or a dependency.
 */
describe('AccessTokenStore — docs/plan/10 § Authentication inputs', () => {
  let setItem: jest.SpyInstance;
  let sessionSetItem: jest.SpyInstance;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setItem = jest.spyOn(Storage.prototype, 'setItem');
    sessionSetItem = jest.spyOn(window.sessionStorage, 'setItem');
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('never writes the token to web storage', () => {
    const store = new AccessTokenStore();

    store.set('a-very-secret-access-token', Date.now() + 3_600_000);

    expect(setItem).not.toHaveBeenCalled();
    expect(sessionSetItem).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);

    // Belt and braces: nothing anywhere in either storage resembles the token.
    expect(JSON.stringify({ ...localStorage })).not.toContain('a-very-secret-access-token');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('a-very-secret-access-token');
  });

  it('holds the token only in memory, and gives it up on clear', () => {
    const store = new AccessTokenStore();

    expect(store.hasToken()).toBe(false);
    expect(store.current()).toBeNull();

    store.set('t', Date.now() + 3_600_000);
    expect(store.hasToken()).toBe(true);
    expect(store.current()).toBe('t');

    store.clear();
    expect(store.hasToken()).toBe(false);
    expect(store.current()).toBeNull();
  });

  it('treats a token inside the clock-skew window as already expired', () => {
    const store = new AccessTokenStore();
    const now = 1_000_000;

    store.set('t', now + 3_600_000);
    expect(store.isExpired(now)).toBe(false);

    // 10 seconds left, 30 seconds of skew: expired, because a token that dies mid-flight surfaces
    // as a random 401 rather than as a renewal.
    store.set('t', now + 10_000);
    expect(store.isExpired(now)).toBe(true);
  });

  it('reports an unset token as expired rather than as valid', () => {
    // Failing open here would mean an unauthenticated client sending no Authorization header and
    // being told it is fine.
    expect(new AccessTokenStore().isExpired()).toBe(true);
  });
});
