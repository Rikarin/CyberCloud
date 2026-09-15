import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { DestroyRef, Injectable, PLATFORM_ID, effect, inject, untracked } from '@angular/core';
import { AuthFlow } from './auth-flow';
import { AuthSession } from './auth-session';
import { IDENTITY_ISSUER } from './identity-issuer';
import { PORTAL_CLIENT_ID, postTokenRequest } from './token-endpoint';

/** How long before expiry the next token is fetched: `exp − 60 s`, well clear of the store's 30 s skew. */
export const REFRESH_LEAD_MS = 60_000;

/**
 * Keeps the access token fresh from the refresh cookie.
 *
 * docs/plan/10 § Authentication inputs: "Tokens are short (10 minutes) and scoped to a tenant …
 * a 10-minute token with a refresh flow costs the SDK one line." This is the portal's line. It
 * posts `grant_type=refresh_token&client_id=cyc-portal` — ⚠ **and no `refresh_token` field**,
 * because the portal never held one: the host moved it into the `__Host-cyc-refresh` cookie on
 * its own origin at the code exchange, reads it back from that cookie when the request's `Origin`
 * is the portal's, and replaces it on every success. `ISessionGrain.RefreshAsync` rotates the
 * handle with reuse detection (docs/plan/11 § Sessions and revocation), so a refresh that fails
 * is a chain that is over — the store is cleared and a sign-in begins, rather than retried.
 *
 * Three moments call it, and they share one in-flight request so that two 401s landing
 * together — every page opens with several calls — rotate the chain once rather than tripping
 * reuse detection against each other:
 *
 * - the timer, armed at `exp − 60 s` whenever `AuthSession` accepts a token;
 * - `accessTokenInterceptor`, on a 401 from `/api`, once, then one retry;
 * - `authGuard`, when a navigation finds no token — a reload still holds the cookie, so this is
 *   what turns a reload into "straight back to the page" rather than a round trip to the host.
 */
@Injectable({ providedIn: 'root' })
export class TokenRefresher {
  private readonly session = inject(AuthSession);
  private readonly flow = inject(AuthFlow);
  private readonly issuer = inject(IDENTITY_ISSUER);
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private inFlight: Promise<boolean> | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private scheduledForMs: number | null = null;

  constructor() {
    // Armed from the session rather than by each caller, so a token accepted anywhere is a token
    // that will be refreshed. On the server the session is never populated and nothing is armed.
    effect(() => {
      const expiresAt = this.session.expiresAtEpochMs();
      untracked(() => this.arm(expiresAt));
    });

    inject(DestroyRef).onDestroy(() => this.disarm());
  }

  /**
   * One refresh, shared by everyone who asks while it is running. `true` when a new access token
   * is in the store. On any failure the store is cleared and a sign-in begins toward
   * `returnTo` — the current path by default — and the answer is `false`.
   */
  refresh(options: { returnTo?: string } = {}): Promise<boolean> {
    if (!this.browser) return Promise.resolve(false);

    this.inFlight ??= this.run(options.returnTo).finally(() => {
      this.inFlight = null;
    });

    return this.inFlight;
  }

  /** When the timer will fire, in epoch milliseconds, or `null` while nothing is armed. */
  scheduledAt(): number | null {
    return this.scheduledForMs;
  }

  private async run(returnTo: string | undefined): Promise<boolean> {
    const result = await postTokenRequest(this.issuer, {
      grant_type: 'refresh_token',
      client_id: PORTAL_CLIENT_ID
    });

    if (result.ok && this.session.accept(result.response) !== null) return true;

    this.session.clear();
    await this.flow.beginSignIn(returnTo ?? this.currentPath());
    return false;
  }

  private arm(expiresAtEpochMs: number | null): void {
    this.disarm();
    if (!this.browser || expiresAtEpochMs === null) return;

    const at = expiresAtEpochMs - REFRESH_LEAD_MS;
    this.scheduledForMs = at;
    this.timer = setTimeout(
      () => {
        this.timer = null;
        this.scheduledForMs = null;
        void this.refresh();
      },
      Math.max(0, at - Date.now())
    );
  }

  private disarm(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.scheduledForMs = null;
  }

  private currentPath(): string {
    const location = this.document.defaultView?.location;
    return location === undefined ? '/' : `${location.pathname}${location.search}`;
  }
}
