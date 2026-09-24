/**
 * The performance budget, as a build gate.
 *
 * docs/plan/20 § Performance budget is explicit that these are "Enforced in CI, failing the build":
 *
 *   Initial JS (shell, gzipped)   < 250 KB
 *   Route chunk                   < 120 KB
 *   Chart library (ECharts)       < 180 KB — deferred, one chunk, added with the cost page (#41)
 *
 * ⚠ Angular's own `budgets` in angular.json are a first line of defence but not sufficient on their
 * own: they measure the initial set and named bundles, and their "estimated transfer size" is a
 * separate number from the gzip a CDN actually serves. This script gzips the emitted files and
 * compares the real bytes, so the number in the report is the number on the wire.
 *
 * ⚠ The route-chunk budget is the enforcement point for docs/plan/20 § Performance budget's
 * "Route-level code splitting is mandatory" — with 100 resource types, a generated form renderer
 * that pulled every schema into one chunk would blow the 120 KB ceiling long before it blew the
 * 250 KB initial one. A chunk over budget is the symptom this catches.
 *
 * ⚠ The chart library is the one lazy chunk measured against its own ceiling, and it is found by what
 * it contains rather than by name: `_echarts_instance_` is the attribute ECharts stamps on every
 * chart's element, so it is in the library and in nothing else. ECharts' core and canvas renderer
 * alone are 129.3 KB here (measured 2026-09-24), so no ECharts build fits the route ceiling; the
 * cost page renders its totals and table without it and the chart fills in when it arrives —
 * docs/plan/20 § Performance budget. ⚠ Exactly one chunk may match. Two would mean the library was
 * split or copied into a route chunk, and the route ceiling is the one that must then hold.
 *
 * ⚠ The marker alone can't tell the library's own chunk from a route chunk the library was merged
 * into. A page that imported `echarts-engine` statically would put ECharts in its route chunk, that
 * chunk would be the only one carrying the marker, and it would be measured against the chart
 * ceiling instead of the route one. So a chunk counts as the library only when it also carries no
 * Angular definition: `ɵcmp`, `ɵdir`, `ɵpipe`, `ɵfac` and `ɵprov` are property names the compiler
 * writes on every component, directive, pipe and injectable, and a minifier doesn't rename a property.
 * A marked chunk that has one is a route chunk with ECharts in it, measured against the route ceiling
 * and failed by name.
 */

import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const KB = 1024;

/** docs/plan/20 § Performance budget. */
const BUDGET = {
  initialJsGzip: 250 * KB,
  routeChunkGzip: 120 * KB,
  chartLibraryGzip: 180 * KB
};

/** What only the ECharts chunk contains — the attribute it stamps on a chart's element. */
const CHART_LIBRARY_MARKER = '_echarts_instance_';

/** What Angular's compiler writes on what it defines, and the library's own chunk never holds. */
const ANGULAR_DEFINITIONS = ['ɵcmp', 'ɵdir', 'ɵpipe', 'ɵfac', 'ɵprov'];

const here = dirname(fileURLToPath(import.meta.url));
const browserDir = join(here, '..', 'dist', 'portal', 'browser');

if (!existsSync(browserDir)) {
  console.error(`✗ No build output at ${browserDir}. Run \`pnpm build\` first.`);
  process.exit(1);
}

const gzipOf = file => gzipSync(readFileSync(join(browserDir, file)), { level: 9 }).length;
const fmt = bytes => `${(bytes / KB).toFixed(1)} KB`;

/**
 * The initial set is what the browser fetches before it can paint: every `<script src>` plus every
 * `<link rel="modulepreload">` the builder wrote into the entry document. Reading it out of the
 * HTML rather than guessing filenames means a change to the builder's chunking strategy cannot
 * silently move a bundle out of the measured set.
 */
function initialScripts() {
  const entry = ['index.csr.html', 'index.html'].map(f => join(browserDir, f)).find(existsSync);

  if (!entry) {
    console.error('✗ Neither index.csr.html nor index.html was emitted; cannot determine the initial set.');
    process.exit(1);
  }

  const html = readFileSync(entry, 'utf8');
  const found = new Set();

  for (const m of html.matchAll(/<script[^>]+src="([^"]+\.js)"/g)) found.add(basename(m[1]));
  for (const m of html.matchAll(/<link[^>]+rel="modulepreload"[^>]+href="([^"]+\.js)"/g)) found.add(basename(m[1]));
  for (const m of html.matchAll(/<link[^>]+href="([^"]+\.js)"[^>]+rel="modulepreload"/g)) found.add(basename(m[1]));

  return found;
}

