import { fromBase64Url } from './pkce';

/**
 * A JWT's payload, read without checking its signature.
 *
 * ⚠ **Display and hints only — never authority.** The portal derives nothing it enforces from
 * these claims. `tid` picks which tenant to load and which hint to remember, `sub` labels the
 * session, `email` and `name` fill the account menu; every one of those is a convenience the
 * gateway does not rely on. The gateway verifies the signature against
 * `/.well-known/jwks` on every request (`JwksCallerContextResolver`), so a token this function
 * decoded wrongly, or one somebody edited, buys nothing beyond a mislabeled menu and a 401.
 *
 * Verifying here would mean fetching the JWKS into the browser and holding a second copy of the
 * gateway's validation, which is a second place for it to be wrong.
 */
export function decodeJwtPayload(token: string): Readonly<Record<string, unknown>> | null {
  const segments = token.split('.');
  if (segments.length !== 3) return null;

  try {
    const parsed: unknown = JSON.parse(new TextDecoder().decode(fromBase64Url(segments[1])));
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/** A string claim, or `null` when the payload lacks it or it is not a string. */
export function stringClaim(payload: Readonly<Record<string, unknown>> | null, name: string): string | null {
  const value = payload?.[name];
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/**
 * A GUID claim as an address spells it: the `D` form, `8-4-4-4-12` lower-case hex.
 *
 * ⚠ **The token and the address disagree about the form, and this is where they meet.** The
 * identity host writes `tid` and `sub` in the `N` form (32 hex digits, `AccessTokenClaims`), and
 * the gateway's scope grammar reads `/tenants/{t}/…` in the `D` form — docs/plan/06
 * § Identifiers. The first dev run sent `GET /api/tenants/76fe0b2b…` straight from the claim and
 * the gateway answered 400 `InvalidResourceId` with every host healthy. A claim that is not 32
 * hex digits is returned as it came, so a value that is already an address, or not a GUID at
 * all, is not mangled.
 */
export function guidClaimAsAddress(value: string): string {
  if (!/^[0-9a-fA-F]{32}$/.test(value)) return value;
  const n = value.toLowerCase();
  return `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
}
