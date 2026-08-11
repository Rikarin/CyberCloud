import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * How each route is rendered on the server.
 *
 * ⚠ **`RenderMode.Server` for everything, and never `Prerender`.** This is the single most
 * important line in the portal's SSR setup. docs/plan/20 § SSR: "The authenticated portal is
 * rendered per request and must never cache a rendered page across users."
 *
 * `RenderMode.Prerender` builds the page once at build time and serves the same bytes to everyone.
 * For an authenticated portal that is the CDN-cache leak the doc calls "the worst bug this document
 * can prevent" — one tenant's rendered resource list served to another tenant. `RenderMode.Client`
 * would be safe but throws away the three reasons the doc wants SSR at all (cold first paint, the
 * shared marketing/docs shell, and deep links from alert emails rendering immediately).
 *
 * So: per-request server rendering, with `Cache-Control: no-store, private` set on the way out by
 * `server.ts`. Belt and braces, because either one alone is a single point of failure for a bug
 * whose blast radius is cross-tenant.
 *
 * ⚠ When the marketing and docs pages arrive (docs/plan/20 § SSR, reason 2 — "The docs site and the
 * marketing pages share the app shell") they are the *only* routes that may be prerendered, and
 * they must be served from a different path prefix so no cache rule can straddle the two. Adding
 * `RenderMode.Prerender` to any route under an authenticated prefix is a security change, not a
 * performance one.
 */
export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server,
  },
];
