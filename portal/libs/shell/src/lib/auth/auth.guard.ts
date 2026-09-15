import { isPlatformBrowser } from '@angular/common';
import { PLATFORM_ID, inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AccessTokenStore } from './access-token-store';
import { AuthFlow } from './auth-flow';
import { TokenRefresher } from './token-refresher';

/**
 * Every route but the callback runs through this. Three answers, tried in order:
 *
 * 1. A token in memory that is not about to expire → pass, once the tenant context is loaded.
 * 2. No token → one refresh. A reload empties the store but not the identity host's refresh
 *    cookie, so this is what makes a reload land "straight back to the page" rather than on the
 *    sign-in page. A refresh that fails begins a sign-in toward the URL being navigated to, so
 *    the person comes back to the page they asked for.
 * 3. Otherwise the navigation is cancelled: the page is leaving for the identity host.
 *
 * ⚠ **On the server it passes and touches nothing.** docs/plan/20 § SSR: "The SSR process holds
 * no tokens; it renders the shell and the client hydrates with the user's token." A guard that
 * refreshed on the server would need the cookie, which the render never sees; one that redirected
 * would send a CDN a 302 to cache. The page renders its no-tenant state and the browser's guard
 * runs on hydration.
 */
export const authGuard: CanActivateFn = async (_route, state) => {
  if (!isPlatformBrowser(inject(PLATFORM_ID))) return true;

  const tokens = inject(AccessTokenStore);
  const refresher = inject(TokenRefresher);
  const flow = inject(AuthFlow);

  if (tokens.hasToken() && !tokens.isExpired()) {
    await flow.ensureContext();
    return true;
  }

  if (await refresher.refresh({ returnTo: state.url })) {
    await flow.ensureContext();
    return true;
  }

  return false;
};
