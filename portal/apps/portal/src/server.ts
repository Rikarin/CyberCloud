import { AngularNodeAppEngine, createNodeRequestHandler, isMainModule, writeResponseToNodeResponse } from '@angular/ssr/node';
import express from 'express';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const serverDistFolder = dirname(fileURLToPath(import.meta.url));
const browserDistFolder = resolve(serverDistFolder, '../browser');

const app = express();
const angularApp = new AngularNodeAppEngine();

/**
 * ⚠ **The cache rule, and it comes before anything that can render.**
 *
 * docs/plan/20 § SSR: "The authenticated portal is rendered per request and must never cache a
 * rendered page across users. … Getting this wrong leaks one tenant's data to another through a
 * CDN cache, which is the worst bug this document can prevent."
 *
 * This middleware is registered first, and sets the header unconditionally on every non-static
 * response, so the safe value is the default and an unsafe one has to be an explicit act. The
 * inverse — setting `no-store` on the authenticated routes and forgetting a new one — is a leak
 * that ships silently, because a cached page looks exactly like a fast page.
 *
 * Each directive earns its place:
 *
 * - `no-store` — do not write this to any cache, shared or private. Stronger than `no-cache`, which
 *   permits storing and only requires revalidation.
 * - `private` — belt and braces for a CDN that honours `private` but mishandles `no-store`.
 * - `max-age=0, must-revalidate` — for the intermediaries that predate `no-store`.
 * - `Vary: Cookie, Authorization` — so that anything which caches despite the above at least
 *   cannot serve one user's page to another. This is the last line, not the first.
 */
app.use((_req, res, next) => {
  res.setHeader('Cache-Control', 'no-store, private, max-age=0, must-revalidate');
  res.setHeader('Vary', 'Cookie, Authorization');
  next();
});

/**
 * Hashed build artefacts are immutable and contain nothing user-specific, so they get the opposite
 * treatment. `index.html` is deliberately excluded — it is the document the SSR engine renders and
 * must not be served from the static cache.
 */
app.use(
  express.static(browserDistFolder, {
    maxAge: '1y',
    index: false,
    redirect: false,
    setHeaders: (res) => {
      res.setHeader('Cache-Control', 'public, max-age=31536000, immutable');
    },
  }),
);

/**
 * Everything else is rendered per request.
 *
 * ⚠ No token, cookie or header from the request is forwarded into the render. The engine gets the
 * URL and nothing else that identifies a user, so the rendered shell is identical for everyone and
 * the tenant-specific content arrives after the client hydrates with its own token — docs/plan/20
 * § SSR: "The SSR process holds no tokens; it renders the shell and the client hydrates with the
 * user's token."
 */
app.use((req, res, next) => {
  angularApp
    .handle(req)
    .then((response) => (response ? writeResponseToNodeResponse(response, res) : next()))
    .catch(next);
});

if (isMainModule(import.meta.url)) {
  const port = process.env['PORT'] ?? 4000;
  app.listen(port, () => {
    console.log(`Portal SSR listening on http://localhost:${port}`);
  });
}

export const reqHandler = createNodeRequestHandler(app);
