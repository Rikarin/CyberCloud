import { Injectable, computed, signal } from '@angular/core';

/**
 * The access token, held in memory and nowhere else.
 *
 * docs/plan/10 § Authentication inputs, the Portal row: "Authorization Code + PKCE → access token
 * in memory, refresh in an `HttpOnly` cookie scoped to the identity host" with the note "Access
 * token never in `localStorage`".
 *
 * ⚠ Three properties follow from that sentence and all three are enforced rather than documented:
 *
 * 1. **No web storage, ever.** A token in `localStorage` is readable by any script that reaches the
 *    page, which turns one XSS into a full account takeover that survives the tab closing. The
 *    lint config bans `localStorage`/`sessionStorage` outright in portal code and
 *    `access-token-store.spec.ts` asserts that using this store writes to neither.
 * 2. **Refresh is the cookie's job, not ours.** The refresh token is an `HttpOnly` cookie scoped to
 *    the identity host, so this class cannot read it and does not try. Renewal is a call to the
 *    identity host that returns a new access token; the cookie rides along and stays invisible.
 * 3. **The server holds nothing.** docs/plan/20 § SSR: "The SSR process holds no tokens; it renders
 *    the shell and the client hydrates with the user's token." On the server this store is simply
 *    never populated, and `hasToken()` is false for the whole render.
 */
@Injectable({ providedIn: 'root' })
export class AccessTokenStore {
  /**
   * A plain field rather than anything persistent. It is a signal so that the guards and the
   * interceptor can react to sign-in without a subscription, per docs/plan/20 § Live updates.
   */
  private readonly token = signal<string | null>(null);
  private readonly expiresAtEpochMs = signal<number | null>(null);

  readonly hasToken = computed(() => this.token() !== null);

  /**
   * Read the raw token. Deliberately a method and not a public signal: a signal would invite a
   * template to interpolate it, and a token in the DOM is a token in the SSR payload.
   */
  current(): string | null {
    return this.token();
  }

  /**
   * True when the token is gone or within `skewMs` of expiry. The skew exists because a token that
   * expires mid-flight produces a 401 the user sees as a random failure.
   */
  isExpired(nowMs: number = Date.now(), skewMs = 30_000): boolean {
    const expiry = this.expiresAtEpochMs();
    return expiry === null || nowMs + skewMs >= expiry;
  }

  set(token: string, expiresAtEpochMs: number): void {
    this.token.set(token);
    this.expiresAtEpochMs.set(expiresAtEpochMs);
  }

  clear(): void {
    this.token.set(null);
    this.expiresAtEpochMs.set(null);
  }
}
