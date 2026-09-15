import { Injectable, inject, signal } from '@angular/core';
import { AccessTokenStore } from './access-token-store';
import { CookieJar } from './cookies';
import { decodeJwtPayload, stringClaim } from './jwt-payload';

/** The cookie the last signed-in tenant is remembered in — the `tenant` hint on the next `/authorize`. */
export const TENANT_COOKIE = 'cyc-tenant';

/** 400 days, the longest lifetime Chromium honors. A tenant id is in every resource id; it is not a secret. */
export const TENANT_COOKIE_MAX_AGE_SECONDS = 34_560_000;

/** What `POST /token` answers with on success — RFC 6749 § 5.1, with OIDC's `id_token`. */
export interface TokenResponse {
  readonly access_token: string;
  readonly token_type: string;
  readonly expires_in: number;
  readonly id_token?: string;
  readonly scope?: string;
}

/** Who is signed in, as the tokens describe them. Labels, not authority — see `decodeJwtPayload`. */
export interface SignedInAccount {
  /** The access token's `tid`, `N` form. */
  readonly tenantId: string;
  /** The access token's `sub`. */
  readonly subjectId: string;
  /** The id_token's `email`, when the response carried one. */
  readonly email: string | null;
  /** The id_token's `name`, when the response carried one. */
  readonly name: string | null;
}

/**
 * The signed-in person, as the last accepted token response described them.
 *
 * Every token the portal holds arrives through {@link accept}, whether the callback page's code
 * exchange or a refresh minted it, so the four things a new token implies happen in one place:
 * the access token goes into `AccessTokenStore`, its expiry is recorded for the refresher, the
 * account labels are decoded, and the tenant is remembered in the `cyc-tenant` cookie.
 *
 * ⚠ Like every store in `libs/shell`, this is `providedIn: 'root'` and therefore per-injector,
 * which under SSR is per-request. It is never populated on the server, so the server render
 * carries no account — docs/plan/20 § SSR.
 */
@Injectable({ providedIn: 'root' })
export class AuthSession {
  private readonly tokens = inject(AccessTokenStore);
  private readonly cookies = inject(CookieJar);

  private readonly _account = signal<SignedInAccount | null>(null);
  private readonly _expiresAtEpochMs = signal<number | null>(null);

  readonly account = this._account.asReadonly();

  /** When the current access token expires, for the refresher to schedule against. */
  readonly expiresAtEpochMs = this._expiresAtEpochMs.asReadonly();

  /**
   * Takes a token response into the session. Answers `null` — and takes nothing — when the access
   * token names no tenant or no subject, because a token the portal cannot place in a tenant is
   * a token it cannot act with.
   */
  accept(response: TokenResponse, nowMs: number = Date.now()): SignedInAccount | null {
    const access = decodeJwtPayload(response.access_token);
    const tenantId = stringClaim(access, 'tid');
    const subjectId = stringClaim(access, 'sub');
    if (tenantId === null || subjectId === null) return null;

    const identity = response.id_token === undefined ? null : decodeJwtPayload(response.id_token);
    const previous = this._account();

    // A refresh answers no id_token, so the labels from the sign-in survive a refresh for the
    // same person and are dropped for a different one.
    const carry = previous !== null && previous.subjectId === subjectId && previous.tenantId === tenantId;
    const account: SignedInAccount = {
      tenantId,
      subjectId,
      email: stringClaim(identity, 'email') ?? (carry ? previous.email : null),
      name: stringClaim(identity, 'name') ?? (carry ? previous.name : null)
    };

    this.tokens.set(response.access_token, nowMs + response.expires_in * 1000);
    this._expiresAtEpochMs.set(nowMs + response.expires_in * 1000);
    this._account.set(account);
    this.cookies.write(TENANT_COOKIE, tenantId, { path: '/', maxAgeSeconds: TENANT_COOKIE_MAX_AGE_SECONDS });

    return account;
  }

  /**
   * Forgets the token and the account. ⚠ The tenant cookie stays: it is the hint that lets the
   * next sign-in skip the "which organization" question, and it identifies nobody.
   */
  clear(): void {
    this.tokens.clear();
    this._expiresAtEpochMs.set(null);
    this._account.set(null);
  }

  /** The tenant a person last signed into on this browser, or `null` on a fresh one. */
  rememberedTenant(): string | null {
    return this.cookies.read(TENANT_COOKIE);
  }
}
