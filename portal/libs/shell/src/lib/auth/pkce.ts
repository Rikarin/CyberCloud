/**
 * RFC 7636 — Proof Key for Code Exchange — and the `state` parameter beside it.
 *
 * docs/plan/11 § Protocol: "Authorization Code + PKCE … The only interactive flow. No implicit,
 * no hybrid." A public client has no secret, so what binds the authorization code to the tab
 * that asked for it is the verifier: the identity host stores `SHA-256(verifier)` with the code
 * and refuses the exchange unless the verifier hashes to it. The code is also the reason
 * one-time use of the code can stay owed on the host for now — a stolen code is useless without
 * the verifier, and the verifier lives in a cookie only the callback route receives.
 *
 * Everything here is `crypto.subtle` and `crypto.getRandomValues`. ⚠ `Math.random` would make the
 * verifier guessable and the state predictable, and a predictable state is a login-CSRF: an
 * attacker completes their own sign-in in the victim's browser and the victim then acts in the
 * attacker's tenant.
 */

/** The bytes both the verifier and the state are drawn from: 32, which is 43 base64url characters. */
const RANDOM_BYTES = 32;

/**
 * RFC 4648 § 5 base64url, without padding — the alphabet RFC 7636 § 4.1 requires for the
 * verifier and § 4.2 for the challenge.
 */
export function base64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
}

/** The inverse of {@link base64Url}, tolerant of the padding a JWT never carries. */
export function fromBase64Url(text: string): Uint8Array {
  const padded = text.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - (text.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/** 32 random bytes, base64url: 43 characters, which is RFC 7636 § 4.1's minimum verifier length. */
export function randomToken(): string {
  const bytes = new Uint8Array(RANDOM_BYTES);
  crypto.getRandomValues(bytes);
  return base64Url(bytes);
}

/** RFC 7636 § 4.2, method `S256`: `base64url(SHA-256(ASCII(verifier)))`. */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64Url(new Uint8Array(digest));
}

/** What one authorization request carries, minted fresh for every request. */
export interface PkcePair {
  /** RFC 6749 § 10.12's CSRF binding: echoed by the callback and compared before the code is used. */
  readonly state: string;
  /** Kept on this origin, sent only with the code exchange. */
  readonly verifier: string;
  /** Sent with the authorization request. */
  readonly challenge: string;
  /** OIDC Core § 3.1.2.1 `nonce`, so a replayed id_token can be told from a fresh one. */
  readonly nonce: string;
}

export async function newPkcePair(): Promise<PkcePair> {
  const verifier = randomToken();
  return { state: randomToken(), verifier, challenge: await codeChallenge(verifier), nonce: randomToken() };
}
