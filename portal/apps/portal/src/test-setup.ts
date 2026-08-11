// ── docs/plan/20 § Accessibility, i18n, theming ─────────────────────────────────────────────
// "i18n from day one with `@angular/localize`." The shell's labels are `$localize` tagged
// templates, so the runtime has to be installed before any component is created — otherwise every
// component under test throws on a label rather than on the thing being tested.
import '@angular/localize/init';

import { setupZonelessTestEnv } from 'jest-preset-angular/setup-env/zoneless';

// ── docs/plan/20 § Live updates ─────────────────────────────────────────────────────────────
// "the templates are `OnPush` and zoneless". The test environment has to be zoneless too, or a
// component that only updates because zone.js ran change detection for it would pass here and
// fail in the app.
setupZonelessTestEnv();
