import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { AccessTokenStore, TokenRefresher } from '@cybercloud/shell';
import { from, switchMap, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

/**
 * Attaches the access token to API calls, and renews it once when the platform says it is stale.
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
 *    Only same-origin and explicitly-relative URLs get it. `/token` and `/logout` on the identity
 *    host are raw `fetch` calls in `libs/shell` and never pass through here at all.
 * 3. **On the server it attaches nothing**, because the store is empty there. docs/plan/20 § SSR:
 *    "The SSR process holds no tokens; it renders the shell and the client hydrates with the user's
 *    token." That is not a branch in this file — it falls out of the store being per-request and
 *    never populated server-side, which is a much harder property to break by accident than an
 *    `if (isPlatformServer)`.
 *
 * **On a 401 with a token attached: one refresh, one retry.** The timer in `TokenRefresher` fires
 * at `exp − 60 s`, so a 401 here is the clock drifting or the session being revoked. One refresh
 * tells the two apart — a rotated token retries the call once and the person notices nothing; a
 * refused refresh clears the store and begins a sign-in, and the 401 is rethrown so the page can
 * show its failed state while the browser leaves. ⚠ The retry is not wrapped again: a 401 on the
 * retried call is the platform's answer, not a reason to loop. `TokenRefresher.refresh` is
 * single-flight, so the several calls a page opens with rotate the chain once between them.
 */
export const accessTokenInterceptor: HttpInterceptorFn = (req, next) => {
  const tokens = inject(AccessTokenStore);
  const refresher = inject(TokenRefresher);
  const token = tokens.current();

  if (token === null || !isSameOrigin(req.url)) return next(req);

  return next(withBearer(req, token)).pipe(
    catchError((failure: unknown) => {
      if (!(failure instanceof HttpErrorResponse) || failure.status !== 401) return throwError(() => failure);

      return from(refresher.refresh()).pipe(
        switchMap(refreshed => {
          const renewed = tokens.current();
          return refreshed && renewed !== null ? next(withBearer(req, renewed)) : throwError(() => failure);
        })
      );
    })
  );
};

function withBearer<T>(req: HttpRequest<T>, token: string): HttpRequest<T> {
  return req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}

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
