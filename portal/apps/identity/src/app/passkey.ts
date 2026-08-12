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

/**
 * Base64url without padding — the encoding WebAuthn uses on the wire.
 *
 * ⚠ Not `btoa` alone. Standard base64 emits `+`, `/` and `=`, and the server decodes base64url;
 * the three characters that differ are exactly the ones that appear in a minority of credential
 * ids, so getting this wrong produces a sign-in that works for most users and fails for some.
 */
function encode(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * The inverse, for the challenge and credential ids the options carry as base64url text.
 *
 * ⚠ Returns the `ArrayBuffer` rather than the view. `BufferSource` in the DOM types is
 * `ArrayBufferView<ArrayBuffer>`, and a `Uint8Array` is `Uint8Array<ArrayBufferLike>` — which
 * admits `SharedArrayBuffer` and so does not satisfy it. Handing back the buffer sidesteps a cast
 * that would be load-bearing and unchecked.
 */
function decode(value: string): ArrayBuffer {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  const binary = atob(padded.padEnd(Math.ceil(padded.length / 4) * 4, '='));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes.buffer;
}

/**
 * Runs the WebAuthn assertion ceremony and returns the result as JSON for the server.
 *
 * ⚠ **The options are the server's and are consumed rather than rebuilt.** What this does is the
 * one transformation the platform requires and no more: `navigator.credentials.get()` takes
 * `BufferSource`s where JSON carries base64url strings, so `challenge` and `allowCredentials[].id`
 * are decoded and nothing else is touched. Reconstructing the options — reordering, defaulting, or
 * "cleaning up" a field — breaks the challenge binding the server verifies against.
 *
 * ⚠ **Returns `null` when the user cancels**, which is not an error and must not be rendered as
 * one. A dismissed authenticator prompt and a failed assertion are indistinguishable from the
 * `DOMException` alone, and telling a user who chose to cancel that their passkey failed is how
 * they learn to distrust the button.
 *
 * @param optionsJson The `optionsJson` from `POST /api/signin/passkey/begin`, verbatim.
 * @returns The assertion, serialized for `POST /api/signin/passkey/complete`, or `null`.
 */
export async function assertPasskey(optionsJson: string): Promise<string | null> {
  if (!canUsePasskey() || optionsJson.length === 0) {
    return null;
  }

  const options = JSON.parse(optionsJson) as PublicKeyCredentialRequestOptions & {
    challenge: unknown;
    allowCredentials?: { id: unknown; type: string; transports?: string[] }[];
  };

  // ⚠ `allowCredentials` is spread in only when the server sent one. `exactOptionalPropertyTypes`
  // makes an explicit `undefined` a different thing from an absent key, and WebAuthn treats the two
  // the same only by luck of implementation.
  const request: PublicKeyCredentialRequestOptions = {
    ...options,
    challenge: decode(String(options.challenge)),
    ...(options.allowCredentials === undefined
      ? {}
      : {
          allowCredentials: options.allowCredentials.map((credential) => ({
            ...credential,
            id: decode(String(credential.id)),
            type: 'public-key' as const,
          })),
        }),
  };

  let credential: Credential | null;
  try {
    credential = await navigator.credentials.get({ publicKey: request });
  } catch {
    // ⚠ Swallowed to `null`, deliberately. `NotAllowedError` covers both "the user dismissed the
    // prompt" and "the authenticator refused", and the spec gives no way to tell them apart — by
    // design, because distinguishing them would leak whether a credential was present.
    return null;
  }

  if (credential === null) {
    return null;
  }

  const assertion = credential as PublicKeyCredential;
  const response = assertion.response as AuthenticatorAssertionResponse;

  // ⚠ `id` is sent as the base64url text the server compares ordinally against its stored
  // `PasskeyCredential.CredentialId`. `rawId` rides along because the WebAuthn response shape
  // includes it and the server's library reads the full object.
  return JSON.stringify({
    id: assertion.id,
    rawId: encode(assertion.rawId),
    type: assertion.type,
    extensions: assertion.getClientExtensionResults(),
    response: {
      authenticatorData: encode(response.authenticatorData),
      clientDataJSON: encode(response.clientDataJSON),
      signature: encode(response.signature),
      userHandle: response.userHandle === null ? null : encode(response.userHandle),
    },
  });
}
