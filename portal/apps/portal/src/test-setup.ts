// ── docs/plan/20 § Accessibility, i18n, theming ─────────────────────────────────────────────
// "i18n from day one with `@angular/localize`." The shell's labels are `$localize` tagged
// templates, so the runtime has to be installed before any component is created — otherwise every
// component under test throws on a label rather than on the thing being tested.
import '@angular/localize/init';

import { setupZonelessTestEnv } from 'jest-preset-angular/setup-env/zoneless';
import { webcrypto } from 'node:crypto';

// ── docs/plan/20 § Live updates ─────────────────────────────────────────────────────────────
// "the templates are `OnPush` and zoneless". The test environment has to be zoneless too, or a
// component that only updates because zone.js ran change detection for it would pass here and
// fail in the app.
setupZonelessTestEnv();

// ── docs/plan/11 § Protocol ─────────────────────────────────────────────────────────────────
// "Authorization Code + PKCE … The only interactive flow." The PKCE challenge is a SHA-256 through
// `crypto.subtle.digest`, and jsdom ships `crypto.getRandomValues` without `crypto.subtle`. Node's
// WebCrypto is the same W3C interface over the same algorithm, so it stands in — `pkce.spec.ts`
// checks it against RFC 7636's appendix B vector, which would catch a stand-in that was not.
if (globalThis.crypto.subtle === undefined) {
  Object.defineProperty(globalThis.crypto, 'subtle', { value: webcrypto.subtle, configurable: true });
}
