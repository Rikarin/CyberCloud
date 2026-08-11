import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AccessTokenStore } from '@cybercloud/shell';

/**
 * Attaches the access token to API calls.
 *
 * docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token
 * in memory, refresh in an `HttpOnly` cookie scoped to the identity host".
 *
 * ⚠ Three things this deliberately does not do.
 *
 * 1. **It does not read a token from storage**, because there is none to read — `AccessTokenStore`
 *    holds it in a signal and the lint config bans web storage outright.
 * 2. **It does not attach the token to cross-origin requests.** A bearer token is a credential; a
 *    header attached by URL pattern rather than by origin is how one ends up on a third-party CDN.
 *    Only same-origin and explicitly-relative URLs get it.
 * 3. **On the server it attaches nothing**, because the store is empty there. docs/plan/20 § SSR:
 *    "The SSR process holds no tokens; it renders the shell and the client hydrates with the user's
 *    token." That is not a branch in this file — it falls out of the store being per-request and
 *    never populated server-side, which is a much harder property to break by accident than an
 *    `if (isPlatformServer)`.
 */
export const accessTokenInterceptor: HttpInterceptorFn = (req, next) => {
  const tokens = inject(AccessTokenStore);
  const token = tokens.current();

  if (token === null || !isSameOrigin(req.url)) return next(req);

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};

/**
 * A relative URL is same-origin by construction. An absolute one is only same-origin if it
 * actually is — and on the server, where there is no `location`, an absolute URL is treated as
 * foreign, which is the safe direction to be wrong in.
 */
function isSameOrigin(url: string): boolean {
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(url)) return true;
  if (typeof location === 'undefined') return false;

  try {
    return new URL(url).origin === location.origin;
  } catch {
    return false;
  }
}
