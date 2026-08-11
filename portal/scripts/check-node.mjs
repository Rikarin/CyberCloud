/**
 * The Node version gate.
 *
 * docs/plan/02 § Platform baseline left Node "unresolved" — the plan said 22 LTS, the dev host
 * runs 26.5.0, and no `@xui/*` peer range constrains it, so it is the portal's decision and it
 * "needs a `.nvmrc`/`engines` decision and a matching CI image before portal work starts, or local
 * and CI will silently differ". This script is the half of that decision that has teeth.
 *
 * The pin is Node 24 (Active LTS). The reasoning is in portal/README.md § Node.
 *
 * Two modes, deliberately different:
 *
 *   default    warn. A developer whose shell is on another Node still gets a working build; the
 *              message tells them to `nvm use` / `fnm use`.
 *   --strict   fail. CI runs this first, so a CI image that drifts off the pin stops the build
 *              instead of quietly producing artefacts nobody can reproduce.
 */

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pinned = readFileSync(join(here, '..', '.nvmrc'), 'utf8').trim();

const strict = process.argv.includes('--strict');
const actualMajor = Number(process.versions.node.split('.')[0]);
const pinnedMajor = Number(pinned.split('.')[0]);

if (actualMajor === pinnedMajor) {
  console.log(`node ${process.versions.node} matches the pin (${pinned}).`);
  process.exit(0);
}

const message =
  `Node ${process.versions.node} does not match the portal's pin of ${pinned}.\n` +
  `  The pin lives in portal/.nvmrc, portal/.node-version and portal/package.json engines.\n` +
  `  Run \`nvm use\` or \`fnm use\` from portal/ to switch.`;

if (strict) {
  console.error(`\n  ✗ ${message}\n`);
  process.exit(1);
}

console.warn(`\n  ⚠ ${message}\n  Continuing — this is a warning locally and a hard failure in CI.\n`);
