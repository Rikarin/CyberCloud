import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * How each route is rendered on the server.
 *
 * ⚠ **`RenderMode.Server`, and never `Prerender`.** A prerendered sign-in page is built once and
 * served byte-identical to everyone, which for this origin means the OIDC request parameters baked
 * into one user's page would be handed to the next. `RenderMode.Client` would be safe but gives up
 * the first-paint reason docs/plan/11 § Effort asks for SSR here at all — a sign-in page that shows
 * a spinner is a sign-in page people abandon.
 */
export const serverRoutes: ServerRoute[] = [
  {
    path: '**',
    renderMode: RenderMode.Server,
  },
];
