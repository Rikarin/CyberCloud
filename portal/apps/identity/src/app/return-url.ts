/** Where a user goes when the request named nowhere usable. */
export const DEFAULT_RETURN_URL = '/';

/** The longest value considered. Past this the answer is {@link DEFAULT_RETURN_URL}. */
export const MAX_RETURN_URL_LENGTH = 1024;

/**
 * Reports whether `candidate` is a same-origin path this app may navigate to after a sign-in.
 *
 * ⚠ **This is a deliberate second copy of `CyberCloud.Identity.SignIn.ReturnUrl`, not a
 * duplication to clean up.** The two run in different places against different attackers: the
 * server's copy guards a `Location` header it emits, and this one guards a client-side navigation
 * the server never sees, because a redirect decided after hydration never reaches the server at
 * all. Deleting either leaves a live open redirect. `return-url.spec.ts` and `ReturnUrlTests.cs`
 * carry the same corpus of hostile spellings so the two cannot drift apart quietly.
 *
 * The rule is an allow-list of one shape rather than a block-list of tricks — see the C# file for
 * why every block-list version of this control has eventually been beaten.
 */
export function isSafeReturnUrl(candidate: string | null | undefined): boolean {
  if (!candidate || candidate.length > MAX_RETURN_URL_LENGTH) {
    return false;
  }

  // ── 1. A path starts with exactly one slash. ────────────────────────────────────────────────
  //
  // ⚠ The second character is the scheme-relative attack. `//evil.example` is an absolute URL to
  // every browser — the scheme is inherited, the authority is `evil.example` — while looking like a
  // path to a check that tested only the first character.
  if (candidate[0] !== '/') {
    return false;
  }

  if (candidate.length > 1 && (candidate[1] === '/' || candidate[1] === '\\')) {
    return false;
  }

  // ── 2. No backslash anywhere. ──────────────────────────────────────────────────────────────
  // Browsers normalize `\` to `/` while string checks do not, which is a parser differential.
  if (candidate.includes('\\')) {
    return false;
  }

  // ── 3. No control characters. ──────────────────────────────────────────────────────────────
  //
  // ⚠ Browsers STRIP tab, CR and LF from a URL before parsing it, so the value a check inspects and
  // the value the browser navigates to are different strings. Refusing them is simpler and safer
  // than re-deriving each engine's normalization.
  for (let i = 0; i < candidate.length; i++) {
    const code = candidate.charCodeAt(i);
    if (code < 0x20 || code === 0x7f) {
      return false;
    }
  }

  // ── 4. Resolved against an origin, it must still be on that origin. ────────────────────────
  //
  // The question a browser actually answers. Testing the RESULT rather than the input's shape means
  // an unanticipated spelling fails closed.
  //
  // ⚠ `.invalid` is reserved by RFC 2606 and resolves nowhere, so an escaped probe origin fails
  // loudly instead of reaching a host somebody owns.
  const probe = 'https://return-url-probe.invalid';

  try {
    const resolved = new URL(candidate, probe);
    return resolved.origin === probe;
  } catch {
    return false;
  }
}

/**
 * Returns `candidate` when it is a safe same-origin path, and {@link DEFAULT_RETURN_URL} otherwise.
 *
 * ⚠ Never throws and never propagates a rejected value, so a caller cannot use the input by
 * forgetting to check a boolean. A `tryParse` whose false branch somebody leaves empty fails open,
 * and for this control failing open *is* the vulnerability.
 */
export function sanitizeReturnUrl(candidate: string | null | undefined): string {
  return isSafeReturnUrl(candidate) ? (candidate as string) : DEFAULT_RETURN_URL;
}
