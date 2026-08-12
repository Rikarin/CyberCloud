/**
 * ⚠ **The SSR cross-tenant isolation gate.**
 *
 * docs/plan/20 § SSR asks for this test by name:
 *
 * > "The authenticated portal is rendered per request and must never cache a rendered page across
 * > users. The SSR process holds no tokens; it renders the shell and the client hydrates with the
 * > user's token. Getting this wrong leaks one tenant's data to another through a CDN cache, which
 * > is the worst bug this document can prevent. It is an explicit test: two concurrent SSR requests
 * > with different tenants, asserting no shared state."
 *
 * ⚠ **Why this is a Node test against the built bundle rather than a Jest suite.** The property
 * being tested is a property of the *deployed SSR process* — its rendered bytes and its response
 * headers. A Jest suite calling `renderApplication` in-process would test a different thing: an
 * Angular API, with Jest's module transform in between, and with none of `server.ts`'s middleware
 * in the path. The CDN-cache leak the doc is about is a leak through HTTP headers on a real
 * response, so the test issues real requests to the real server and reads the real headers.
 *
 * **Concurrent, not sequential, and that is the whole point.** Two renders run one after another
 * would pass even if per-request state lived in a module-level binding, because the second would
 * simply overwrite the first before anyone looked. Firing them together is what makes a shared
 * binding observable: with shared state, whichever render resolves second wins and *both*
 * documents come back carrying its tenant.
 *
 * The failure this guards against is one `let` at module scope in any file the server bundle
 * imports:
 *
 *     let activeTenant = null;   // ← process-wide. Every request in the process shares it.
 *
 * That is why every store in `libs/shell` is an `@Injectable` and why `main.server.ts` bootstraps
 * per request.
 *
 * Run with `pnpm test:ssr`, which `pnpm build` runs after the bundle exists.
 */

import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const serverBundle = join(here, '..', 'dist', 'portal', 'server', 'server.mjs');

if (!existsSync(serverBundle)) {
  console.error(`✗ ${serverBundle} does not exist. Run \`pnpm build\` first — this gate tests the built SSR bundle, not a mock of it.`);
  process.exit(1);
}

const results = [];
const check = (name, fn) => {
  try {
    fn();
    results.push({ name, ok: true });
  } catch (error) {
    results.push({ name, ok: false, error });
  }
};

/** Boot the real server on an ephemeral port. */
const { reqHandler } = await import(pathToFileURL(serverBundle).href);
const { createServer } = await import('node:http');

const server = createServer(reqHandler);
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;

/**
 * Two requests carrying *different* tenant identity, issued together.
 *
 * The identity is carried the way a real one would be: a session cookie, plus the tenant hint
 * header the gateway sets. Neither should influence the render at all — that is the assertion.
 */
const [acme, initech] = await Promise.all([
  fetch(`${base}/resources`, {
    headers: {
      cookie: 'cc_session=acme-session-token; cc_refresh=acme-refresh',
      'x-cc-tenant': 'Acme Corporation',
      authorization: 'Bearer acme.eyJhbGciOiJIUzI1NiJ9.acme-access-token',
    },
  }).then(async (r) => ({ status: r.status, headers: r.headers, body: await r.text() })),
  fetch(`${base}/resources`, {
    headers: {
      cookie: 'cc_session=initech-session-token; cc_refresh=initech-refresh',
      'x-cc-tenant': 'Initech Holdings',
      authorization: 'Bearer initech.eyJhbGciOiJIUzI1NiJ9.initech-access-token',
    },
  }).then(async (r) => ({ status: r.status, headers: r.headers, body: await r.text() })),
]);

check('both concurrent renders succeed', () => {
  assert.equal(acme.status, 200);
  assert.equal(initech.status, 200);
});

check('neither rendered page carries the other request’s tenant', () => {
  assert.ok(!acme.body.includes('Initech'), 'the Acme render leaked the Initech tenant');
  assert.ok(!initech.body.includes('Acme'), 'the Initech render leaked the Acme tenant');
});

