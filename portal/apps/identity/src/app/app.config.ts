import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideClientHydration, withEventReplay } from '@angular/platform-browser';
import { appRoutes } from './app.routes';

/**
 * The browser application config.
 *
 * ⚠ `provideZonelessChangeDetection()` and no `zone.js`, matching the portal — docs/plan/20 § Live
 * updates. zone.js is not in the workspace's dependency tree, so this is not a setting that can be
 * quietly reverted; reverting it would not compile.
 *
 * ⚠ **No interceptor, and that absence is the point.** The portal's `accessTokenInterceptor`
 * attaches a bearer token to every outgoing request. This origin has no token — it is where one is
 * *obtained* — and its credential is the `HttpOnly` session cookie the browser attaches by itself.
 * An interceptor here would be the first step toward a token on the cookie origin, which
 * docs/plan/11 § Hosts separates the two hosts to prevent.
 *
 * ⚠ `withEventReplay()` matters more here than in the portal. A user who lands on a cold sign-in
 * page and starts typing their address before hydration finishes would otherwise lose the
 * keystrokes and the submit — on the one page where losing input means the user assumes their
 * password was wrong.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(appRoutes, withComponentInputBinding()),
    provideHttpClient(withFetch()),
    provideClientHydration(withEventReplay()),
  ],
};
