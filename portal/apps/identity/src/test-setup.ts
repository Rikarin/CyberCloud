// The identity app's templates are `$localize` tagged like the portal's — docs/plan/20
// § Accessibility, i18n, theming — so the runtime has to exist before any component is created.
import '@angular/localize/init';

import { setupZonelessTestEnv } from 'jest-preset-angular/setup-env/zoneless';

// Zoneless in the tests too, matching the app. A component that only updated because zone.js ran
// change detection for it would pass here and fail in the browser.
setupZonelessTestEnv();
