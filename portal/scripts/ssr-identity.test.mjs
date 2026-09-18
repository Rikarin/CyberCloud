/**
 * ⚠ **The identity host's SSR gate.**
 *
 * Two properties, both of which are only observable against a real rendered response, which is why
 * this is a Node test against the built bundle and not a Jest suite — the same reasoning
 * `ssr-isolation.test.mjs` sets out for the portal.
 *
 * **1. No credential reaches the rendered document.** ⚠ This is the easy mistake and it has a
 * specific mechanism: Angular SSR serializes its transfer state into a `<script>` tag inside the
 * HTML, so anything resolved on the server ships inside the page. A route resolver that read the
 * session cookie, or a server-side `HttpClient` call to `/api/signin/begin`, would put its response
 * bytes in the document — and the document is what a proxy logs and a CDN caches. The defence is
 * structural (`app.config.server.ts` adds no resolver and no state) and this asserts it against the
 * bytes rather than against the intent.
 *
 * **2. The page renders at all.** ⚠ Several `@xui/*` components are broken under SSR — `XuiDialog`,
 * `XuiDrawer` and `XuiAlertDialog` attach a CDK overlay from a constructor `effect()` that runs
 * server-side; `@xui/overflow-list` measures the DOM; `XuiText` and `XuiTextarea` read layout. A
 * sign-in page is exactly the kind of page somebody reaches for a dialog on. This test renders both
 * pages and fails on a 500, so importing a broken component is caught here rather than in
 * production.
 *
 * Run with `pnpm test:ssr:identity`, after `pnpm build:identity`.
 */

import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { missingClassRules, missingClassRulesMessage } from './rendered-class-coverage.mjs';
import {
  captureProxyHeaderWarnings,
  hostileProxyHeaders,
  proxyHeaderMarkers,
  renderWithTrustedForwardedHost
} from './ssr-proxy-headers.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const serverBundle = join(here, '..', 'dist', 'identity', 'server', 'server.mjs');

if (!existsSync(serverBundle)) {
  console.error(
    `✗ ${serverBundle} does not exist. Run \`pnpm build:identity\` first — this gate tests the built SSR bundle, not a mock of it.`
  );
  process.exit(1);
}

const results = [];

/**
 * ⚠ **`fn` must be synchronous.** An `async fn` returns a promise instead of throwing, so the pass
 * would be recorded before any assertion inside it had run and a failure would surface as an
 * unhandled rejection after the summary printed — a check that reports ✓ whatever it asserts. That
 * had actually happened in `ssr-isolation.test.mjs`; the guard is here too so it cannot happen in
 * this file either. Await outside, assert inside.
 */
const check = (name, fn) => {
  try {
    const returned = fn();

    if (returned instanceof Promise) {
      throw new Error(
        'check() was given an async function. Its assertions would be evaluated after this ' +
          'helper had already recorded a pass. Await outside, assert inside.'
      );
    }

    results.push({ name, ok: true });
  } catch (error) {
    results.push({ name, ok: false, error });
  }
};

/** Hooked before the bundle is imported — `scripts/ssr-proxy-headers.mjs` says which warning and why. */
const proxyHeaderWarnings = captureProxyHeaderWarnings();

const { reqHandler } = await import(pathToFileURL(serverBundle).href);
const { createServer } = await import('node:http');

const server = createServer(reqHandler);
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;

/**
 * Every request carries credential-shaped material in every place a browser could put it, and every
 * proxy header the engine knows, each with a value that would show in the render if it were
 * honoured.
 *
 * ⚠ The point is that NONE of it may influence the render or appear in it. A page that echoed any
 * of these into the document would be handing them to the next thing that reads the HTML.
 *
 * ⚠ The proxy headers used to be a lure — `x-forwarded-for` alone, sent so that @angular/ssr 22.1's
 * `Received "x-forwarded-for" header but "trustProxyHeaders" was not set up to allow it` on stderr
 * confirmed the engine was stripping it. `server.ts` decides the trust list now and drops every
 * header not on it before the engine looks, so the warning is a failure below rather than the
 * expected noise, and the six headers are a check: none of their values may reach the document.
 */
const hostileHeaders = {
  cookie: '__Host-cyc-session=SESSIONVALUE9f3a; other=OTHERVALUE7b21',
  authorization: 'Bearer eyJhbGciOiJFUzI1NiJ9.PAYLOADMARKER.SIGNATUREMARKER',
  ...hostileProxyHeaders
};

