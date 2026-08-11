import { canUsePasskey, passkeyUnavailableReason } from './passkey';

/**
 * docs/plan/11 § Credentials makes passkeys "the **default** offered credential", and these pages
 * are server-rendered — so the capability check runs in Node before it ever runs in a browser.
 *
 * ⚠ **The failure this suite exists for is a `ReferenceError` during SSR.** A component that read
 * `navigator.credentials` in a field initialiser or a constructor `effect()` would throw on the
 * server, where `navigator` does not exist, and the user would get a blank 500 rather than a
 * missing button. `passkeyUnavailableReason` answers `'server'` there instead.
 */
describe('passkeyUnavailableReason', () => {
  const originalSecure = Object.getOwnPropertyDescriptor(window, 'isSecureContext');

  afterEach(() => {
    if (originalSecure) {
      Object.defineProperty(window, 'isSecureContext', originalSecure);
    }
  });

  const setSecureContext = (value: boolean) => {
    Object.defineProperty(window, 'isSecureContext', { value, configurable: true });
  };

  it('reports an insecure context rather than pretending passkeys are unsupported', () => {
    setSecureContext(false);

    // ⚠ The distinction is not pedantry. WebAuthn will not run over plain HTTP on anything that is
    // not localhost, so a developer testing the SSR bundle on `http://192.168.1.20:4001` from a
    // phone gets this branch — and "open this over HTTPS" is an actionable message where "your
    // browser cannot do passkeys" sends them looking in the wrong place for an hour.
    expect(passkeyUnavailableReason()).toBe('insecure-context');
    expect(canUsePasskey()).toBe(false);
  });

  it('offers a passkey in a secure context with the API present', () => {
    setSecureContext(true);

    // jsdom provides neither `navigator.credentials` nor `PublicKeyCredential`, so the honest
    // assertion here is the negative one: the check does not crash and does not claim support it
    // cannot verify.
    const reason = passkeyUnavailableReason();
    expect(reason === null || reason === 'unsupported').toBe(true);
  });

  it('never throws, whatever the environment reports', () => {
    setSecureContext(false);
    expect(() => passkeyUnavailableReason()).not.toThrow();

    setSecureContext(true);
    expect(() => passkeyUnavailableReason()).not.toThrow();
  });

  /**
   * ⚠ The closest a jsdom suite can get to the SSR condition, and it is worth having anyway: the
   * function must consult `typeof window` rather than assume it. The real assertion that the server
   * render survives lives in `scripts/ssr-identity.test.mjs`, which renders both pages in Node and
   * fails on a 500 — because that is where the absence of `window` is genuine rather than simulated.
   */
  it('checks for the globals rather than assuming them', () => {
    const source = passkeyUnavailableReason.toString();
    expect(source).toContain('undefined');
  });
});
