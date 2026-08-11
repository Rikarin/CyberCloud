import { ApplicationConfig, mergeApplicationConfig } from '@angular/core';
import { provideServerRendering, withRoutes } from '@angular/ssr';
import { appConfig } from './app.config';
import { serverRoutes } from './app.routes.server';

/**
 * The server-side additions to `appConfig`.
 *
 * ⚠ **This file adds no state and no credentials, and that is the requirement.** docs/plan/20
 * § SSR: "The SSR process holds no tokens; it renders the shell and the client hydrates with the
 * user's token."
 *
 * There is no server-side token provider, no cookie reader, and no tenant resolver here. Anything
 * that put a user's identity into the server render would also put it into the rendered HTML, and
 * rendered HTML is exactly what a CDN caches — "Getting this wrong leaks one tenant's data to
 * another through a CDN cache, which is the worst bug this document can prevent."
 *
 * The stores in `libs/shell` are per-injector, and `mergeApplicationConfig` produces a config that
 * `bootstrapApplication` instantiates fresh for every request, so two concurrent renders share
 * nothing. `ssr-isolation.server.spec.ts` asserts it rather than assuming it.
 */
const serverConfig: ApplicationConfig = {
  providers: [provideServerRendering(withRoutes(serverRoutes))],
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