const fetchPage = path =>
  fetch(`${base}${path}`, { headers: hostileHeaders }).then(async r => ({
    status: r.status,
    headers: r.headers,
    body: await r.text()
  }));

const [signIn, signUp] = await Promise.all([
  // ⚠ The query string carries credential-shaped values too, because a page that reflected
  // `returnUrl` or an unexpected `password` parameter into the DOM would be the same leak by a
  // different route.
  fetchPage('/signin?returnUrl=%2Fafter&password=QUERYPASSWORD&code=123456'),
  fetchPage('/signup?returnUrl=%2Fafter')
]);

const pages = [
  ['signin', signIn],
  ['signup', signUp]
];

check('both pages render server-side without throwing', () => {
  // ⚠ A 500 here is almost always an `@xui/*` component that touches the DOM during construction.
  // The message names the likely cause so the next person does not have to rediscover it.
  for (const [label, page] of pages) {
    assert.equal(
      page.status,
      200,
      `${label} did not render server-side (HTTP ${page.status}). The usual cause is an @xui component that touches the DOM in a constructor effect — XuiDialog, XuiDrawer, XuiAlertDialog, overflow-list, XuiText and node-graph are all known-broken under SSR.`
    );
  }
});

check('the rendered pages actually contain their form, not an empty shell', () => {
  // Guards against the render "succeeding" by producing nothing, which would make every assertion
  // below vacuously true.
  assert.match(signIn.body, /Sign in/, 'the sign-in page rendered no heading');
  assert.match(signIn.body, /type="email"/, 'the sign-in page rendered no email field');
  assert.match(signUp.body, /Create an account/, 'the sign-up page rendered no heading');
});

check('no credential material reaches the rendered document', () => {
  for (const [label, page] of pages) {
    for (const marker of [
      'SESSIONVALUE9f3a',
      'OTHERVALUE7b21',
      'PAYLOADMARKER',
      'SIGNATUREMARKER',
      'QUERYPASSWORD',
      ...proxyHeaderMarkers
    ]) {
      assert.ok(
        !page.body.includes(marker),
        `${label}: "${marker}" reached the rendered HTML. Anything the server resolves is serialized into the document's transfer state and ships to the browser inside the page.`
      );
    }

    assert.ok(!/Bearer\s/i.test(page.body), `${label}: a bearer token reached the rendered HTML`);
    assert.ok(!/eyJ[A-Za-z0-9_-]{5,}/.test(page.body), `${label}: a JWT reached the rendered HTML`);
  }
});

/**
 * Keys Angular's own hydration machinery writes into the transfer state.
 *
 * ⚠ These are not application data. `__nghData__` is the node-hydration annotation table and is
 * empty here (`[]`); it exists because `provideClientHydration()` is on, which is a requirement
 * rather than a leak. Listing them explicitly means an *application* key — a resolver's payload, an
 * `HttpClient` response cached by `withFetch()` — fails the check by not being on the list, which
 * is the direction that catches the bug.
 */
const HYDRATION_KEYS = new Set(['__nghData__', '__nghDeferData__', '__nghCtx__']);

check('the transfer state carries no application data', () => {
  // ⚠ The mechanism, stated because it is the easy mistake: `provideClientHydration()` plus
  // `withFetch()` makes Angular cache a server-side `HttpClient` response into the transfer state
  // so the client does not re-issue it — and the transfer state is serialized into the document.
  // A sign-in page that resolved `/api/signin/begin` on the server would therefore ship that
  // response inside the HTML, where a proxy logs it and a CDN could cache it.
  for (const [label, page] of pages) {
    const match = page.body.match(/id="ng-state"[^>]*>([\s\S]*?)<\/script>/);
    if (!match) {
      continue;
    }

    const payload = match[1].trim();
    if (payload === '' || payload === '{}') {
      continue;
    }

    const parsed = JSON.parse(payload);
    const applicationKeys = Object.keys(parsed).filter(key => !HYDRATION_KEYS.has(key));

    assert.deepEqual(
      applicationKeys,
      [],
      `${label}: the SSR transfer state carries application data under ${applicationKeys.join(', ')}. These pages must resolve nothing server-side — whatever is in here ships inside the rendered document.`
    );

    // The hydration table itself must be empty of content too: a non-empty `__nghData__` would
    // mean a component rendered state the client is expected to adopt.
    if (Array.isArray(parsed['__nghData__'])) {
      assert.equal(parsed['__nghData__'].length, 0, `${label}: the hydration annotation table is not empty`);
    }
  }
});

