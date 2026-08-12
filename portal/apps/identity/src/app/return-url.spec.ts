import { DEFAULT_RETURN_URL, isSafeReturnUrl, sanitizeReturnUrl } from './return-url';

/**
 * The open-redirect suite, carrying the same corpus as `ReturnUrlTests.cs`.
 *
 * ⚠ **The assertion that matters is the refusal.** A suite that only checked "a relative path
 * survives" passes against a `sanitizeReturnUrl` that returns its input unchanged — which is the
 * vulnerability, not a near miss. Every hostile spelling is therefore its own case.
 */
describe('sanitizeReturnUrl', () => {
  const rejected = [
    // Plainly absolute.
    'https://evil.example',
    'https://evil.example/signin',
    'http://evil.example',
    'HTTPS://EVIL.EXAMPLE',

    // Scheme-relative — an absolute URL that looks like a path, and the most common bypass.
    '//evil.example',
    '//evil.example/path',
    '///evil.example',

    // The same attack spelled with backslashes, which browsers normalize to slashes.
    '/\\evil.example',
    '\\\\evil.example',
    '/\\/evil.example',
    'https:/\\evil.example',

    // Schemes that execute rather than navigate.
    'javascript:alert(1)',
    'javascript:alert(document.cookie)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',

    // Control characters browsers strip before parsing.
    '/\tevil',
    '/\nevil',
    '/\revil',
    '\t//evil.example',
    'java\nscript:alert(1)',

    // Not a path at all.
    'evil.example',
    'signin',
    '../admin',
    '',
  ];

  const accepted = [
    '/',
    '/signin',
    '/authorize?client_id=portal&response_type=code',
    '/resource-groups/prod?tab=access',
    '/a/b/c',
    '/path#fragment',
    '/redirect:target',
  ];

  it.each(rejected)('refuses %j and falls back to the default', (candidate) => {
    expect(isSafeReturnUrl(candidate)).toBe(false);
    // ⚠ The second assertion is the one that catches a fail-open refactor: a sanitize that returned
    // its input regardless would still satisfy the first.
    expect(sanitizeReturnUrl(candidate)).toBe(DEFAULT_RETURN_URL);
  });

  it.each(accepted)('lets the same-origin path %j through unchanged', (candidate) => {
    expect(isSafeReturnUrl(candidate)).toBe(true);
    expect(sanitizeReturnUrl(candidate)).toBe(candidate);
  });

  it('falls back for null, undefined and an overlong value', () => {
    expect(sanitizeReturnUrl(null)).toBe(DEFAULT_RETURN_URL);
    expect(sanitizeReturnUrl(undefined)).toBe(DEFAULT_RETURN_URL);
    expect(sanitizeReturnUrl(`/${'a'.repeat(1024)}`)).toBe(DEFAULT_RETURN_URL);
  });

  it('produces a value that always resolves back to the calling origin', () => {
    for (const candidate of [...rejected, ...accepted]) {
      const resolved = new URL(sanitizeReturnUrl(candidate), 'https://id.cybercloud.test');
      expect(resolved.origin).toBe('https://id.cybercloud.test');
    }
  });

  /**
   * ⚠ The two implementations are a deliberate pair, not a duplication to collapse. This test does
   * not check they are the same code — it checks the corpus they are both asserted against has not
   * been quietly trimmed on one side, which is how the two would drift.
   */
  it('carries every spelling the C# suite carries', () => {
    expect(rejected).toHaveLength(24);
    expect(accepted).toHaveLength(7);
  });
});
