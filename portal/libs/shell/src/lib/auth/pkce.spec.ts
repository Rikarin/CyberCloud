import { base64Url, codeChallenge, fromBase64Url, newPkcePair, randomToken } from './pkce';

/**
 * docs/plan/11 § Protocol: "Authorization Code + PKCE … The only interactive flow." The verifier
 * is what binds the code to the tab that asked for it, so the challenge has to be the one RFC
 * 7636 specifies — a challenge computed any other way is a code the host refuses to exchange.
 */
describe('PKCE — RFC 7636', () => {
  it('computes the appendix B vector', async () => {
    // RFC 7636 appendix B, verbatim: the verifier and the S256 challenge the RFC derives from it.
    const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';

    expect(await codeChallenge(verifier)).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
  });

  it('verifierIsAtLeast43Chars', async () => {
    // § 4.1: "code_verifier = high-entropy cryptographic random STRING … with a minimum length of
    // 43 characters and a maximum length of 128 characters", from the unreserved alphabet.
    for (let i = 0; i < 20; i++) {
      const { verifier } = await newPkcePair();
      expect(verifier.length).toBeGreaterThanOrEqual(43);
      expect(verifier.length).toBeLessThanOrEqual(128);
      expect(verifier).toMatch(/^[A-Za-z0-9\-._~]+$/);
    }
  });

  it('stateIsUnpredictable', () => {
    // RFC 6749 § 10.12: the state is the CSRF binding, and a repeated one is no binding at all.
    // 32 bytes of `getRandomValues` cannot collide in a thousand draws unless it is not random.
    const states = new Set<string>();
    for (let i = 0; i < 1000; i++) states.add(randomToken());

    expect(states.size).toBe(1000);
    for (const state of states) expect(state).toHaveLength(43);
  });

  it('base64url round-trips bytes without padding', () => {
    const bytes = new Uint8Array([0, 1, 2, 250, 251, 252, 253, 254, 255]);
    const text = base64Url(bytes);

    expect(text).not.toContain('=');
    expect(text).not.toContain('+');
    expect(text).not.toContain('/');
    expect([...fromBase64Url(text)]).toEqual([...bytes]);
  });
});
