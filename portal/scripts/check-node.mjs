/**
 * The Node version gate.
 *
 * The pin is Node 24 (Active LTS) — docs/plan/02 § Platform baseline settles it and
 * portal/README.md § Node carries the four inputs. This script is the half of that decision that
 * has teeth.
 *
 * Three modes, deliberately different:
 *
 *   default    warn, before the work. A developer whose shell is on another Node still gets a
 *              working build; the message tells them to `nvm use` / `fnm use`.
 *   --strict   fail, before the work. CI runs this first (`pnpm node:gate`), so a CI image that
 *              drifts off the pin stops the build instead of quietly producing artefacts nobody
 *              can reproduce.
 *   --recap    warn, *after* the work, and exit 0 either way. Same fact, different moment.
 *
 * ── ⚠ WHY --recap EXISTS: THE NUDGE WAS TESTED AND IT LOST ────────────────────────────────────
 *
 * `.npmrc` argues that "the gate is the wall, the warning is the nudge", and the wall half of that
 * is still right: a refused install on a host where only the wrong Node is available stops all
 * work over a version that builds fine. But the nudge half was measured, and it failed. A whole
 * session of portal work — a layout fix, a class-coverage gate, two SSR suites, 73 jest tests —
 * was gathered on Node 26 and reported as if it were the pinned runtime. The warning had fired.
 * It fired once, at the top, minutes and thousands of lines of `ng build` output before the
 * numbers it applied to, and by the time anyone read a figure the warning was off-screen.
 *
 * So the fix is not a louder wall, it is a warning that survives to where the numbers are. The
 * failure was never "someone built on the wrong Node" — that is fine and deliberately allowed.
 * The failure was "someone recorded a figure without knowing which runtime produced it". --recap
 * runs last in `pnpm test` and `pnpm build`, so the runtime is the final thing on screen, next to
 * the test count and the bundle sizes it qualifies.
 *
 * It exits 0 on purpose. Making it exit non-zero would be the local wall this project rejected,
 * and on a host with only Node 26 installed it would mean the portal cannot be built at all.
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pinned = readFileSync(join(here, '..', '.nvmrc'), 'utf8').trim();

const strict = process.argv.includes('--strict');
const recap = process.argv.includes('--recap');
const actual = process.versions.node;
const actualMajor = Number(actual.split('.')[0]);
const pinnedMajor = Number(pinned.split('.')[0]);

if (actualMajor === pinnedMajor) {
  console.log(
    recap
      ? `node ${actual} matches the pin (${pinned}) — the figures above are CI's runtime.`
      : `node ${actual} matches the pin (${pinned}).`,
  );
  process.exit(0);
}

const where =
  `  The pin lives in portal/.nvmrc, portal/.node-version and portal/package.json engines.\n` +
  `  Run \`nvm use\` or \`fnm use\` from portal/ to switch.`;

if (recap) {
  // ⚠ A rule, not a box-drawing flourish. This is the last thing printed by `pnpm test` and
  // `pnpm build`, and it has to be findable by someone scrolling back past a build log.
  const rule = '═'.repeat(78);

  console.warn(
    `\n${rule}\n` +
      `  ⚠ MEASURED ON NODE ${actual}, NOT THE PINNED ${pinned}.\n\n` +
      `  Every number above — test counts, bundle sizes, SSR results — came from an\n` +
      `  unpinned runtime. This run is not a reproduction of CI, and \`pnpm node:gate\`,\n` +
      `  which CI runs first, fails on this Node.\n\n` +
      `  Do not record these figures without saying which Node produced them.\n` +
      `${where}\n` +
      `${rule}\n`,
  );

  process.exit(0);
}

const message =
  `Node ${actual} does not match the portal's pin of ${pinned}.\n` +
  `${where}`;

if (strict) {
  console.error(`\n  ✗ ${message}\n`);
  process.exit(1);
}

console.warn(
  `\n  ⚠ ${message}\n` +
    `  Continuing — this is a warning locally and a hard failure in CI. Anything this run\n` +
    `  measures is a Node ${actualMajor} figure; \`pnpm node:recap\` says so again at the end.\n`,
);
