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
 * The proxy headers this process trusts, decided here rather than left to the engine's default.
 *
 * ⚠ `@angular/ssr` 22.1 builds the request URL — scheme, host, port, prefix — from `Forwarded` and
 * `X-Forwarded-*` only when told which of them to trust, strips the ones it was not told about, and
 * prints `Received "x-forwarded-for" header but "trustProxyHeaders" was not set up to allow it` for
 * every one it strips. Behind any ingress that is a line on stderr per request, about a decision
 * this file had not made.
 *
 * The decision: trust none, unless the deployment says otherwise. The render does not read the
 * request's origin — the identity issuer comes from `<meta>` in `index.html`, the route redirects
 * are relative, and the shell is the same document for every scheme and host — so a trusted
 * `X-Forwarded-Proto` would change nothing this process emits, and `X-Forwarded-For` is a client
 * identity the render must never see. `NG_TRUST_PROXY_HEADERS`, the engine's own variable, is the
 * one knob: a comma-separated list of the exact headers the thing in front of this process sets and
 * validates, for example `x-forwarded-proto,x-forwarded-host`. It is read once at startup and
 * handed to the engine explicitly, so the list the engine applies and the list the middleware below
 * applies are the same list. A name that is not `forwarded` or `x-forwarded-*` fails the engine's
 * constructor, so a typo stops the process instead of trusting nothing silently.
 *
 * ⚠ Trusting `x-forwarded-host` also subjects it to the engine's allowed-hosts check
 * (`angular.json` → `security.allowedHosts`, or `NG_ALLOWED_HOSTS`), and a host outside that list
 * is answered with 400. `scripts/ssr-isolation.test.mjs` pins both halves: with the variable unset
 * a request full of hostile proxy headers renders with none of them in the document and no warning
 * printed; with `x-forwarded-host` trusted, `evil.example` is refused.
 */
const trustProxyHeaders: readonly string[] = (process.env['NG_TRUST_PROXY_HEADERS'] ?? '')
  .split(',')
  .map(header => header.trim().toLowerCase())
  .filter(header => header.length > 0);

const app = express();
const angularApp = new AngularNodeAppEngine({ trustProxyHeaders });

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
 * Removes every proxy header the list above does not trust, at this process's edge.
 *
 * The engine would drop them anyway; dropping them here is what turns its per-request warning into
 * the stated policy above, and it means nothing downstream — `express.static`, the engine, the
 * render — ever sees a `Forwarded` or `X-Forwarded-*` value this process did not decide to trust.
 * Node lower-cases every incoming header name, so the comparison is exact.
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
 * Hashed build artefacts are immutable and contain nothing user-specific, so they get the opposite
 * treatment. `index.html` is deliberately excluded — it is the document the SSR engine renders and
 * must not be served from the static cache.
 */
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
 * ⚠ No token, cookie or header from the request is forwarded into the render. The engine gets the
 * URL and nothing else that identifies a user, so the rendered shell is identical for everyone and
 * the tenant-specific content arrives after the client hydrates with its own token — docs/plan/20
 * § SSR: "The SSR process holds no tokens; it renders the shell and the client hydrates with the
 * user's token."
 */
app.use((req, res, next) => {
  angularApp
    .handle(req)
    .then(response => (response ? writeResponseToNodeResponse(response, res) : next()))
    .catch(next);
});

if (isMainModule(import.meta.url)) {
  const port = process.env['PORT'] ?? 4000;
  app.listen(port, () => {
    console.log(`Portal SSR listening on http://localhost:${port}`);
  });
}

export const reqHandler = createNodeRequestHandler(app);
