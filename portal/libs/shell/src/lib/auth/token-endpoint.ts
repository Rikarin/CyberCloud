import { TokenResponse } from './auth-session';

/** The portal's `client_id` — `FirstPartyClients` on the identity host holds the registration. */
export const PORTAL_CLIENT_ID = 'cyc-portal';

/** The scopes the portal asks for; `cyc.api` is the gateway's audience, `AccessTokenPolicy.Audience`. */
export const PORTAL_SCOPES = 'openid profile offline_access cyc.api';

/** The callback route, relative to the portal origin. Registered on the host as the portal's redirect URI. */
export const CALLBACK_PATH = '/auth/callback';

/** What one `POST /token` came back with. */
export type TokenResult =
  | { readonly ok: true; readonly response: TokenResponse }
  | { readonly ok: false; readonly status: number; readonly error: string; readonly description: string };

/**
 * One call to the identity host's `/token`.
 *
 * ⚠ **A raw `fetch`, cross-origin, with credentials — never `HttpClient` and never proxied.** Three
 * reasons, each of which the alternative breaks:
 *
 * - `accessTokenInterceptor` attaches the bearer token to same-origin calls, and `/token` must
 *   never carry one. Staying off `HttpClient` keeps it out of every interceptor's reach.
 * - The refresh token is an `HttpOnly` cookie on the identity host (docs/plan/10 § Authentication
 *   inputs), so the request has to reach that origin with `credentials: 'include'`; a same-origin
 *   proxy would never see the cookie. In production `portal.` and `id.` are different origins and
 *   there is no dev server to proxy through, and docs/plan/20 § SSR says the SSR process "holds no
 *   tokens" — so the dev run takes the production path and exercises the host's CORS policy
 *   rather than hiding it.
 * - `application/x-www-form-urlencoded` with only `Content-Type` set is a CORS *simple* request:
 *   no preflight, one round trip.
 *
 * A non-2xx answer is returned, not thrown, so a caller can tell `invalid_grant` (sign in again)
 * from an unreachable host (say so, keep the token) without a `try` around every call.
 */
export async function postTokenRequest(issuer: string, form: Readonly<Record<string, string>>): Promise<TokenResult> {
  let http: Response;

  try {
    http = await fetch(`${issuer}/token`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(form).toString()
    });
  } catch {
    return { ok: false, status: 0, error: 'unreachable', description: 'The identity host could not be reached.' };
  }

  let body: unknown = null;
  try {
    body = await http.json();
  } catch {
    // A body that is not JSON is answered by the status alone.
  }

  const record = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};

  if (http.ok && typeof record['access_token'] === 'string' && typeof record['expires_in'] === 'number') {
    return { ok: true, response: record as unknown as TokenResponse };
  }

  return {
    ok: false,
    status: http.status,
    error: typeof record['error'] === 'string' ? record['error'] : 'server_error',
    description:
      typeof record['error_description'] === 'string'
        ? record['error_description']
        : `The identity host answered ${http.status}.`
  };
}
