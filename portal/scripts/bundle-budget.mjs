/**
 * The performance budget, as a build gate.
 *
 * docs/plan/20 § Performance budget is explicit that these are "Enforced in CI, failing the build":
 *
 *   Initial JS (shell, gzipped)   < 250 KB
 *   Route chunk                   < 120 KB
 *   Chart engine (one chunk)      < 180 KB   — see CHART_ENGINE below
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
  chartEngineGzip: 180 * KB
};

/**
 * ⚠ The one lazy chunk that is measured against its own ceiling, and it is named, not sized (#41).
 *
 * `libs/charts/src/lib/echarts-engine.ts` is ECharts, tree-shaken to two chart types, two
 * components and the canvas renderer, and reached only through `import()` after a charting page
 * has painted. It cannot meet the 120 KB route ceiling at any build: measured with esbuild on
 * echarts 6.1.0, `echarts/core` plus the canvas renderer alone is 101.7 KB gzipped, a line chart
 * with a grid is 159.4 KB, and the build this portal ships is 173.6 KB. docs/plan/20 names
 * `@xui/echarts` for the portal's charts and puts a 120 KB ceiling on a route chunk, and the two
 * sentences disagree by that measurement; § Performance budget records the resolution — a second
 * row for this chunk, loaded after first content and cached across every chart page.
 *
 * It is matched by the chunk name `namedChunks` gives it (angular.json, production), so a route
 * chunk cannot hide under the larger ceiling by growing, and a build with a chart page and no
 * engine chunk — the engine inlined into a route — fails below as a route chunk over 120 KB.
 */
const CHART_ENGINE = /^echarts-engine-[A-Za-z0-9_-]+\.js$/;

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
const lazy = allJs.filter(f => !initial.has(f));

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
  const engine = CHART_ENGINE.test(f);
  const ceiling = engine ? BUDGET.chartEngineGzip : BUDGET.routeChunkGzip;
  const verdict = size < ceiling ? 'pass' : 'FAIL';
  console.log(
    `    ${f.padEnd(44)} ${fmt(size).padStart(10)}  / ${fmt(ceiling)}  ${verdict}${engine ? '  (chart engine)' : ''}`
  );

  if (size >= ceiling) {
    failures.push(
      `${engine ? 'chart engine chunk' : 'route chunk'} ${f} is ${fmt(size)} gzipped, over the ${fmt(ceiling)} budget`
    );
  }
}

if (lazy.filter(f => CHART_ENGINE.test(f)).length > 1) {
  failures.push('more than one chunk is named echarts-engine — the chart engine ceiling covers exactly one');
}

if (failures.length > 0) {
  console.error('\n✗ Performance budget exceeded:');
  for (const f of failures) console.error(`    ${f}`);
  console.error('');
  process.exit(1);
}

console.log('\n✓ Within budget.\n');
