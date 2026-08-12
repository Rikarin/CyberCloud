import { InjectionToken } from '@angular/core';
import { sanitizeReturnUrl } from './return-url';

/**
 * How this app leaves for a URL the server named.
 *
 * ⚠ **A full-page navigation and never the Angular router**, which is why this is a token rather
 * than a `Router` call. The destination is the OIDC `/authorize` request that sent the user here,
 * and resuming it means a real request carrying the session cookie the sign-in just set — a
 * client-side route change would render an Angular page that does not exist and never reach the
 * server at all.
 *
 * ⚠ **It sanitizes, so a caller cannot forget to.** The value passed in is a server response field,
 * which is precisely the case where the client-side check is the only one left — a compromised,
 * patched or simply wrong server is the threat this half of the pair exists for. Putting
 * `sanitizeReturnUrl` inside the default implementation rather than at each call site means the
 * next handler somebody adds inherits it.
 *
 * The indirection also makes the navigation observable in a test: `window.location` is
 * non-configurable in jsdom and `location.assign` is read-only, so a suite that wants to assert
 * where the page went has no way to intercept it otherwise.
 */
export const NAVIGATE = new InjectionToken<(url: string) => void>('cc.identity.navigate', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(sanitizeReturnUrl(url)),
});
