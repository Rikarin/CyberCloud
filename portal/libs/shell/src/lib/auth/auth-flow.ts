import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Injectable, InjectionToken, PLATFORM_ID, inject } from '@angular/core';
import { TenantContextStore } from '../context/tenant-context';
import { AuthSession, SignedInAccount } from './auth-session';
import { CookieJar } from './cookies';
import { IDENTITY_ISSUER } from './identity-issuer';
import { newPkcePair } from './pkce';
import { TENANT_CONTEXT_SOURCE } from './tenant-context-source';
import { CALLBACK_PATH, PORTAL_CLIENT_ID, PORTAL_SCOPES, postTokenRequest } from './token-endpoint';

/** The cookie the sign-in in flight lives in: `{state}.{verifier}.{returnTo}`, sent only to the callback route. */
export const PKCE_COOKIE = 'cyc-pkce';

/** Ten minutes — longer than a sign-up takes, shorter than a tab left open overnight. */
export const PKCE_COOKIE_MAX_AGE_SECONDS = 600;

/**
 * How the shell leaves for the identity host.
 *
 * ⚠ **A full-page navigation, never the router.** `/authorize` and `/logout` are on another origin
 * and answer redirects the browser has to follow with its cookies. A token rather than a direct
 * `location.assign` for the same reason the identity app has `NAVIGATE`: jsdom's `location` is
 * not configurable, so a test that wants to see where the page went has nothing else to hook.
 */
export const AUTH_NAVIGATE = new InjectionToken<(url: string) => void>('cc.authNavigate', {
  providedIn: 'root',
  factory: () => {
    // ⚠ Resolved here, at factory time, and not inside the closure. The closure runs after an
    // `await` — the guard's failed refresh, the callback's exchange — where there is no injection
    // context, and an `inject()` there throws NG0203 in the browser while every unit test, which
    // provides its own navigator for this token, stays green. The first dev run landed on the
    // portal's empty shell with that error in the console and no redirect.
    const view = inject(DOCUMENT).defaultView;
    return (url: string) => {
      view?.location.assign(url);
    };
  }
});

/** What the callback page does with the answer. */
export type CallbackOutcome =
  | { readonly kind: 'signedIn'; readonly account: SignedInAccount; readonly returnTo: string }
  | { readonly kind: 'failed'; readonly message: string; readonly returnTo: string };

/**
 * Authorization Code + PKCE against the identity host, from the portal's side.
 *
 * docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token
 * in memory, refresh in an `HttpOnly` cookie scoped to the identity host". The three legs:
 *
 * 1. {@link beginSignIn} mints a PKCE pair, keeps it in the `cyc-pkce` cookie, and leaves for
 *    `{issuer}/authorize`. The identity host sends a person with no session on to the identity
 *    app's pages and comes back to `/auth/callback` with a code.
 * 2. {@link completeCallback} checks `state` against the cookie, trades the code and the verifier
 *    for tokens at `{issuer}/token`, and hands the response to `AuthSession`. The refresh token
 *    never appears in that response for this client — the host moves it into `__Host-cyc-refresh`
 *    on its own origin, which is why this class holds no refresh token and `AccessTokenStore`'s
 *    remarks say it cannot read one.
 * 3. {@link ensureContext} turns the token's `tid` into a tenant and its subscriptions through
 *    the app's `TENANT_CONTEXT_SOURCE`, so the context bar reads a name rather than a GUID.
 *
 * ⚠ **The tenant hint.** docs/plan/11 § Sign-up and tenant creation refuses a global email index,
 * so an address alone resolves nothing on the host and the request has to name the tenant. The
 * last signed-in `tid` is remembered in `cyc-tenant` and sent as `tenant=` on the next
 * `/authorize`; a fresh browser sends none and the identity app asks for the organization. The
 * hint is a convenience — the host resolves it through the tenant directory and an unknown value
 * is an ordinary sign-in failure there.
 */
@Injectable({ providedIn: 'root' })
export class AuthFlow {
  private readonly issuer = inject(IDENTITY_ISSUER);
  private readonly cookies = inject(CookieJar);
  private readonly session = inject(AuthSession);
  private readonly context = inject(TenantContextStore);
  private readonly contextSource = inject(TENANT_CONTEXT_SOURCE, { optional: true });
  private readonly navigate = inject(AUTH_NAVIGATE);
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  /**
   * Leaves for the identity host's `/authorize`. `returnTo` is where the callback lands afterwards
   * — a same-origin path; anything else becomes `/`.
   */
  async beginSignIn(returnTo: string): Promise<void> {
    if (!this.browser) return;

    const pkce = await newPkcePair();
    const destination = sameOriginPath(returnTo);

    this.cookies.write(PKCE_COOKIE, `${pkce.state}.${pkce.verifier}.${destination}`, {
      path: CALLBACK_PATH,
      maxAgeSeconds: PKCE_COOKIE_MAX_AGE_SECONDS
    });

    const query = new URLSearchParams({
      response_type: 'code',
      client_id: PORTAL_CLIENT_ID,
      redirect_uri: this.redirectUri(),
      scope: PORTAL_SCOPES,
      state: pkce.state,
      code_challenge: pkce.challenge,
      code_challenge_method: 'S256',
      nonce: pkce.nonce
    });

    const tenant = this.session.rememberedTenant();
    if (tenant !== null) query.set('tenant', tenant);

    this.navigate(`${this.issuer}/authorize?${query.toString()}`);
  }