const initial = initialScripts();
const allJs = readdirSync(browserDir).filter(f => f.endsWith('.js'));
const source = f => readFileSync(join(browserDir, f), 'utf8');
const marked = allJs.filter(f => !initial.has(f) && source(f).includes(CHART_LIBRARY_MARKER));
const mergedIntoRoutes = marked.filter(f => ANGULAR_DEFINITIONS.some(d => source(f).includes(d)));
const chartLibrary = marked.filter(f => !mergedIntoRoutes.includes(f));
const lazy = allJs.filter(f => !initial.has(f) && !(chartLibrary.length === 1 && chartLibrary[0] === f));

const initialTotal = [...initial].reduce((sum, f) => sum + gzipOf(f), 0);
const failures = [];

console.log('\nPerformance budget — docs/plan/20 § Performance budget\n');
console.log('  Initial JS (gzipped)');

for (const f of [...initial].sort()) console.log(`    ${f.padEnd(44)} ${fmt(gzipOf(f)).padStart(10)}`);

const initialVerdict = initialTotal < BUDGET.initialJsGzip ? 'PASS' : 'FAIL';
console.log(
  `    ${'TOTAL'.padEnd(44)} ${fmt(initialTotal).padStart(10)}  / ${fmt(BUDGET.initialJsGzip)}  ${initialVerdict}`
);

if (initialTotal >= BUDGET.initialJsGzip) {
  failures.push(`initial JS is ${fmt(initialTotal)} gzipped, over the ${fmt(BUDGET.initialJsGzip)} budget`);
}

console.log('\n  Route chunks (gzipped)');

if (lazy.length === 0) {
  // Not a soft warning. docs/plan/20 § Performance budget calls route-level code splitting
  // "mandatory"; a build that produced no lazy chunk at all has either lost its lazy routes or
  // inlined them into the initial bundle, and both are the failure this gate exists to catch.
  failures.push('the build emitted no lazy chunks — route-level code splitting is mandatory');
  console.log('    (none — see failure below)');
}

for (const f of lazy.sort()) {
  const size = gzipOf(f);
  const verdict = size < BUDGET.routeChunkGzip ? 'pass' : 'FAIL';
  console.log(`    ${f.padEnd(44)} ${fmt(size).padStart(10)}  / ${fmt(BUDGET.routeChunkGzip)}  ${verdict}`);

  if (size >= BUDGET.routeChunkGzip) {
    failures.push(`route chunk ${f} is ${fmt(size)} gzipped, over the ${fmt(BUDGET.routeChunkGzip)} budget`);
  }
}

console.log('\n  Chart library (gzipped)');

for (const f of mergedIntoRoutes) {
  failures.push(
    `the chart library is in route chunk ${f}, beside Angular code; it must be a chunk of its own, reached through import()`
  );
}

if (chartLibrary.length > 1) {
  failures.push(
    `the chart library is in ${chartLibrary.length} chunks (${chartLibrary.join(', ')}); it must be one, loaded by import()`
  );
}

if (initial.size > 0 && allJs.some(f => initial.has(f) && source(f).includes(CHART_LIBRARY_MARKER))) {
  failures.push('the chart library is in the initial set; it must only be reached through import()');
}

if (chartLibrary.length === 0 && mergedIntoRoutes.length === 0) {
  console.log('    (none — no page draws a chart)');
} else if (chartLibrary.length === 0) {
  console.log('    (none of its own — see failure below)');
} else if (chartLibrary.length === 1) {
  const size = gzipOf(chartLibrary[0]);
  const verdict = size < BUDGET.chartLibraryGzip ? 'pass' : 'FAIL';
  console.log(
    `    ${chartLibrary[0].padEnd(44)} ${fmt(size).padStart(10)}  / ${fmt(BUDGET.chartLibraryGzip)}  ${verdict}`
  );

  if (size >= BUDGET.chartLibraryGzip) {
    failures.push(
      `the chart library ${chartLibrary[0]} is ${fmt(size)} gzipped, over the ${fmt(BUDGET.chartLibraryGzip)} budget`
    );
  }
}

if (failures.length > 0) {
  console.error('\n✗ Performance budget exceeded:');
  for (const f of failures) console.error(`    ${f}`);
  console.error('');
  process.exit(1);
}

console.log('\n✓ Within budget.\n');
