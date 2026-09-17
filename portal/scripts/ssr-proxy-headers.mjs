/**
 * ⚠ **The proxy-header half of both SSR gates.**
 *
 * `@angular/ssr` 22.1 decides the request's scheme, host, port and path prefix from `Forwarded`
 * and `X-Forwarded-*` — but only the ones `trustProxyHeaders` names. It strips the rest and prints
 * `Received "x-forwarded-for" header but "trustProxyHeaders" was not set up to allow it` for each
 * one, which behind an ingress is a line per request. Both `server.ts` files answer with the same
 * policy: trust nothing unless `NG_TRUST_PROXY_HEADERS` names it, and remove everything else at
 * the process's edge so the engine never has cause to warn. This file is what
 * `ssr-isolation.test.mjs` and `ssr-identity.test.mjs` share to pin that policy against the built
 * bundle; they import it rather than each other because each is a gate that runs on its own.
 *
 * Two properties, and why each needs the shape it has:
 *
 * 1. **With the variable unset, no proxy header reaches the render and the engine does not warn.**
 *    The warning is a `console.warn` inside the engine, in the same process the gate imports the
 *    bundle into, so `captureProxyHeaderWarnings` hooks `console.warn` and records the ones that
 *    match. A gate that only read the rendered bytes would pass with the middleware deleted — the
 *    engine strips the headers too — and miss the per-request line, which is the thing the policy
 *    exists to remove.
 * 2. **With the variable set, the engine gets exactly that list.** `x-forwarded-host` is the header
 *    with an observable consequence: a trusted one is checked against the allowed hosts and a
 *    stranger is refused with 400, where an untrusted one is dropped and the page renders. The
 *    variable is read once at startup, so this runs the bundle again in a child process with it
 *    set, and asks for a page as `evil.example`. A 200 there means the list never reached the
 *    engine; a warning there means the middleware and the engine are working from different lists.
 */

import { execFileSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

/** The exact warning the engine prints for a proxy header it was not told to trust. */
export const PROXY_HEADER_WARNING = /"trustProxyHeaders" was not set up to allow it/;

/**
 * Every proxy header the engine knows, each carrying a value that would be visible in the render
 * if it were honoured: the host would become the document's origin, the prefix its `<base href>`,
 * and the client address is a client identity the render must never see.
 */
export const hostileProxyHeaders = Object.freeze({
  forwarded: 'for=203.0.113.9;host=evil.example;proto=https',
  'x-forwarded-for': '203.0.113.9',
  'x-forwarded-host': 'evil.example',
  'x-forwarded-proto': 'https',
  'x-forwarded-port': '8443',
  'x-forwarded-prefix': '/evil-prefix'
});

/** The substrings that would prove one of `hostileProxyHeaders` reached the rendered document. */
export const proxyHeaderMarkers = Object.freeze(['203.0.113.9', 'evil.example', 'evil-prefix']);

/**
 * Hooks `console.warn` and records every message that is the engine's proxy-header warning.
 *
 * Other warnings still print. Call `restore()` before the summary so the gate's own output is not
 * affected; read `seen` after every request has completed.
 */
export const captureProxyHeaderWarnings = () => {
  const seen = [];
  const original = console.warn;

  console.warn = (...args) => {
    const text = args.map(String).join(' ');
    if (PROXY_HEADER_WARNING.test(text)) {
      seen.push(text);
    } else {
      original(...args);
    }
  };

  return {
    seen,
    restore: () => {
      console.warn = original;
    }
  };
};

/**
 * Boots the built bundle in a child process with `NG_TRUST_PROXY_HEADERS=x-forwarded-host`, asks
 * for `path` as `evil.example` with an untrusted `x-forwarded-for` beside it, and returns the
 * status and the proxy-header warnings that process printed.
 *
 * ⚠ The engine logs the refusal as `ERROR: Bad Request …` on stderr; that is the expected outcome
 * here, so stderr is captured rather than inherited and only shown when the child fails to run.
 */
export const renderWithTrustedForwardedHost = (serverBundle, path) => {
  const script = `
    const { reqHandler } = await import(${JSON.stringify(pathToFileURL(serverBundle).href)});
    const { createServer } = await import('node:http');

    const warnings = [];
    const warn = console.warn;
    console.warn = (...args) => {
      const text = args.map(String).join(' ');
      if (${PROXY_HEADER_WARNING.toString()}.test(text)) warnings.push(text);
      else warn(...args);
    };

    const server = createServer(reqHandler);
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const response = await fetch('http://127.0.0.1:' + server.address().port + ${JSON.stringify(path)}, {
      headers: { 'x-forwarded-host': 'evil.example', 'x-forwarded-for': '203.0.113.9' }
    });
    await response.text();
    server.close();

    process.stdout.write('\\n@@' + JSON.stringify({ status: response.status, warnings }) + '@@\\n');
  `;

  let stdout;
  try {
    stdout = execFileSync(process.execPath, ['--input-type=module', '--eval', script], {
      env: { ...process.env, NG_TRUST_PROXY_HEADERS: 'x-forwarded-host' },
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe']
    });
  } catch (error) {
    throw new Error(
      `the bundle did not come up with NG_TRUST_PROXY_HEADERS set: ${error.message}\n${error.stderr ?? ''}`
    );
  }

  const match = stdout.match(/@@(\{.*\})@@/);
  if (!match) {
    throw new Error(`the child process printed no result:\n${stdout}`);
  }

  return JSON.parse(match[1]);
};
