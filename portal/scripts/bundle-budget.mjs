/**
 * The performance budget, as a build gate.
 *
 * docs/plan/20 § Performance budget is explicit that these are "Enforced in CI, failing the build":
 *
 *   Initial JS (shell, gzipped)   < 250 KB
 *   Route chunk                   < 120 KB
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

import { gzipSync } from 'node:zlib';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const KB = 1024;

/** docs/plan/20 § Performance budget. */
const BUDGET = {
  initialJsGzip: 250 * KB,
  routeChunkGzip: 120 * KB,
};

const here = dirname(fileURLToPath(import.meta.url));
const browserDir = join(here, '..', 'dist', 'portal', 'browser');

if (!existsSync(browserDir)) {
  console.error(`✗ No build output at ${browserDir}. Run \`pnpm build\` first.`);
  process.exit(1);
}

const gzipOf = (file) => gzipSync(readFileSync(join(browserDir, file)), { level: 9 }).length;
const fmt = (bytes) => `${(bytes / KB).toFixed(1)} KB`;

/**
 * The initial set is what the browser fetches before it can paint: every `<script src>` plus every
 * `<link rel="modulepreload">` the builder wrote into the entry document. Reading it out of the
 * HTML rather than guessing filenames means a change to the builder's chunking strategy cannot
 * silently move a bundle out of the measured set.
 */
function initialScripts() {
  const entry = ['index.csr.html', 'index.html'].map((f) => join(browserDir, f)).find(existsSync);

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
const allJs = readdirSync(browserDir).filter((f) => f.endsWith('.js'));
const lazy = allJs.filter((f) => !initial.has(f));

const initialTotal = [...initial].reduce((sum, f) => sum + gzipOf(f), 0);
const failures = [];

console.log('\nPerformance budget — docs/plan/20 § Performance budget\n');
console.log('  Initial JS (gzipped)');

for (const f of [...initial].sort()) console.log(`    ${f.padEnd(44)} ${fmt(gzipOf(f)).padStart(10)}`);

const initialVerdict = initialTotal < BUDGET.initialJsGzip ? 'PASS' : 'FAIL';
console.log(`    ${'TOTAL'.padEnd(44)} ${fmt(initialTotal).padStart(10)}  / ${fmt(BUDGET.initialJsGzip)}  ${initialVerdict}`);

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

if (failures.length > 0) {
  console.error('\n✗ Performance budget exceeded:');
  for (const f of failures) console.error(`    ${f}`);
  console.error('');
  process.exit(1);
}

console.log('\n✓ Within budget.\n');
