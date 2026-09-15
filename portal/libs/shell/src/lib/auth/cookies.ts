import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, inject } from '@angular/core';

/** The attributes every cookie this shell writes carries, beside the fixed `SameSite=Lax; Secure`. */
export interface CookieAttributes {
  /** The path the browser sends the cookie back to. `/auth/callback` for the PKCE cookie, `/` for the tenant hint. */
  readonly path: string;
  /** Seconds until the browser drops it. */
  readonly maxAgeSeconds: number;
}

/**
 * The one place portal code touches `document.cookie`.
 *
 * The portal keeps two things across a navigation: the PKCE state for the sign-in in flight, and
 * the tenant a person last signed into. Neither may go to web storage — `eslint.config.mjs` bans
 * `localStorage` and `sessionStorage` outright, and `access-token-store.spec.ts` asserts on it —
 * and a cookie is the storage that is left. A path-scoped cookie is also the better fit for the
 * verifier: it is sent only to the callback route, lives ten minutes, and is not readable by a
 * script on any other path of this origin.
 *
 * ⚠ **`Secure` is unconditional, including on `http://localhost`.** Chromium and Firefox treat
 * `localhost` as a secure context and accept the attribute there, which is what lets the dev run
 * and production take one code path. jsdom does not, so the tests substitute a memory jar rather
 * than relaxing the attribute — a cookie without `Secure` on a production origin would be sent
 * over plain HTTP by any downgrade, and the attribute is cheaper than a second code path.
 *
 * On the server there is no cookie jar to read and nothing to write: docs/plan/20 § SSR says the
 * SSR process "holds no tokens", and it holds no verifier either. Reads answer `null` and writes
 * are dropped, so a component that runs on both sides need not branch.
 */
@Injectable({ providedIn: 'root' })
export class CookieJar {
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  /** The cookie's value, URL-decoded, or `null` when the browser holds none by that name for this path. */
  read(name: string): string | null {
    if (!this.browser) return null;

    for (const pair of this.document.cookie.split(';')) {
      const separator = pair.indexOf('=');
      if (separator < 0) continue;
      if (pair.slice(0, separator).trim() !== name) continue;

      try {
        return decodeURIComponent(pair.slice(separator + 1).trim());
      } catch {
        return null;
      }
    }

    return null;
  }

  write(name: string, value: string, attributes: CookieAttributes): void {
    if (!this.browser) return;

    this.document.cookie =
      `${name}=${encodeURIComponent(value)}; Path=${attributes.path}; Max-Age=${attributes.maxAgeSeconds}; ` +
      'SameSite=Lax; Secure';
  }

  /**
   * Drops the cookie. ⚠ The path must be the one it was written with — a browser matches a cookie
   * by name and path, so a `Max-Age=0` at `/` leaves the one at `/auth/callback` in place.
   */
  remove(name: string, path: string): void {
    if (!this.browser) return;

    this.document.cookie = `${name}=; Path=${path}; Max-Age=0; SameSite=Lax; Secure`;
  }
}
