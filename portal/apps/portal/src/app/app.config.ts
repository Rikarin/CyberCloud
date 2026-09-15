import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  Injector,
  inject,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection
} from '@angular/core';
import { provideClientHydration, withEventReplay, withIncrementalHydration } from '@angular/platform-browser';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { TENANT_CONTEXT_SOURCE, TenantContextSource } from '@cybercloud/shell';
import { appRoutes } from './app.routes';
import { accessTokenInterceptor } from './auth/access-token.interceptor';

/**
 * The browser application config.
 *
 * ⚠ `provideZonelessChangeDetection()` and no `zone.js` anywhere. docs/plan/20 § Live updates:
 * "the templates are `OnPush` and zoneless. Since xUI is zoneless and signal-based, this is the
 * natural style rather than a discipline". zone.js is not in the dependency tree at all, so this is
 * not a setting that can be quietly reverted — reverting it would not compile.
 *
 * ⚠ `withFetch()` matters for SSR specifically: it is what lets `provideClientHydration` match a
 * server-side `HttpClient` request to its transfer-state entry, so the client does not re-issue
 * every request the server already made.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(
      appRoutes,
      withComponentInputBinding(),
      // A blade that reopens scrolled to where it was is the behaviour people expect from Azure's
      // portal; anchor scrolling makes a deep link to a section of a long settings blade work.
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' })
    ),
    provideHttpClient(withFetch(), withInterceptors([accessTokenInterceptor])),

    // ── docs/plan/10 § Authentication inputs ────────────────────────────────────────────────
    // The shell's sign-in flow knows the tenant from the token's `tid` and asks the app for the
    // rest: the tenant's name and its subscriptions, through the generated client and the scope
    // collections. The shell cannot import the client (`libs/shell` does not depend on
    // `apps/portal`), so this is the one provider that turns a token into a context bar.
    //
    // ⚠ Imported lazily, on the first load, and not as a `useClass`. `PlatformTenantContextSource`
    // reaches `PlatformApi`, which is the whole generated client — and a static import here would
    // put its 150 methods in the initial bundle, which `platform-api.ts` promises does not happen
    // and `scripts/bundle-budget.mjs` measures (it cost 8 KB gzipped when tried). The dynamic
    // import lands in the same chunk the first page already loads.
    { provide: TENANT_CONTEXT_SOURCE, useFactory: lazyTenantContextSource },

    // ── docs/plan/20 § SSR ──────────────────────────────────────────────────────────────────
    // Hydration rather than a re-render. The reason the doc gives is not SEO: "First paint on a
    // cold load matters when the alternative is a spinner on a 3 MB bundle" and "a link to a
    // resource blade from an alert email must render something immediately". Both of those are
    // undone by a destructive re-bootstrap that blanks the screen the server just painted.
    //
    // `withEventReplay()` closes the gap that makes SSR feel like a lie — a click on a context-bar
    // switcher between first paint and hydration is replayed rather than swallowed. In a portal
    // whose first interaction is often "switch to the right subscription", a swallowed click is
    // the user acting in the wrong place.
    provideClientHydration(withEventReplay(), withIncrementalHydration())
  ]
};

/** The provider's factory, exported so `tenant-context.source.spec.ts` can drive it as the app does. */
export function lazyTenantContextSource(): TenantContextSource {
  const injector = inject(Injector);

  return {
    load: async tenantId => {
      const { PlatformTenantContextSource } = await import('./auth/tenant-context.source');
      return injector.get(PlatformTenantContextSource).load(tenantId);
    }
  };
}
