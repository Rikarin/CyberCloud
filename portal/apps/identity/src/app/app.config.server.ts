import { ApplicationConfig, mergeApplicationConfig } from '@angular/core';
import { provideServerRendering, withRoutes } from '@angular/ssr';
import { appConfig } from './app.config';
import { serverRoutes } from './app.routes.server';

/**
 * The server-side additions to `appConfig`.
 *
 * ⚠ **This file adds no state, no credential, and no request-derived value, and that is the whole
 * requirement.** Angular SSR serializes transfer state into the rendered document, so anything
 * resolved here ships inside the HTML that goes over the wire. There is no cookie reader, no
 * resolver and no server-side `HttpClient` call, which is what makes
 * `credentials-never-leak.spec.ts`'s assertion about the rendered bytes hold by construction rather
 * than by review.
 */
const serverConfig: ApplicationConfig = {
  providers: [provideServerRendering(withRoutes(serverRoutes))],
};

export const config = mergeApplicationConfig(appConfig, serverConfig);