  /**
   * Finishes a sign-in from the callback route's query. Every failure is an outcome rather than a
   * throw, because the page renders it inline and offers a retry — docs/plan/20's "nothing throws
   * to the console" for a person who cannot open the console.
   */
  async completeCallback(query: URLSearchParams): Promise<CallbackOutcome> {
    const ticket = this.takeTicket();
    const returnTo = ticket?.returnTo ?? '/';

    const failed = (message: string): CallbackOutcome => ({ kind: 'failed', message, returnTo });

    if (!this.browser) return failed($localize`:@@auth.callback.serverSide:Sign-in completes in the browser.`);

    const error = query.get('error');
    if (error !== null) {
      const description = query.get('error_description');
      return failed(
        description !== null && description.length > 0
          ? description
          : $localize`:@@auth.callback.refused:The identity host refused the sign-in (${error}:error:).`
      );
    }

    const code = query.get('code');
    const state = query.get('state');

    // ⚠ Refused before the code is touched. A code arriving with the wrong state is either a
    // login-CSRF or a stale tab; either way the exchange must not happen, and the message must not
    // say which (RFC 6749 § 10.12).
    if (ticket === null || code === null || state === null || state !== ticket.state) {
      return failed($localize`:@@auth.callback.mismatch:The sign-in could not be completed. Try again.`);
    }

    const result = await postTokenRequest(this.issuer, {
      grant_type: 'authorization_code',
      client_id: PORTAL_CLIENT_ID,
      redirect_uri: this.redirectUri(),
      code,
      code_verifier: ticket.verifier
    });

    if (!result.ok) return failed(result.description);

    const account = this.session.accept(result.response);
    if (account === null) {
      return failed($localize`:@@auth.callback.noTenant:The token names no tenant. Sign in again.`);
    }

    await this.ensureContext();

    return { kind: 'signedIn', account, returnTo };
  }

  /**
   * Loads the tenant and subscriptions the token names into `TenantContextStore`, once. A second
   * call with the context resolved is a no-op, so the guard can call it on every navigation.
   */
  async ensureContext(): Promise<void> {
    const account = this.session.account();
    if (account === null || this.context.resolved()) return;

    if (this.contextSource === null) {
      throw new Error(
        'TENANT_CONTEXT_SOURCE is not provided. The app supplies it in app.config.ts over the scope ' +
          'collections — the shell cannot read GET /api/tenants/{tid} on its own.'
      );
    }

    const snapshot = await this.contextSource.load(account.tenantId);
    this.context.loadFromToken(account.tenantId, snapshot.displayName, snapshot.subscriptions);
  }

  /**
   * Signs out: forgets the token, then leaves for the host's `/logout`, which clears both `__Host-`
   * cookies, revokes the interactive session, and lands back on `/` — where the guard starts a
   * sign-in that carries the remembered tenant.
   */
  signOut(): void {
    this.session.clear();
    if (!this.browser) return;

    const query = new URLSearchParams({
      client_id: PORTAL_CLIENT_ID,
      post_logout_redirect_uri: `${this.origin()}/`
    });

    this.navigate(`${this.issuer}/logout?${query.toString()}`);
  }

  private redirectUri(): string {
    return `${this.origin()}${CALLBACK_PATH}`;
  }

  private origin(): string {
    return this.document.defaultView?.location.origin ?? '';
  }

  /** Reads and deletes the PKCE cookie. One sign-in, one exchange. */
  private takeTicket(): { state: string; verifier: string; returnTo: string } | null {
    const raw = this.cookies.read(PKCE_COOKIE);
    if (raw === null) return null;

    this.cookies.remove(PKCE_COOKIE, CALLBACK_PATH);

    // The state and the verifier are base64url and carry no `.`; the return path may.
    const first = raw.indexOf('.');
    const second = first < 0 ? -1 : raw.indexOf('.', first + 1);
    if (first <= 0 || second < 0) return null;

    return {
      state: raw.slice(0, first),
      verifier: raw.slice(first + 1, second),
      returnTo: sameOriginPath(raw.slice(second + 1))
    };
  }
}

/**
 * A path on this origin, or `/`. `//evil.example` is a scheme-relative URL and `http:` is
 * absolute; both would turn the return path into an open redirect.
 */
export function sameOriginPath(candidate: string): string {
  if (!candidate.startsWith('/') || candidate.startsWith('//') || candidate.startsWith('/\\')) return '/';
  if (candidate.startsWith(CALLBACK_PATH)) return '/';
  return candidate;
}
