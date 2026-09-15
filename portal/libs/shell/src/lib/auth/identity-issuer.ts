import { DOCUMENT } from '@angular/common';
import { InjectionToken, inject } from '@angular/core';

/** The meta tag `apps/portal/src/index.html` carries the issuer in. */
export const IDENTITY_ISSUER_META = 'cyc-identity-issuer';

/**
 * The issuer the AppHost runs the identity host at, and what the portal falls back to when the
 * document names none. One string on every side — `CyberCloudResources.IdentityIssuer` pins it
 * for the gateway, and the identity host infers its `iss` from the request — so the portal
 * spelling it differently would send a person to an authority the gateway refuses.
 */
export const DEFAULT_IDENTITY_ISSUER = 'http://localhost:5101';

/**
 * Where the portal sends a person to sign in: the identity host's origin, with no trailing slash.
 *
 * Read from `<meta name="cyc-identity-issuer">` in the entry document so the deployed value is a
 * property of the served HTML rather than of the bundle — the same bundle serves
 * `localhost:5101` on the dev run and `id.cybercloud.io` in production, and a value baked into
 * the JavaScript would need a build per environment.
 *
 * ⚠ **The tenant is never part of this.** docs/plan/11 § Sign-up and tenant creation refuses a
 * global email index, and the settled shape is one issuer and one discovery document for every
 * tenant, with the tenant carried as a `tenant` request parameter and read back from the token's
 * `tid`. A per-tenant issuer would be N discovery documents for one key set, and the gateway pins
 * exactly one issuer string.
 */
export const IDENTITY_ISSUER = new InjectionToken<string>('cc.identityIssuer', {
  providedIn: 'root',
  factory: () => {
    const content = inject(DOCUMENT)
      .querySelector(`meta[name="${IDENTITY_ISSUER_META}"]`)
      ?.getAttribute('content')
      ?.trim();

    return (content ? content : DEFAULT_IDENTITY_ISSUER).replace(/\/+$/, '');
  }
});
