import { AngularNodeAppEngine, createNodeRequestHandler, isMainModule, writeResponseToNodeResponse } from '@angular/ssr/node';
import express from 'express';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const serverDistFolder = dirname(fileURLToPath(import.meta.url));
const browserDistFolder = resolve(serverDistFolder, '../browser');

const app = express();
const angularApp = new AngularNodeAppEngine();

/**
 * ⚠ **The cache rule, first, before anything that can render.**
 *
 * The portal's version of this middleware exists because a cached page leaks one tenant's data to
 * another. Here the stake is different and at least as bad: these URLs carry the live OIDC
 * authorization request, and a cached `/signin?...` served to a second user would hand them the
 * first user's `state` and `redirect_uri`.
 */
app.use((_req, res, next) => {
  res.setHeader('Cache-Control', 'no-store, private, max-age=0, must-revalidate');
  res.setHeader('Vary', 'Cookie');
  next();
});

/**
 * The headers that make this origin a hostile place to embed or to sniff.
 *
 * ⚠ `X-Frame-Options: DENY` is the load-bearing one and it is a clickjacking control on a
 * *credential* form. Without it an attacker frames the real sign-in page, overlays it, and
 * harvests keystrokes from a page whose origin the browser will happily confirm is genuine. The
 * OIDC flow never needs this origin in a frame, so `DENY` costs nothing.
 *
 * `Referrer-Policy` repeats the `<meta>` in `index.html` because the header wins where both exist
 * and applies to the document's subresources too, and `X-Content-Type-Options` stops a browser
 * from re-interpreting a JSON error body as HTML.
 */
app.use((_req, res, next) => {
  res.setHeader('X-Frame-Options', 'DENY');
  res.setHeader('X-Content-Type-Options', 'nosniff');
  res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
  next();
});

/** Hashed build artefacts are immutable and carry nothing user-specific. */
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
 * ⚠ No cookie, header or body from the request is forwarded into the render. The engine gets the
 * URL and nothing else, which is what keeps a credential out of the serialized transfer state that
 * Angular embeds in every rendered document.
 */
app.use((req, res, next) => {
  angularApp
    .handle(req)
    .then((response) => (response ? writeResponseToNodeResponse(response, res) : next()))
    .catch(next);
});

if (isMainModule(import.meta.url)) {
  const port = process.env['PORT'] ?? 4001;
  app.listen(port, () => {
    console.log(`Identity UI SSR listening on http://localhost:${port}`);
  });
}

export const reqHandler = createNodeRequestHandler(app);
