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
import { existsSync, readFileSync, readdirSync } from 'node:fs';
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

/**
 * ⚠ **`fn` must be synchronous, and the guard below is why.**
 *
 * This helper used to take whatever it was given and call it inside a `try`. An `async fn` returns
 * a promise instead of throwing, so `results.push({ ok: true })` ran before any assertion inside it
 * had been evaluated and a failure surfaced as an unhandled rejection *after* the summary printed —
 * a check that reported ✓ no matter what it asserted. One of the checks in this file was written
 * that way, which means it had never been capable of failing.
 *
 * Rejecting an async `fn` outright rather than awaiting it is deliberate: awaiting would make the
 * mistake invisible again by making it work, and every assertion here is against bytes already in
 * memory or already on disk. If one genuinely needs to await something, do the awaiting outside and
 * assert on the result inside.
 */
const check = (name, fn) => {
  try {
    const returned = fn();

    if (returned instanceof Promise) {
      throw new Error(
        'check() was given an async function. Its assertions would be evaluated after this ' +
          'helper had already recorded a pass. Await outside, assert inside.',
      );
    }

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

check('no access token is written to web storage by the shipped bundles', () => {
  // docs/plan/10 § Authentication inputs: "Access token never in `localStorage`". Asserted against
  // the built browser bundles rather than the sources, because the sources are what lint checks and
  // a dependency could bring its own storage write.
  const browserDir = join(here, '..', 'dist', 'portal', 'browser');
  const offenders = [];

  for (const file of readdirSync(browserDir).filter((f) => f.endsWith('.js'))) {
    const source = readFileSync(join(browserDir, file), 'utf8');
    if (/\b(localStorage|sessionStorage)\s*\.\s*setItem/.test(source)) offenders.push(file);
  }

  assert.deepEqual(offenders, [], `web-storage writes found in: ${offenders.join(', ')}`);
});

/**
 * ⚠ **The layout gate: every class the rendered page uses must have a rule behind it.**
 *
 * This is here because of a defect that every gate in this repository passed. The portal's
 * stylesheet was missing `@source '../../../node_modules/@xui'`, and Tailwind's automatic source
 * detection skips `node_modules`. So the utilities the portal's own templates ask for were
 * compiled, and the utilities only xUI's published templates ask for were not — 66 of the 115
 * classes on the rendered page had no rule. `flex-1` and `min-w-0` were among them, which is what
 * the dock manager's panes fill their host with, so the workspace collapsed to two content-sized
 * boxes in the top-left corner of an empty 1280×720 page.
 *
 * ⚠ **Why the existing checks could not see it.** The one directly above compares two concurrent
 * renders byte-for-byte, which is a real and worth-keeping property — but two equally collapsed
 * layouts are byte-identical. Nothing in a suite that compares a render to another render can
 * notice that both are wrong in the same way. This check compares the render to something else
 * entirely: the stylesheet that is supposed to serve it.
 *
 * ⚠ **Why this shape rather than a geometry assertion.** The obvious gate is "the main surface
 * fills a plausible fraction of the viewport", and it is the right instinct — but measuring it
 * needs a layout engine. jsdom has none (`clientWidth` is always 0 there), so the Jest suite cannot
 * express it, and a real one means adding a headless browser: a browser binary in the CI image, a
 * download step, and a class of flake that gets gates disabled rather than fixed. A screenshot diff
 * costs more again — a baseline image per viewport, per theme, regenerated and re-reviewed by a
 * human on every intentional design change, which is a standing tax on exactly the kind of change
 * this codebase makes most often.
 *
 * So this asserts the *cause* rather than the symptom, and gets something the geometry check would
 * not have given: it has no magic numbers at all. There is no pinned pixel count to go stale, no
 * fraction to re-tune when the layout changes, and no baseline to regenerate. The expected set is
 * derived from the render itself on every run, so it tracks the markup automatically — a component
 * added tomorrow is covered the day it is added, with nothing to update here.
 *
 * ⚠ **What it does not catch, stated plainly.** It proves every class *resolves*, not that the
 * result *fills the screen*. A genuine flex/min-height mistake written by hand in portal markup
 * would compile to real CSS and pass this check while still collapsing. This closes the "markup
 * references CSS that was never emitted" hole, which is the one that actually happened and the one
 * that is invisible by construction — a missing class is not an error in HTML, in CSS, in Angular
 * or in Tailwind. It is not a general layout gate and should not be mistaken for one.
 */
check('every class the rendered page uses has a rule behind it', () => {
  const browserDir = join(here, '..', 'dist', 'portal', 'browser');
  const stylesheets = readdirSync(browserDir).filter((f) => f.endsWith('.css'));

  assert.ok(stylesheets.length > 0, 'the build emitted no stylesheet at all');

  /**
   * Every class selector the emitted stylesheets define.
   *
   * ⚠ Read off the files on disk, not out of the rendered document. The production build inlines
   * *critical* CSS into a `<style>` element and defers the rest, so the document contains a subset
   * by design; checking the page against itself would call the deferred half missing.
   *
   * Tailwind escapes the characters that are illegal in a CSS identifier — `.w-0\.5`,
   * `.focus-visible\:outline-2`, `.\[\&_svg\]\:size-3` — so the escapes come back out to recover
   * the class as it is written in the markup.
   */
  const defined = new Set();

  for (const sheet of stylesheets) {
    const css = readFileSync(join(browserDir, sheet), 'utf8');
    for (const m of css.matchAll(/\.((?:\\.|[A-Za-z0-9_-])+)/g)) {
      defined.add(m[1].replace(/\\(.)/g, '$1'));
    }
  }

  /**
   * Every class the rendered document actually puts on an element.
   *
   * `<script>` and `<style>` blocks come out first: the hydration state is JSON that can contain
   * anything, and the inlined critical CSS is full of selectors that are not markup.
   */
  const markup = acme.body
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '')
    .replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, '');

  const entities = { amp: '&', lt: '<', gt: '>', quot: '"', '#39': "'" };
  const used = new Set();

  for (const attr of markup.matchAll(/\sclass="([^"]*)"/g)) {
    const value = attr[1].replace(/&(amp|lt|gt|quot|#39);/g, (_, e) => entities[e]);
    for (const token of value.split(/\s+/)) if (token) used.add(token);
  }

  assert.ok(used.size > 0, 'the rendered document carries no classes at all — is this the shell?');

  /**
   * Classes that correctly have no rule of their own.
   *
   * ⚠ Kept to markers and runtime plumbing on purpose. Every entry here is a hole in the check, so
   * the bar for adding one is that the class *cannot* have a rule — not that it happens not to.
   * Anything added because "that one is fine" would have been the line that let this defect back
   * in.
   */
  const isBracketFragment = (cls) => {
    const opens = (cls.match(/\[/g) ?? []).length;
    const closes = (cls.match(/\]/g) ?? []).length;

    return opens !== closes || cls.endsWith(',');
  };

  const ruleless = (cls) =>
    // Tailwind's variant markers. `group` and `peer` exist to be referenced by `group-*:` and
    // `peer-*:` selectors on other elements; they emit nothing themselves, by design.
    cls === 'group' ||
    cls === 'peer' ||
    /^(group|peer)\/[A-Za-z0-9_-]+$/.test(cls) ||
    // Angular's runtime classes, written by the framework rather than by anyone's template.
    /^ng-/.test(cls) ||
    // ⚠ Not a class at all, but a fragment of one. Arbitrary values may contain commas —
    // `transition-[color,background-color,outline]` — and xUI writes some of them with a space
    // after the comma. Tailwind reads the whole bracket as a single utility and emits a single
    // rule; splitting the attribute on whitespace, as this check and a browser both do, chops that
    // utility into pieces that were never classes.
    //
    // ⚠ Only the *pieces* are excused, never a whole bracket utility. A fragment gives itself away
    // by unbalanced brackets or a trailing comma, so `transition-[color,`, `background-color,` and
    // `outline]` are skipped while `[&_svg]:size-3` — balanced, complete, and a real utility that
    // needs a real rule — is still checked. Excusing every token containing a bracket would have
    // left arbitrary-value utilities unguarded, which is most of what xUI's icon sizing uses.
    isBracketFragment(cls);

  const missing = [...used].filter((cls) => !defined.has(cls) && !ruleless(cls)).sort();

  assert.deepEqual(
    missing,
    [],
    `${missing.length} class(es) on the rendered page have no rule in the emitted stylesheet, so ` +
      `the browser drops them silently:\n    ${missing.join('\n    ')}\n\n  ` +
      `The usual cause is a Tailwind @source glob that does not cover the markup using them — ` +
      `see apps/portal/src/styles.css.`,
  );
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