check('the sign-in URL is never cacheable', () => {
  // These URLs carry the live OIDC authorization request. A cached one served to a second user
  // would hand them the first user's `state` and `redirect_uri`.
  for (const [label, page] of pages) {
    const cacheControl = page.headers.get('cache-control') ?? '';
    assert.match(cacheControl, /no-store/, `${label}: Cache-Control lacks no-store — got "${cacheControl}"`);
    assert.match(cacheControl, /private/, `${label}: Cache-Control lacks private — got "${cacheControl}"`);
  }
});

check('the credential form cannot be framed', () => {
  // ⚠ Clickjacking on a credential form. Without this an attacker frames the real sign-in page,
  // overlays it, and harvests input from a page whose origin the browser confirms is genuine. The
  // OIDC flow never needs this origin in a frame.
  for (const [label, page] of pages) {
    assert.equal(
      page.headers.get('x-frame-options'),
      'DENY',
      `${label}: the page can be framed, which makes the credential form clickjackable`
    );
  }
});

check('untrusted proxy headers make the engine warn about nothing', () => {
  // Every page above was requested with all six proxy headers. The values are checked against the
  // document by the credential check; this is the other half — the engine had nothing to strip,
  // because server.ts had already removed everything it was not told to trust.
  assert.deepEqual(
    proxyHeaderWarnings.seen,
    [],
    'the engine warned about a proxy header — server.ts is meant to drop every untrusted one before the engine sees it, so behind an ingress this is a line on stderr per request'
  );
});

/**
 * The trust list, exercised: a child process with `NG_TRUST_PROXY_HEADERS=x-forwarded-host` asks
 * for the sign-in page as `evil.example`. ⚠ On this origin that is the attack — a proxy header
 * naming a host the sign-in page then renders under — and the engine's allowed-hosts check is what
 * refuses it, which only happens if the list reached the engine.
 */
const trustedHost = renderWithTrustedForwardedHost(serverBundle, '/signin');

check('a trusted x-forwarded-host outside the allowed hosts is refused, not rendered', () => {
  assert.equal(
    trustedHost.status,
    400,
    `with x-forwarded-host trusted, the sign-in page rendered as "evil.example" (HTTP ${trustedHost.status}) instead of failing the allowed-hosts check`
  );
  assert.deepEqual(
    trustedHost.warnings,
    [],
    'the engine warned while x-forwarded-host was trusted, so the middleware and the engine are working from different lists'
  );
});

const hostile = await fetchPage('/signin?returnUrl=https%3A%2F%2Fevil.example%2Fharvest');

/**
 * The server-rendered half of the open-redirect defence.
 *
 * ⚠ Asserted on the SSR output specifically, because the pre-hydration document is a window during
 * which a user can click a link whose client-side check has not run yet. The page sanitizes on
 * read, so the hostile value must be absent from the markup the server emits, not merely corrected
 * once JavaScript starts.
 */
check('an absolute off-origin returnUrl is refused server-side too', () => {
  assert.equal(hostile.status, 200);
  assert.ok(
    !hostile.body.includes('evil.example'),
    'an off-origin returnUrl reached the rendered HTML — the pre-hydration document offers a link that leaves this origin'
  );
});

/**
 * ⚠ **The layout gate**, the same one the portal has — see `scripts/rendered-class-coverage.mjs`
 * for what it is and why it is shaped this way.
 *
 * ⚠ This app is where the defect it guards against would have been noticed last. The portal has a
 * dock manager, which collapses conspicuously; this is a form on a page, and a form missing its
 * spacing and control heights still looks like a form. It is also the app where looking wrong is
 * most expensive: `apps/identity/src/styles.css` shares the portal's token layer specifically so
 * that the sign-in origin cannot drift into looking like a different product, "the oldest phishing
 * tell there is". Sharing the tokens does not help if half the utilities never compile.
 */
check('every class the rendered page uses has a rule behind it', () => {
  const missing = missingClassRules({
    browserDir: join(here, '..', 'dist', 'identity', 'browser'),
    html: signIn.body
  });

  assert.deepEqual(missing, [], missingClassRulesMessage(missing, 'apps/identity/src/styles.css'));
});

server.close();
proxyHeaderWarnings.restore();

const failures = results.filter(r => !r.ok);
for (const result of results) {
  console.log(result.ok ? `  ✓ ${result.name}` : `  ✗ ${result.name}`);
  if (!result.ok) {
    console.error(`    ${result.error.message}`);
  }
}

if (failures.length > 0) {
  console.error(`\n${failures.length} of ${results.length} identity SSR checks failed.`);
  process.exit(1);
}

console.log(`\n${results.length} identity SSR checks passed.`);
