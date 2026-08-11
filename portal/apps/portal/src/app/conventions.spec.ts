import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

/**
 * The conventions docs/plan/20 states as rules, enforced as tests.
 *
 * Each of these is something the plan says plainly and that is otherwise only enforced by someone
 * remembering it during review. Lint covers most of them for TypeScript; these cover the cases lint
 * cannot see — template class strings, and the shape of the route table.
 */

const workspace = join(__dirname, '..', '..', '..', '..');

function sourceFiles(dir: string, acc: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === 'dist' || entry === '.angular') continue;

    const full = join(dir, entry);
    if (statSync(full).isDirectory()) sourceFiles(full, acc);
    else if (/\.(ts|html|css)$/.test(entry) && !entry.endsWith('.spec.ts')) acc.push(full);
  }

  return acc;
}

const files = [...sourceFiles(join(workspace, 'apps')), ...sourceFiles(join(workspace, 'libs'))];

/**
 * ⚠ Every check below reads *code*, not prose.
 *
 * Without this, the suite fails on its own documentation: the files that explain why
 * `ChangeDetectorRef` and `RenderMode.Prerender` are banned necessarily name them, and a grep that
 * cannot tell an explanation from a use would force the bans to go undocumented in order to stay
 * enforced. Comments and template comments are stripped first; string literals are left alone,
 * because a banned identifier smuggled through a string is a use.
 */
const code = (file: string): string =>
  readFileSync(file, 'utf8')
    .replaceAll(/\/\*[\s\S]*?\*\//g, ' ') // block and JSDoc comments
    .replaceAll(/^\s*\/\/.*$/gm, ' ') // whole-line // comments
    .replaceAll(/<!--[\s\S]*?-->/g, ' '); // HTML template comments

describe('Portal conventions', () => {
  it('no template uses a `dark:` class — docs/plan/20 § Accessibility, i18n, theming', () => {
    // "Theming is tokens, not `dark:` classes. Light and dark both work by construction (ADR-017),
    // and a white-label per tenant is a token override rather than a fork."
    //
    // A `dark:` utility bakes a colour pair in at build time. It cannot follow a runtime theme
    // switch and it cannot follow a tenant's token override, so it silently opts out of both of the
    // properties the token layer exists to provide.
    const offenders = files
      .filter((f) => /\bdark:[a-z]/.test(code(f)))
      .map((f) => relative(workspace, f));

    expect(offenders).toEqual([]);
  });

  it('no ChangeDetectorRef anywhere in portal code — docs/plan/20 § Live updates', () => {
    // "a `ChangeDetectorRef` in portal code is a code-review failure". Lint enforces this on the
    // TypeScript AST; this is the coarse backstop that also catches it in a comment-free string,
    // an inline template or a file lint has been told to ignore.
    const offenders = files
      .filter((f) => /\bChangeDetectorRef\b/.test(code(f)))
      .map((f) => relative(workspace, f));

    expect(offenders).toEqual([]);
  });

  it('no component sets a change-detection strategy other than OnPush', () => {
    const offenders = files
      .filter((f) => f.endsWith('.ts'))
      .filter((f) => {
        const source = code(f);
        return /@Component\(/.test(source) && /ChangeDetectionStrategy\.Default/.test(source);
      })
      .map((f) => relative(workspace, f));

    expect(offenders).toEqual([]);
  });

  it('every application route is lazily loaded — docs/plan/20 § Performance budget', () => {
    // "Route-level code splitting is mandatory". An eager `component:` in the route table pulls the
    // route's whole import graph into the initial bundle, which for a resource route means the form
    // renderer and everything it reaches.
    const routes = code(join(workspace, 'apps', 'portal', 'src', 'app', 'app.routes.ts'));

    expect(routes).not.toMatch(/^\s*component:/m);
    expect(routes.match(/loadComponent:/g)?.length ?? 0).toBeGreaterThanOrEqual(4);
  });

  it('no route is prerendered — docs/plan/20 § SSR', () => {
    // "The authenticated portal is rendered per request and must never cache a rendered page across
    // users." `RenderMode.Prerender` builds one document at build time and serves it to everyone,
    // which is that leak with extra steps.
    const serverRoutes = code(join(workspace, 'apps', 'portal', 'src', 'app', 'app.routes.server.ts'));

    expect(serverRoutes).not.toMatch(/RenderMode\.Prerender/);
    expect(serverRoutes).toMatch(/RenderMode\.Server/);
  });

  it('no source file cites the plan by line number', () => {
    // `Build.Architecture`'s plan-citation gate — docs/code-documentation-style.md § Citing the
    // plan. A line number is a citation that rots on the next edit to the doc.
    const offenders = files
      .filter((f) => /docs\/plan\/\d+[A-Za-z0-9._-]*:\d+/.test(readFileSync(f, 'utf8')))
      .map((f) => relative(workspace, f));

    expect(offenders).toEqual([]);
  });
});
