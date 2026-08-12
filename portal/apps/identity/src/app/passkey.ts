/**
 * Why a passkey is not on offer right now, or `null` when it is.
 *
 * ⚠ Every value here is a *capability* answer, never an *account* answer. "This browser cannot do
 * WebAuthn" is safe to say; "this address has no passkey" is enumeration, and docs/plan/11
 * § Credentials requires sign-in to look identical whether or not the account exists.
 */
export type PasskeyUnavailableReason = 'server' | 'unsupported' | 'insecure-context';

/**
 * Reports why passkeys cannot be offered, or `null` when they can.
 *
 * ⚠ **Every branch here must be safe to evaluate during server-side rendering**, which is the
 * reason this is a plain function rather than a service that reads `navigator` in a field
 * initialiser. docs/plan/11 § Effort puts these pages on the identity host *with SSR*, so component
 * construction runs in Node — where `navigator` and `window` do not exist and a bare
 * `navigator.credentials` throws a `ReferenceError` that surfaces as a blank 500 page rather than
 * as a missing button.
 *
 * The order of the checks is deliberate: `server` first, because on the server the later checks
 * would themselves be the crash they are meant to prevent.
 */
export function passkeyUnavailableReason(): PasskeyUnavailableReason | null {
  // ── 1. Are we even in a browser? ───────────────────────────────────────────────────────────
  if (typeof window === 'undefined' || typeof navigator === 'undefined') {
    return 'server';
  }

  // ── 2. WebAuthn needs a secure context, and this bites in local development. ───────────────
  //
  // ⚠ `window.isSecureContext` is false over plain HTTP on anything that is not `localhost` or
  // `127.0.0.1`. A developer who serves the SSR bundle on `http://192.168.1.20:4001` to test on a
  // phone gets a silently missing passkey button and no error — so the page says which of these
  // three it is instead of just hiding the option.
  //
  // ⚠ The RP ID is the server's business, not this file's: it must equal the origin's registrable
  // domain or a parent of it, so a page served from `id.cybercloud.io` can use `cybercloud.io` but
  // never `cybercloud.io` from `id.example.net`. Getting it wrong fails inside the authenticator
  // with a `SecurityError` that names nothing useful.
  if (!window.isSecureContext) {
    return 'insecure-context';
  }

  // ── 3. Does this browser have the API at all? ─────────────────────────────────────────────
  if (!('credentials' in navigator) || typeof window.PublicKeyCredential === 'undefined') {
    return 'unsupported';
  }

  return null;
}

/**
 * Whether a passkey may be offered on this page load.
 *
 * Safe to call from a component field initialiser, including during SSR, where it returns `false`.
 */
export function canUsePasskey(): boolean {
  return passkeyUnavailableReason() === null;
}
