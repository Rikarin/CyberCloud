import {
  AngularNodeAppEngine,
  createNodeRequestHandler,
  isMainModule,
  writeResponseToNodeResponse
} from '@angular/ssr/node';
import express from 'express';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const serverDistFolder = dirname(fileURLToPath(import.meta.url));
const browserDistFolder = resolve(serverDistFolder, '../browser');

/**
 * The proxy headers this process trusts: none, unless `NG_TRUST_PROXY_HEADERS` names them.
 *
 * The portal's `server.ts` has the full reasoning — `@angular/ssr` 22.1 strips the `Forwarded` and
 * `X-Forwarded-*` headers it was not told to trust and warns on stderr for each one, which behind
 * an ingress is a line per request — and the same variable, the same grammar and the same
 * middleware apply here. This file repeats them rather than importing them because the identity
 * app shares no code with the portal (`tsconfig.app.json` says why).
 *
 * ⚠ The stake is higher on this origin. A trusted `X-Forwarded-Host` becomes the host in the URL
 * the engine renders, and this origin's pages are where a person types a password; a proxy header
 * an attacker could set is a proxy header that could put their host into the sign-in page's URL.
 * The engine checks a trusted forwarded host against `angular.json`'s `security.allowedHosts` (or
 * `NG_ALLOWED_HOSTS`), so the list here must name only what the ingress in front sets and
 * validates. `scripts/ssr-identity.test.mjs` pins the default: hostile proxy headers on every
 * request, none in the rendered document, no warning printed.
 */
const trustProxyHeaders: readonly string[] = (process.env['NG_TRUST_PROXY_HEADERS'] ?? '')
  .split(',')
  .map(header => header.trim().toLowerCase())
  .filter(header => header.length > 0);

const app = express();
const angularApp = new AngularNodeAppEngine({ trustProxyHeaders });

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
 * Removes every proxy header the list above does not trust, before anything downstream can read
 * it. Node lower-cases every incoming header name, so the comparison is exact.
 */
app.use((req, _res, next) => {
  for (const name of Object.keys(req.headers)) {
    if ((name === 'forwarded' || name.startsWith('x-forwarded-')) && !trustProxyHeaders.includes(name)) {
      delete req.headers[name];
    }
  }
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
    setHeaders: res => {
      res.setHeader('Cache-Control', 'public, max-age=31536000, immutable');
    }
  })
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
    .then(response => (response ? writeResponseToNodeResponse(response, res) : next()))
    .catch(next);
});

if (isMainModule(import.meta.url)) {
  const port = process.env['PORT'] ?? 4001;
  app.listen(port, () => {
    console.log(`Identity UI SSR listening on http://localhost:${port}`);
  });
}

export const reqHandler = createNodeRequestHandler(app);