/**
 * ⚠ **This used to rewrite `\b(xui-[a-z-]+?)-\d+\b` and no longer does, as of `@xui` 2.2.3.**
 *
 * xUI allocated widget ids from a per-process counter, so `xui-select-trigger-1` in one render was
 * `xui-select-trigger-7` in the next and the two documents could not be compared without a waiver.
 * `XIdSequence` is now `providedIn: 'root'` — one root injector is one application and one
 * application is one render — so every generated id is a pure function of the page and the waiver is
 * gone.
 *
 * It is worth saying why the waiver was worth removing rather than keeping as harmless. It was
 * exact, and it still hid three ids that never matched it: `x-label-N`, `x-checkbox-N`, and
 * `XuiCaption`'s **bare integer**, which landed in a table's `aria-labelledby` — a legal HTML id and
 * an illegal CSS identifier, so `querySelector('#2')` threw. A pattern narrow enough to look safe
 * was still the reason nobody looked at the ids beside it.
 *
 * Comparison is now byte-for-byte. If this assertion starts failing, the cause is a real one:
 * something request-derived, clock-derived or counter-derived reached the render. `@xui/carousel`
 * autoplays during SSR from an unguarded `setInterval` and would do exactly that — the portal does
 * not depend on it, and adding that dependency is what would break this.
 */
const normalise = (html) => html;

check('no shared state: two concurrent renders produce identical shells', () => {
  // The strongest form of "no shared state" available at this layer. The SSR process holds no
  // tokens and resolves no tenant, so two requests with entirely different identities must render
  // the same document. A difference between them is per-request identity reaching the render, and
  // per-request identity in the render is what a CDN would then cache and serve to someone else.
  assert.equal(
    normalise(acme.body),
    normalise(initech.body),
    'the two renders differ, which means request identity reached the server render',
  );
});

check('the SSR process holds no tokens', () => {
  // docs/plan/20 § SSR: "The SSR process holds no tokens; it renders the shell and the client
  // hydrates with the user's token." Each request above sent a bearer token and a session cookie;
  // none of it may appear in the rendered output.
  for (const [label, page] of [['acme', acme], ['initech', initech]]) {
    assert.ok(!/Bearer\s/i.test(page.body), `${label}: a bearer token reached the rendered HTML`);
    assert.ok(!/eyJ[A-Za-z0-9_-]{5,}/.test(page.body), `${label}: a JWT reached the rendered HTML`);
    assert.ok(!page.body.includes('-session-token'), `${label}: a session cookie reached the rendered HTML`);
    assert.ok(!page.body.includes('-refresh'), `${label}: a refresh token reached the rendered HTML`);
  }
});

check('the rendered page is never cacheable across users', () => {
  // The header half of the same guarantee, and the one a CDN actually reads.
  for (const [label, page] of [['acme', acme], ['initech', initech]]) {
    const cacheControl = page.headers.get('cache-control') ?? '';
    assert.match(cacheControl, /no-store/, `${label}: Cache-Control lacks no-store — got "${cacheControl}"`);
    assert.match(cacheControl, /private/, `${label}: Cache-Control lacks private — got "${cacheControl}"`);

    const vary = page.headers.get('vary') ?? '';
    assert.match(vary, /Cookie/i, `${label}: Vary lacks Cookie — got "${vary}"`);
  }
});

check('hydration is on, so the client takes over rather than re-rendering', () => {
  // Angular writes its hydration annotation into the document when
  // `provideClientHydration()` is active. Without it the client blanks and rebuilds the page,
  // which throws away the cold-first-paint and deep-link reasons docs/plan/20 § SSR wants SSR for.
  assert.match(acme.body, /ngh=/, 'no hydration annotations in the rendered output');
});

check('no access token is written to web storage by the shipped bundles', async () => {
  // docs/plan/10 § Authentication inputs: "Access token never in `localStorage`". Asserted against
  // the built browser bundles rather than the sources, because the sources are what lint checks and
  // a dependency could bring its own storage write.
  const { readdirSync, readFileSync } = await import('node:fs');
  const browserDir = join(here, '..', 'dist', 'portal', 'browser');
  const offenders = [];

  for (const file of readdirSync(browserDir).filter((f) => f.endsWith('.js'))) {
    const source = readFileSync(join(browserDir, file), 'utf8');
    if (/\b(localStorage|sessionStorage)\s*\.\s*setItem/.test(source)) offenders.push(file);
  }

  assert.deepEqual(offenders, [], `web-storage writes found in: ${offenders.join(', ')}`);
});

server.close();

console.log('\nSSR isolation — docs/plan/20 § SSR\n');
for (const r of results) console.log(`  ${r.ok ? '✓' : '✗'} ${r.name}`);

const failed = results.filter((r) => !r.ok);

if (failed.length > 0) {
  console.error('');
  for (const r of failed) console.error(`  ✗ ${r.name}\n    ${r.error.message}`);
  console.error('');
  process.exit(1);
}

console.log('\n✓ No state is shared across concurrent SSR requests.\n');
