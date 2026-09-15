/**
 * Copies `generated/forms/{apiVersion}.json` into the portal's static assets, as `/forms/…`.
 *
 * The form renderer fetches its schema at runtime — docs/plan/20 § Performance budget: "Schemas
 * are fetched per type, cached, and versioned by the api-version" — so the document has to be
 * served, not imported. The place it will be served from is `CyberCloud.Portal.Host` (docs/plan/03
 * § `portal/`: "the API shim + static serving"), which does not exist yet; until it does, the
 * Angular build ships the document beside the bundle and `server.ts` serves it as a static file.
 *
 * ⚠ Why a copy rather than an `assets` entry pointing at `../generated/forms`: the application
 * builder refuses it —
 *
 *     An unhandled exception occurred: The ../generated/forms asset path must be within the
 *     workspace root.
 *
 * — so the file is copied to `apps/portal/public/forms/` first, which is gitignored. It runs
 * before every build and serve (`package.json`), reads the checked-in file `./build.sh Generate`
 * byte-checks, and refuses to run if that file is missing rather than shipping a portal whose
 * every create blade would 404.
 */

import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, '..', '..', 'generated', 'forms');
const target = join(here, '..', 'apps', 'portal', 'public', 'forms');

if (!existsSync(source)) {
  console.error(
    `✗ ${source} does not exist. Run ./build.sh Generate first; the portal cannot render a form it cannot fetch.`
  );
  process.exit(1);
}

const documents = readdirSync(source).filter(f => f.endsWith('.json'));

if (documents.length === 0) {
  console.error(`✗ ${source} holds no form document.`);
  process.exit(1);
}

rmSync(target, { recursive: true, force: true });
mkdirSync(target, { recursive: true });

for (const file of documents) copyFileSync(join(source, file), join(target, file));

console.log(`Forms: ${documents.length} document(s) staged at /forms — ${documents.join(', ')}`);
